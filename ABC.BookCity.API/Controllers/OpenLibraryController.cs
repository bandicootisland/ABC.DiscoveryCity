using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;

namespace ABC.BookCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OpenLibraryController : ControllerBase
{
    private readonly string _connectionString;
    private readonly ILogger<OpenLibraryController> _logger;
    private readonly IMemoryCache _cache;
    private readonly ElasticsearchClient _elastic;

    private const int MaxPageSize = 500;
    private const int DefaultPageSize = 50;

    public OpenLibraryController(IConfiguration configuration, IMemoryCache cache, ILogger<OpenLibraryController> logger, ElasticsearchClient elastic)
    {
        _connectionString = configuration.GetConnectionString("MariaDb") 
            ?? "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
        _cache = cache;
        _logger = logger;
        _elastic = elastic;
    }

    /// <summary>
    /// Search OpenLibrary editions, works, or authors
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedOpenLibraryResult>> Search(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? type = "edition", // edition, work, author
        [FromQuery] string? search = null,
        [FromQuery] string? sortField = null,
        [FromQuery] string? sortDir = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        var olType = type?.ToLowerInvariant() switch
        {
            "work" => "/type/work",
            "author" => "/type/author",
            _ => "/type/edition"
        };

        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var offset = (page - 1) * pageSize;

        // Use Elasticsearch for editions if search is provided or if we want fast browsing
        if (olType == "/type/edition")
        {
            try
            {
                var searchResponse = await _elastic.SearchAsync<OpenLibraryItem>(s => s
                    .Index("ol_editions")
                    .From(offset)
                    .Size(pageSize)
                    .Query(q => 
                    {
                        if (string.IsNullOrEmpty(search))
                        {
                            q.MatchAll();
                        }
                        else
                        {
                            q.MultiMatch(mm => mm
                                .Query(search)
                                .Fields(new[] { "title", "isbn10", "isbn13", "publishers", "subjects" })
                                .Fuzziness(new Fuzziness("AUTO"))
                            );
                        }
                    })
                , cancellationToken);

                if (searchResponse.IsValidResponse)
                {
                    return Ok(new PagedOpenLibraryResult
                    {
                        Items = searchResponse.Documents.ToList(),
                        TotalCount = searchResponse.Total,
                        Page = page,
                        PageSize = pageSize
                    });
                }
                _logger.LogWarning("Elasticsearch search failed, falling back to MariaDB: {Error}", searchResponse.DebugInformation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Elasticsearch search exception, falling back to MariaDB");
            }
        }

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Build query
            var whereClause = "WHERE type = @type";
            var parameters = new List<MySqlParameter> { new("@type", olType) };

            if (!string.IsNullOrEmpty(search))
            {
                // Search in JSON for title or name fields
                whereClause += " AND (json LIKE @search)";
                parameters.Add(new("@search", $"%{search}%"));
            }

            // Get total count (cached)
            var countCacheKey = $"ol:count:{olType}:{search ?? ""}";
            long totalCount;
            
            if (!_cache.TryGetValue(countCacheKey, out totalCount))
            {
                var countSql = $"SELECT COUNT(*) FROM ol_base {whereClause}";
                await using var countCmd = new MySqlCommand(countSql, connection);
                foreach (var p in parameters) countCmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                
                totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(cancellationToken));
                _cache.Set(countCacheKey, totalCount, TimeSpan.FromMinutes(5));
            }

            // Get items
            var orderBy = "ORDER BY ol_key";
            if (!string.IsNullOrEmpty(sortField))
            {
                var dir = sortDir?.ToLowerInvariant() == "desc" ? "DESC" : "ASC";
                orderBy = sortField.ToLowerInvariant() switch
                {
                    "key" or "ol_key" => $"ORDER BY ol_key {dir}",
                    "revision" => $"ORDER BY revision {dir}",
                    "last_modified" => $"ORDER BY last_modified {dir}",
                    _ => "ORDER BY ol_key"
                };
            }

            var sql = $@"
                SELECT ol_key, type, revision, last_modified, json
                FROM ol_base
                {whereClause}
                {orderBy}
                LIMIT @limit OFFSET @offset";

            await using var cmd = new MySqlCommand(sql, connection);
            foreach (var p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
            cmd.Parameters.AddWithValue("@limit", pageSize);
            cmd.Parameters.AddWithValue("@offset", offset);

            var items = new List<OpenLibraryItem>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var item = ParseOpenLibraryItem(reader);
                items.Add(item);
            }

            return Ok(new PagedOpenLibraryResult
            {
                Items = items,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenLibrary search failed");
            return StatusCode(500, $"Database error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get a single OpenLibrary record by key
    /// </summary>
    [HttpGet("{*olKey}")]
    public async Task<ActionResult<OpenLibraryItem>> GetByKey(string olKey, CancellationToken cancellationToken = default)
    {
        // Reconstruct the key (URL encoding may have stripped the leading /)
        if (!olKey.StartsWith("/")) olKey = "/" + olKey;

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = "SELECT ol_key, type, revision, last_modified, json FROM ol_base WHERE ol_key = @key";
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@key", olKey);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return Ok(ParseOpenLibraryItem(reader));
            }

            return NotFound($"Record not found: {olKey}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenLibrary get by key failed: {Key}", olKey);
            return StatusCode(500, $"Database error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get summary statistics
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<OpenLibrarySummary>> GetSummary(CancellationToken cancellationToken = default)
    {
        const string cacheKey = "ol:summary";
        if (_cache.TryGetValue(cacheKey, out OpenLibrarySummary? cached) && cached != null)
        {
            return Ok(cached);
        }

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = @"
                SELECT type, COUNT(*) as cnt 
                FROM ol_base 
                WHERE type IN ('/type/edition', '/type/work', '/type/author')
                GROUP BY type";

            await using var cmd = new MySqlCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var summary = new OpenLibrarySummary();
            while (await reader.ReadAsync(cancellationToken))
            {
                var t = reader.GetString(0);
                var c = reader.GetInt64(1);
                switch (t)
                {
                    case "/type/edition": summary.TotalEditions = c; break;
                    case "/type/work": summary.TotalWorks = c; break;
                    case "/type/author": summary.TotalAuthors = c; break;
                }
            }

            _cache.Set(cacheKey, summary, TimeSpan.FromMinutes(10));
            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenLibrary summary failed");
            return StatusCode(500, $"Database error: {ex.Message}");
        }
    }

    /// <summary>
    /// Search for download/access links for an edition
    /// </summary>
    [HttpGet("links/{*olKey}")]
    public async Task<ActionResult<OpenLibraryLinks>> GetLinks(string olKey, CancellationToken cancellationToken = default)
    {
        if (!olKey.StartsWith("/")) olKey = "/" + olKey;

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            var sql = "SELECT json FROM ol_base WHERE ol_key = @key";
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.Parameters.AddWithValue("@key", olKey);

            var jsonStr = await cmd.ExecuteScalarAsync(cancellationToken) as string;
            if (string.IsNullOrEmpty(jsonStr))
            {
                return NotFound();
            }

            var links = new OpenLibraryLinks { OlKey = olKey };

            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            // Extract ISBNs for potential lookups
            if (root.TryGetProperty("isbn_10", out var isbn10) && isbn10.ValueKind == JsonValueKind.Array)
            {
                links.Isbn10 = isbn10.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToList()!;
            }
            if (root.TryGetProperty("isbn_13", out var isbn13) && isbn13.ValueKind == JsonValueKind.Array)
            {
                links.Isbn13 = isbn13.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToList()!;
            }

            // OCLC numbers
            if (root.TryGetProperty("oclc_numbers", out var oclc) && oclc.ValueKind == JsonValueKind.Array)
            {
                links.OclcNumbers = oclc.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToList()!;
            }

            // Internet Archive IDs
            if (root.TryGetProperty("ocaid", out var ocaid))
            {
                links.InternetArchiveId = ocaid.GetString();
            }

            // Covers
            if (root.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array)
            {
                links.CoverIds = covers.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Number)
                    .Select(x => x.GetInt64())
                    .ToList();
            }

            // Build URLs
            if (!string.IsNullOrEmpty(links.InternetArchiveId))
            {
                links.InternetArchiveUrl = $"https://archive.org/details/{links.InternetArchiveId}";
                links.InternetArchiveReadUrl = $"https://archive.org/stream/{links.InternetArchiveId}";
                links.InternetArchiveDownloadUrl = $"https://archive.org/download/{links.InternetArchiveId}";
            }

            links.OpenLibraryUrl = $"https://openlibrary.org{olKey}";

            if (links.CoverIds?.Any() == true)
            {
                links.CoverUrlSmall = $"https://covers.openlibrary.org/b/id/{links.CoverIds.First()}-S.jpg";
                links.CoverUrlMedium = $"https://covers.openlibrary.org/b/id/{links.CoverIds.First()}-M.jpg";
                links.CoverUrlLarge = $"https://covers.openlibrary.org/b/id/{links.CoverIds.First()}-L.jpg";
            }

            // Check if borrowable on Open Library
            links.BorrowUrl = $"https://openlibrary.org{olKey}?mode=all";

            return Ok(links);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OpenLibrary links lookup failed: {Key}", olKey);
            return StatusCode(500, $"Error: {ex.Message}");
        }
    }

    private static OpenLibraryItem ParseOpenLibraryItem(MySqlDataReader reader)
    {
        var olKey = reader.GetString(0);
        var type = reader.GetString(1);
        var revision = reader.GetInt32(2);
        var lastModified = reader.GetDateTime(3);
        var jsonStr = reader.GetString(4);

        var item = new OpenLibraryItem
        {
            OlKey = olKey,
            Type = type,
            Revision = revision,
            LastModified = lastModified,
            RawJson = jsonStr
        };

        // Parse key fields from JSON
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            // Title (for editions and works)
            if (root.TryGetProperty("title", out var title))
            {
                item.Title = title.GetString();
            }

            // Name (for authors)
            if (root.TryGetProperty("name", out var name))
            {
                item.Name = name.GetString();
            }

            // Publishers
            if (root.TryGetProperty("publishers", out var publishers) && publishers.ValueKind == JsonValueKind.Array)
            {
                item.Publishers = publishers.EnumerateArray().Select(p => p.GetString()).Where(p => p != null).ToList()!;
            }

            // Publish date
            if (root.TryGetProperty("publish_date", out var pubDate))
            {
                item.PublishDate = pubDate.GetString();
            }

            // ISBNs
            if (root.TryGetProperty("isbn_10", out var isbn10) && isbn10.ValueKind == JsonValueKind.Array)
            {
                item.Isbn10 = isbn10.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToList()!;
            }
            if (root.TryGetProperty("isbn_13", out var isbn13) && isbn13.ValueKind == JsonValueKind.Array)
            {
                item.Isbn13 = isbn13.EnumerateArray().Select(x => x.GetString()).Where(x => x != null).ToList()!;
            }

            // Covers
            if (root.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array)
            {
                var firstCover = covers.EnumerateArray().FirstOrDefault();
                if (firstCover.ValueKind == JsonValueKind.Number)
                {
                    item.CoverId = firstCover.GetInt64().ToString();
                }
            }

            // Subjects (for works)
            if (root.TryGetProperty("subjects", out var subjects) && subjects.ValueKind == JsonValueKind.Array)
            {
                item.Subjects = subjects.EnumerateArray()
                    .Take(10) // Limit to 10
                    .Select(s => s.GetString())
                    .Where(s => s != null)
                    .ToList()!;
            }

            // Authors (linked)
            if (root.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
            {
                item.AuthorKeys = new List<string>();
                foreach (var authorEl in authors.EnumerateArray())
                {
                    // Edition format: {"key": "/authors/OL123A"}
                    if (authorEl.TryGetProperty("key", out var authorKey))
                    {
                        item.AuthorKeys.Add(authorKey.GetString()!);
                    }
                    // Work format: {"author": {"key": "/authors/OL123A"}}
                    else if (authorEl.TryGetProperty("author", out var authorObj) && 
                             authorObj.TryGetProperty("key", out var authorKey2))
                    {
                        item.AuthorKeys.Add(authorKey2.GetString()!);
                    }
                }
            }

            // Works (for editions)
            if (root.TryGetProperty("works", out var works) && works.ValueKind == JsonValueKind.Array)
            {
                var firstWork = works.EnumerateArray().FirstOrDefault();
                if (firstWork.TryGetProperty("key", out var workKey))
                {
                    item.WorkKey = workKey.GetString();
                }
            }

            // Internet Archive ID
            if (root.TryGetProperty("ocaid", out var ocaid))
            {
                item.InternetArchiveId = ocaid.GetString();
            }

            // Number of pages
            if (root.TryGetProperty("number_of_pages", out var pages) && pages.ValueKind == JsonValueKind.Number)
            {
                item.NumberOfPages = pages.GetInt32();
            }

            // Physical format
            if (root.TryGetProperty("physical_format", out var format))
            {
                item.PhysicalFormat = format.GetString();
            }

            // Birth/death dates for authors
            if (root.TryGetProperty("birth_date", out var birthDate))
            {
                item.BirthDate = birthDate.GetString();
            }
            if (root.TryGetProperty("death_date", out var deathDate))
            {
                item.DeathDate = deathDate.GetString();
            }

            // Bio for authors
            if (root.TryGetProperty("bio", out var bio))
            {
                if (bio.ValueKind == JsonValueKind.String)
                {
                    item.Bio = bio.GetString();
                }
                else if (bio.TryGetProperty("value", out var bioValue))
                {
                    item.Bio = bioValue.GetString();
                }
            }
        }
        catch
        {
            // JSON parsing failed, keep raw fields only
        }

        return item;
    }
}

// DTOs
public class OpenLibraryItem
{
    [System.Text.Json.Serialization.JsonPropertyName("olKey")]
    public string OlKey { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "/type/edition";

    [System.Text.Json.Serialization.JsonPropertyName("revision")]
    public int Revision { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("lastModified")]
    public DateTime LastModified { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("rawJson")]
    public string RawJson { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string? Title { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; } // For authors

    [System.Text.Json.Serialization.JsonPropertyName("publishers")]
    public List<string>? Publishers { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("publishDate")]
    public string? PublishDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("isbn10")]
    public List<string>? Isbn10 { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("isbn13")]
    public List<string>? Isbn13 { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("coverId")]
    public string? CoverId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("subjects")]
    public List<string>? Subjects { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("authorKeys")]
    public List<string>? AuthorKeys { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("workKey")]
    public string? WorkKey { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("iaId")]
    public string? InternetArchiveId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("numberOfPages")]
    public int? NumberOfPages { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("physicalFormat")]
    public string? PhysicalFormat { get; set; }

    // Author-specific
    [System.Text.Json.Serialization.JsonPropertyName("birthDate")]
    public string? BirthDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("deathDate")]
    public string? DeathDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bio")]
    public string? Bio { get; set; }

