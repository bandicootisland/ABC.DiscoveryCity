using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Diagnostics;

namespace ABC.BookCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HathiLibraryController : ControllerBase
{
    private readonly string _connectionString;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HathiLibraryController> _logger;

    private const int MaxPageSize = 500;
    private const int SearchTotalCountCap = 50_000;

    public HathiLibraryController(IConfiguration configuration, IMemoryCache cache, ILogger<HathiLibraryController> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";

        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Gets a list of HathiTrust catalog records with server-side paging.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedHathiResult>> GetBooks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] bool publicOnly = true,
        [FromQuery] string? lang = null,
        [FromQuery] string? rights = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortField = null,
        [FromQuery] string? sortDir = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 1;
            if (pageSize > MaxPageSize) pageSize = MaxPageSize;

            var offset = (page - 1) * pageSize;
            var items = new List<HathiCatalogItem>();

            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            // Build WHERE clause
            var whereClause = "WHERE 1=1";
            var orderClause = "ORDER BY htid";
            search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            var hasSearch = !string.IsNullOrWhiteSpace(search);
            var isExactHtid = hasSearch && LooksLikeHtid(search!);
            var useFulltext = hasSearch && !isExactHtid && search!.Length >= 3;

            rights = string.IsNullOrWhiteSpace(rights) ? null : rights.Trim();

            // Sorting (applied in-memory when cached, else ignored except for default DB ordering).
            sortField = string.IsNullOrWhiteSpace(sortField) ? null : sortField.Trim();
            sortDir = string.IsNullOrWhiteSpace(sortDir) ? "asc" : sortDir.Trim().ToLowerInvariant();
            var sortDescending = sortDir == "desc";

            // Prevent pathological LIKE scans on 19M rows.
            // We only support <3 chars as exact HTID matching.
            if (hasSearch && !isExactHtid && search!.Length < 3)
            {
                return Ok(new PagedHathiResult
                {
                    Items = new List<HathiCatalogItem>(),
                    TotalCount = 0,
                    Page = page,
                    PageSize = pageSize
                });
            }
            
            if (publicOnly)
                whereClause += " AND access = 'allow'";
            if (!string.IsNullOrWhiteSpace(lang))
                whereClause += " AND lang = @lang";
            if (!string.IsNullOrWhiteSpace(rights))
                whereClause += " AND rights = @rights";
            if (isExactHtid)
            {
                whereClause += " AND htid = @htid";
                orderClause = "ORDER BY htid";
            }
            else if (useFulltext)
            {
                // Use fulltext search for 3+ character queries (fast)
                whereClause += " AND MATCH(title, author) AGAINST(@search IN BOOLEAN MODE)";
                orderClause = "ORDER BY MATCH(title, author) AGAINST(@search IN BOOLEAN MODE) DESC";
            }
            else if (hasSearch)
            {
                // Fallback LIKE for >=3 chars only. Prefer prefix match on indexed HTID.
                whereClause += " AND (htid LIKE @searchPrefix OR title LIKE @searchLike OR author LIKE @searchLike)";
            }

            // If the result set is reasonably bounded (search and/or filters), cache it and serve paging+sorting in-memory.
            var shouldCacheResultSet = hasSearch || !string.IsNullOrWhiteSpace(lang) || !string.IsNullOrWhiteSpace(rights);
            if (shouldCacheResultSet)
            {
                var baseKey = BuildCacheKey(publicOnly, lang, rights, isExactHtid ? null : (useFulltext ? BuildBooleanPrefixQuery(search ?? "") : search), isExactHtid ? (search ?? "") : null);
                var baseKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseKey))).Substring(0, 12);

                if (!_cache.TryGetValue(baseKey, out List<HathiCatalogItem>? cachedList))
                {
                    _logger.LogInformation("Hathi cache MISS {KeyHash}. Loading result set from DB...", baseKeyHash);
                    var sw = Stopwatch.StartNew();
                    cachedList = await LoadResultSetAsync(
                        connection,
                        whereClause,
                        orderClause,
                        lang,
                        rights,
                        search,
                        isExactHtid,
                        useFulltext,
                        cancellationToken);
                    sw.Stop();

                    _logger.LogInformation(
                        "Hathi cache FILL {KeyHash}. Cached {Count} items in {ElapsedMs}ms.",
                        baseKeyHash,
                        cachedList.Count,
                        sw.ElapsedMilliseconds);

                    var cacheOptions = new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromMinutes(5),
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15)
                    };
                    _cache.Set(baseKey, cachedList, cacheOptions);
                }
                else
                {
                    _logger.LogInformation(
                        "Hathi cache HIT {KeyHash}. Using cached result set. page={Page} pageSize={PageSize} sortField={SortField} sortDir={SortDir}",
                        baseKeyHash,
                        page,
                        pageSize,
                        sortField,
                        sortDir);
                }

                cachedList ??= new List<HathiCatalogItem>();

                var sorted = ApplySort(cachedList, sortField, sortDescending);
                var pageItems = sorted.Skip(offset).Take(pageSize).ToList();

                return Ok(new PagedHathiResult
                {
                    Items = pageItems,
                    TotalCount = cachedList.Count,
                    Page = page,
                    PageSize = pageSize
                });
            }

            // Get total count
            var countSql = $"SELECT COUNT(*) FROM hathi_catalog {whereClause}";
            if (hasSearch)
            {
                // Counting all matches for broad queries can be very slow.
                // Cap totals so the grid remains responsive and encourages narrowing queries.
                countSql = $"SELECT COUNT(*) FROM (SELECT 1 FROM hathi_catalog {whereClause} LIMIT @cap) t";
            }
            long totalCount;
            await using (var countCmd = new MySqlCommand(countSql, connection))
            {
                if (!string.IsNullOrWhiteSpace(lang))
                    countCmd.Parameters.AddWithValue("@lang", lang);
                if (!string.IsNullOrWhiteSpace(rights))
                    countCmd.Parameters.AddWithValue("@rights", rights);

                if (hasSearch)
                    countCmd.Parameters.AddWithValue("@cap", SearchTotalCountCap);

                if (isExactHtid)
                {
                    countCmd.Parameters.AddWithValue("@htid", search);
                }
                else if (useFulltext)
                {
                    countCmd.Parameters.AddWithValue("@search", BuildBooleanPrefixQuery(search!));
                }
                else if (hasSearch)
                {
                    countCmd.Parameters.AddWithValue("@searchLike", $"%{search}%");
                    countCmd.Parameters.AddWithValue("@searchPrefix", $"{search}%");
                }

                countCmd.CommandTimeout = 180;
                totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
            }

            // Get page data
            var sql = $@"
                SELECT htid, access, rights, ht_bib_key, description, source, source_bib_num,
                       oclc_num, isbn, issn, lccn, title, imprint, rights_reason_code,
                       rights_timestamp, us_gov_doc_flag, rights_date_used, pub_place, lang,
                       bib_fmt, collection_code, content_provider_code, responsible_entity_code,
                       digitization_agent_code, access_profile_code, author
                FROM hathi_catalog
                {whereClause}
                {orderClause} LIMIT @limit OFFSET @offset";

            await using var cmd = new MySqlCommand(sql, connection);
            if (!string.IsNullOrWhiteSpace(lang))
                cmd.Parameters.AddWithValue("@lang", lang);
            if (!string.IsNullOrWhiteSpace(rights))
                cmd.Parameters.AddWithValue("@rights", rights);

            if (isExactHtid)
            {
                cmd.Parameters.AddWithValue("@htid", search);
            }
            else if (useFulltext)
            {
                cmd.Parameters.AddWithValue("@search", BuildBooleanPrefixQuery(search!));
            }
            else if (hasSearch)
            {
                cmd.Parameters.AddWithValue("@searchLike", $"%{search}%");
                cmd.Parameters.AddWithValue("@searchPrefix", $"{search}%");
            }
            cmd.Parameters.AddWithValue("@limit", pageSize);
            cmd.Parameters.AddWithValue("@offset", offset);
            cmd.CommandTimeout = 180;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(MapToItem(reader));
            }

            return Ok(new PagedHathiResult
            {
                Items = items,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static bool LooksLikeHtid(string value)
    {
        // Typical: "mdp.39015012345678" or "uc1.31175035185815".
        // Keep this permissive; we only use it to avoid LIKE scans for tiny inputs.
        if (value.Length < 6 || value.Length > 64) return false;
        if (value.Contains(' ') || value.Contains('"') || value.Contains('\'')) return false;
        return value.Contains('.') && Regex.IsMatch(value, @"^[A-Za-z0-9_.:-]+$");
    }

    private static string BuildBooleanPrefixQuery(string raw)
    {
        // Convert a user search like "shakespeare tragedy" into boolean prefix query:
        // "+shakespeare* +tragedy*".
        // Removes problematic punctuation for stability and performance.
        var cleaned = Regex.Replace(raw, @"[^\p{L}\p{Nd}\s]", " ");
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "";

        var sb = new StringBuilder();
        var used = 0;
        foreach (var part in parts)
        {
            if (part.Length < 2) continue;
            if (used >= 6) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('+');
            sb.Append(part);
            sb.Append('*');
            used++;
        }

        return sb.Length == 0 ? "" : sb.ToString();
    }

    private static string BuildCacheKey(bool publicOnly, string? lang, string? rights, string? searchKey, string? exactHtid)
    {
        return string.Join('|',
            "hathi:v1",
            publicOnly ? "pub" : "all",
            lang ?? "",
            rights ?? "",
            exactHtid != null ? $"htid={exactHtid}" : "",
            searchKey ?? "");
    }

    private static IEnumerable<HathiCatalogItem> ApplySort(IEnumerable<HathiCatalogItem> source, string? sortField, bool desc)
    {
        if (string.IsNullOrWhiteSpace(sortField))
            return source;

        // Allow-list fields we know the grid can sort by.
        return (sortField, desc) switch
        {
            (nameof(HathiCatalogItem.Title), false) => source.OrderBy(x => x.Title),
            (nameof(HathiCatalogItem.Title), true) => source.OrderByDescending(x => x.Title),

            (nameof(HathiCatalogItem.Author), false) => source.OrderBy(x => x.Author),
            (nameof(HathiCatalogItem.Author), true) => source.OrderByDescending(x => x.Author),

            (nameof(HathiCatalogItem.Lang), false) => source.OrderBy(x => x.Lang),
            (nameof(HathiCatalogItem.Lang), true) => source.OrderByDescending(x => x.Lang),

            (nameof(HathiCatalogItem.Rights), false) => source.OrderBy(x => x.Rights),
            (nameof(HathiCatalogItem.Rights), true) => source.OrderByDescending(x => x.Rights),

            (nameof(HathiCatalogItem.RightsDateUsed), false) => source.OrderBy(x => x.RightsDateUsed),
            (nameof(HathiCatalogItem.RightsDateUsed), true) => source.OrderByDescending(x => x.RightsDateUsed),

            (nameof(HathiCatalogItem.Access), false) => source.OrderBy(x => x.Access),
            (nameof(HathiCatalogItem.Access), true) => source.OrderByDescending(x => x.Access),

            (nameof(HathiCatalogItem.Htid), false) => source.OrderBy(x => x.Htid),
            (nameof(HathiCatalogItem.Htid), true) => source.OrderByDescending(x => x.Htid),

            _ => source
        };
    }

    private static async Task<List<HathiCatalogItem>> LoadResultSetAsync(
        MySqlConnection connection,
        string whereClause,
        string orderClause,
        string? lang,
        string? rights,
        string? search,
        bool isExactHtid,
        bool useFulltext,
        CancellationToken cancellationToken)
    {
        var items = new List<HathiCatalogItem>(Math.Min(SearchTotalCountCap, 10_000));

        var sql = $@"
                SELECT htid, access, rights, ht_bib_key, description, source, source_bib_num,
                       oclc_num, isbn, issn, lccn, title, imprint, rights_reason_code,
                       rights_timestamp, us_gov_doc_flag, rights_date_used, pub_place, lang,
                       bib_fmt, collection_code, content_provider_code, responsible_entity_code,
                       digitization_agent_code, access_profile_code, author
                FROM hathi_catalog
                {whereClause}
                {orderClause} LIMIT @cap";

        await using var cmd = new MySqlCommand(sql, connection);
        if (!string.IsNullOrWhiteSpace(lang))
            cmd.Parameters.AddWithValue("@lang", lang);
        if (!string.IsNullOrWhiteSpace(rights))
            cmd.Parameters.AddWithValue("@rights", rights);

        if (isExactHtid)
        {
            cmd.Parameters.AddWithValue("@htid", search);
        }
        else if (useFulltext)
        {
            cmd.Parameters.AddWithValue("@search", BuildBooleanPrefixQuery(search ?? string.Empty));
        }
        else if (!string.IsNullOrWhiteSpace(search))
        {
            cmd.Parameters.AddWithValue("@searchLike", $"%{search}%");
            cmd.Parameters.AddWithValue("@searchPrefix", $"{search}%");
        }

        cmd.Parameters.AddWithValue("@cap", SearchTotalCountCap);
        cmd.CommandTimeout = 180;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(MapToItem(reader));

        return items;
    }

    /// <summary>
    /// Gets summary statistics for the catalog (uses table stats for speed).
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<HathiCatalogSummary>> GetSummary()
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var summary = new HathiCatalogSummary();

            // Use table statistics for fast approximate count
            await using (var cmd = new MySqlCommand(@"
                SELECT TABLE_ROWS 
                FROM information_schema.TABLES 
                WHERE TABLE_SCHEMA = 'allthethings' AND TABLE_NAME = 'hathi_catalog'", connection))
            {
                var result = await cmd.ExecuteScalarAsync();
                summary.TotalRecords = result != null ? Convert.ToInt64(result) : 0;
            }

            // Estimate public/restricted based on sample (fast)
            await using (var cmd = new MySqlCommand(@"
                SELECT access, COUNT(*) as cnt 
                FROM (SELECT access FROM hathi_catalog LIMIT 10000) sample 
                GROUP BY access", connection))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                long allowSample = 0, denySample = 0, totalSample = 0;
                while (await reader.ReadAsync())
                {
                    var access = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    var cnt = reader.GetInt64(1);
                    totalSample += cnt;
                    if (access == "allow") allowSample = cnt;
                    else if (access == "deny") denySample = cnt;
                }
                if (totalSample > 0)
                {
                    summary.PublicAccess = summary.TotalRecords * allowSample / totalSample;
                    summary.RestrictedAccess = summary.TotalRecords * denySample / totalSample;
                }
            }

            // Languages count from index (reasonably fast)
            summary.UniqueLanguages = 100; // Placeholder - actual count is slow

            // Estimate ISBN/OCLC from sample
            await using (var cmd = new MySqlCommand(@"
                SELECT 
                    SUM(CASE WHEN isbn IS NOT NULL AND isbn != '' THEN 1 ELSE 0 END) as with_isbn,
                    SUM(CASE WHEN oclc_num IS NOT NULL AND oclc_num != '' THEN 1 ELSE 0 END) as with_oclc,
                    COUNT(*) as total
                FROM (SELECT isbn, oclc_num FROM hathi_catalog LIMIT 10000) sample", connection))
            {
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var isbnSample = reader.GetInt64(0);
                    var oclcSample = reader.GetInt64(1);
                    var totalSample = reader.GetInt64(2);
                    if (totalSample > 0)
                    {
                        summary.WithIsbn = summary.TotalRecords * isbnSample / totalSample;
                        summary.WithOclc = summary.TotalRecords * oclcSample / totalSample;
                    }
                }
            }

            return Ok(summary);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets a specific book by HTID.
    /// </summary>
    [HttpGet("{htid}")]
    public async Task<ActionResult<HathiCatalogItem>> GetBook(string htid)
    {
        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                SELECT htid, access, rights, ht_bib_key, description, source, source_bib_num,
                       oclc_num, isbn, issn, lccn, title, imprint, rights_reason_code,
                       rights_timestamp, us_gov_doc_flag, rights_date_used, pub_place, lang,
                       bib_fmt, collection_code, content_provider_code, responsible_entity_code,
                       digitization_agent_code, access_profile_code, author
                FROM hathi_catalog
                WHERE htid = @htid";

            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@htid", htid);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Ok(MapToItem(reader));
            }

            return NotFound(new { error = "Book not found" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static HathiCatalogItem MapToItem(MySqlDataReader reader)
    {
        return new HathiCatalogItem
        {
            Htid = reader.GetString("htid"),
            Access = reader.IsDBNull(reader.GetOrdinal("access")) ? null : reader.GetString("access"),
            Rights = reader.IsDBNull(reader.GetOrdinal("rights")) ? null : reader.GetString("rights"),
            HtBibKey = reader.IsDBNull(reader.GetOrdinal("ht_bib_key")) ? null : reader.GetInt64("ht_bib_key"),
            Description = reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString("description"),
            Source = reader.IsDBNull(reader.GetOrdinal("source")) ? null : reader.GetString("source"),
            SourceBibNum = reader.IsDBNull(reader.GetOrdinal("source_bib_num")) ? null : reader.GetString("source_bib_num"),
            OclcNum = reader.IsDBNull(reader.GetOrdinal("oclc_num")) ? null : reader.GetString("oclc_num"),
            Isbn = reader.IsDBNull(reader.GetOrdinal("isbn")) ? null : reader.GetString("isbn"),
            Issn = reader.IsDBNull(reader.GetOrdinal("issn")) ? null : reader.GetString("issn"),
            Lccn = reader.IsDBNull(reader.GetOrdinal("lccn")) ? null : reader.GetString("lccn"),
            Title = reader.IsDBNull(reader.GetOrdinal("title")) ? null : reader.GetString("title"),
            Imprint = reader.IsDBNull(reader.GetOrdinal("imprint")) ? null : reader.GetString("imprint"),
            RightsReasonCode = reader.IsDBNull(reader.GetOrdinal("rights_reason_code")) ? null : reader.GetString("rights_reason_code"),
            RightsTimestamp = reader.IsDBNull(reader.GetOrdinal("rights_timestamp")) ? null : reader.GetDateTime("rights_timestamp"),
            UsGovDocFlag = reader.IsDBNull(reader.GetOrdinal("us_gov_doc_flag")) ? null : reader.GetBoolean("us_gov_doc_flag"),
            RightsDateUsed = reader.IsDBNull(reader.GetOrdinal("rights_date_used")) ? null : reader.GetString("rights_date_used"),
            PubPlace = reader.IsDBNull(reader.GetOrdinal("pub_place")) ? null : reader.GetString("pub_place"),
            Lang = reader.IsDBNull(reader.GetOrdinal("lang")) ? null : reader.GetString("lang"),
            BibFmt = reader.IsDBNull(reader.GetOrdinal("bib_fmt")) ? null : reader.GetString("bib_fmt"),
            CollectionCode = reader.IsDBNull(reader.GetOrdinal("collection_code")) ? null : reader.GetString("collection_code"),
            ContentProviderCode = reader.IsDBNull(reader.GetOrdinal("content_provider_code")) ? null : reader.GetString("content_provider_code"),
            ResponsibleEntityCode = reader.IsDBNull(reader.GetOrdinal("responsible_entity_code")) ? null : reader.GetString("responsible_entity_code"),
            DigitizationAgentCode = reader.IsDBNull(reader.GetOrdinal("digitization_agent_code")) ? null : reader.GetString("digitization_agent_code"),
            AccessProfileCode = reader.IsDBNull(reader.GetOrdinal("access_profile_code")) ? null : reader.GetString("access_profile_code"),
            Author = reader.IsDBNull(reader.GetOrdinal("author")) ? null : reader.GetString("author")
        };
    }

    // DTOs
    public class HathiCatalogItem
    {
        public string Htid { get; set; } = "";
        public string? Access { get; set; }
        public string? Rights { get; set; }
        public long? HtBibKey { get; set; }
        public string? Description { get; set; }
        public string? Source { get; set; }
        public string? SourceBibNum { get; set; }
        public string? OclcNum { get; set; }
        public string? Isbn { get; set; }
        public string? Issn { get; set; }
        public string? Lccn { get; set; }
        public string? Title { get; set; }
        public string? Imprint { get; set; }
        public string? RightsReasonCode { get; set; }
        public DateTime? RightsTimestamp { get; set; }
        public bool? UsGovDocFlag { get; set; }
        public string? RightsDateUsed { get; set; }
        public string? PubPlace { get; set; }
        public string? Lang { get; set; }
        public string? BibFmt { get; set; }
        public string? CollectionCode { get; set; }
        public string? ContentProviderCode { get; set; }
        public string? ResponsibleEntityCode { get; set; }
        public string? DigitizationAgentCode { get; set; }
        public string? AccessProfileCode { get; set; }
        public string? Author { get; set; }
    }

    public class HathiCatalogSummary
    {
        public long TotalRecords { get; set; }
        public long PublicAccess { get; set; }
        public long RestrictedAccess { get; set; }
        public int UniqueLanguages { get; set; }
        public long WithIsbn { get; set; }
        public long WithOclc { get; set; }
    }

    public class PagedHathiResult
    {
        public List<HathiCatalogItem> Items { get; set; } = new();
        public long TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
    }
}