    // Computed
    public string? CoverUrlMedium => !string.IsNullOrEmpty(CoverId) 
        ? $"https://covers.openlibrary.org/b/id/{CoverId}-M.jpg" 
        : null;

    public string? OpenLibraryUrl => $"https://openlibrary.org{OlKey}";

    public string? InternetArchiveUrl => !string.IsNullOrEmpty(InternetArchiveId)
        ? $"https://archive.org/details/{InternetArchiveId}"
        : null;
}

public class PagedOpenLibraryResult
{
    public List<OpenLibraryItem> Items { get; set; } = new();
    public long TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class OpenLibrarySummary
{
    public long TotalEditions { get; set; }
    public long TotalWorks { get; set; }
    public long TotalAuthors { get; set; }
    public long Total => TotalEditions + TotalWorks + TotalAuthors;
}

public class OpenLibraryLinks
{
    public string OlKey { get; set; } = "";
    public List<string>? Isbn10 { get; set; }
    public List<string>? Isbn13 { get; set; }
    public List<string>? OclcNumbers { get; set; }
    public string? InternetArchiveId { get; set; }
    public List<long>? CoverIds { get; set; }

    // URLs
    public string? OpenLibraryUrl { get; set; }
    public string? InternetArchiveUrl { get; set; }
    public string? InternetArchiveReadUrl { get; set; }
    public string? InternetArchiveDownloadUrl { get; set; }
    public string? BorrowUrl { get; set; }
    public string? CoverUrlSmall { get; set; }
    public string? CoverUrlMedium { get; set; }
    public string? CoverUrlLarge { get; set; }
}
