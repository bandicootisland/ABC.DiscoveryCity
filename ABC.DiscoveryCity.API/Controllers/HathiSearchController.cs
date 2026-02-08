using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Linq.Expressions;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HathiSearchController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HathiSearchController> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _elasticUrl;
    private readonly string _indexName;
    private readonly int _fastFromSizeWindow;

    private const int MaxPageSize = 500;
    private const int MaxResultWindow = 10_000; // Elasticsearch default index.max_result_window
    private const int DeepPageStepSize = 50_000;
    private const int DefaultFastFromSizeWindow = 500_000;

    public HathiSearchController(IConfiguration configuration, IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<HathiSearchController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;

        _elasticUrl = configuration["Elasticsearch:Url"]?.TrimEnd('/') ?? "http://localhost:9200";
        _indexName = configuration["Elasticsearch:Index"]?.Trim() ?? "hathi_catalog";

        _fastFromSizeWindow = configuration.GetValue<int?>("Elasticsearch:FastFromSizeWindow") ?? DefaultFastFromSizeWindow;
        if (_fastFromSizeWindow < MaxResultWindow)
        {
            _fastFromSizeWindow = MaxResultWindow;
        }
    }

    [HttpGet]
    public async Task<ActionResult<PagedHathiResult>> Search(
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
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > MaxPageSize) pageSize = MaxPageSize;

        search = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        lang = string.IsNullOrWhiteSpace(lang) ? null : lang.Trim();
        rights = string.IsNullOrWhiteSpace(rights) ? null : rights.Trim();

        sortField = string.IsNullOrWhiteSpace(sortField) ? null : sortField.Trim();
        sortDir = string.IsNullOrWhiteSpace(sortDir) ? "asc" : sortDir.Trim().ToLowerInvariant();
        var sortDescending = sortDir == "desc";

        var isExactHtid = !string.IsNullOrWhiteSpace(search) && LooksLikeHtid(search);

        var offset = (page - 1) * pageSize;
        var requiredWindow = offset + pageSize;

        // Cache exact totals per base query (paging within the same query shouldn't recompute track_total_hits).
        var totalCacheKey = BuildTotalCacheKey(publicOnly, lang, rights, search, isExactHtid);
        var hasCachedTotal = _cache.TryGetValue(totalCacheKey, out long cachedTotal);

        // Prefer a single from/size request up to a reasonable window.
        // Important: ES requires (from + size) <= index.max_result_window.
        // Page 4001 @ size 50 needs 200,050 which is why 200,000 was a cliff.
        if (requiredWindow > MaxResultWindow && requiredWindow <= _fastFromSizeWindow)
        {
            await EnsureMaxResultWindowAsync(_fastFromSizeWindow, cancellationToken);
        }

        var trackTotalHits = !hasCachedTotal;

        // Elasticsearch's default max_result_window prevents deep paging with from/size.
        // To keep the grid's page-jump input working, we fall back to search_after when needed.
        if (requiredWindow > _fastFromSizeWindow)
        {
            var deep = await DeepPageAsync(
                desiredOffset: offset,
                pageSize: pageSize,
                publicOnly: publicOnly,
                lang: lang,
                rights: rights,
                search: search,
                isExactHtid: isExactHtid,
                sortField: sortField,
                sortDescending: sortDescending,
                totalCacheKey: totalCacheKey,
                cachedTotal: hasCachedTotal ? cachedTotal : null,
                cancellationToken: cancellationToken);

            deep.Page = page;
            deep.PageSize = pageSize;
            return Ok(deep);
        }

        var body = BuildSearchRequestBody(
            from: offset,
            size: pageSize,
            publicOnly: publicOnly,
            lang: lang,
            rights: rights,
            search: search,
            isExactHtid: isExactHtid,
            sortField: sortField,
            sortDescending: sortDescending,
            searchAfter: null,
            includeSource: true,
            trackTotalHits: trackTotalHits);

        var http = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_elasticUrl}/{_indexName}/_search?filter_path=hits.total.value,hits.hits._source,hits.hits.sort")
        {
            Content = JsonContent.Create(body)
        };

        string payload;
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, cancellationToken);
            payload = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Elasticsearch request failed");
            return StatusCode(503, "Elasticsearch unavailable");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elasticsearch request failed unexpectedly");
            return StatusCode(500, "Search service error");
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Elasticsearch search failed: {Status} {Body}", resp.StatusCode, payload.Length > 400 ? payload[..400] : payload);
            return StatusCode((int)resp.StatusCode, payload);
        }

        var parsed = ParseSearchResponseWithSort(payload, includeItems: true);
        var result = new PagedHathiResult
        {
            Items = parsed.Items,
            TotalCount = trackTotalHits ? parsed.TotalCount : cachedTotal
        };

        if (trackTotalHits)
        {
            _cache.Set(totalCacheKey, result.TotalCount, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(10),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });
        }
        result.Page = page;
        result.PageSize = pageSize;
        return Ok(result);
    }

    private async Task EnsureMaxResultWindowAsync(int desiredWindow, CancellationToken cancellationToken)
    {
        var key = $"es:mrw:{_indexName}:{desiredWindow}";
        if (_cache.TryGetValue(key, out _))
        {
            return;
        }

        try
        {
            var http = _httpClientFactory.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Put, $"{_elasticUrl}/{_indexName}/_settings")
            {
                Content = JsonContent.Create(new
                {
                    index = new
                    {
                        max_result_window = desiredWindow
                    }
                })
            };

            using var resp = await http.SendAsync(req, cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                _cache.Set(key, true, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromHours(6),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
                });
            }
            else
            {
                var payload = await resp.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to set index.max_result_window={Window} for {Index}: {Status} {Body}", desiredWindow, _indexName, resp.StatusCode, payload.Length > 300 ? payload[..300] : payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set index.max_result_window for {Index}", _indexName);
        }
    }

    private static string BuildTotalCacheKey(bool publicOnly, string? lang, string? rights, string? search, bool isExactHtid)
    {
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? "" : search.Trim();
        var baseKey = $"publicOnly={publicOnly}|lang={lang ?? ""}|rights={rights ?? ""}|search={normalizedSearch}|exactHtid={isExactHtid}";
        return "hathi:es:total:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(baseKey))).Substring(0, 16);
    }

    [HttpGet("summary")]
    public async Task<ActionResult<HathiCatalogSummary>> Summary(CancellationToken cancellationToken = default)
    {
        var body = new
        {
            size = 0,
            track_total_hits = true,
            aggs = new
            {
                public_access = new { filter = new { term = new { access = "allow" } } },
                unique_langs = new { cardinality = new { field = "lang" } }
            }
        };

        var http = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_elasticUrl}/{_indexName}/_search")
        {
            Content = JsonContent.Create(body)
        };

        string payload;
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, cancellationToken);
            payload = await resp.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Elasticsearch summary request failed");
            return StatusCode(503, "Elasticsearch unavailable");
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elasticsearch summary request failed unexpectedly");
            return StatusCode(500, "Search service error");
        }

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Elasticsearch summary failed: {Status} {Body}", resp.StatusCode, payload.Length > 400 ? payload[..400] : payload);
            return StatusCode((int)resp.StatusCode, payload);
        }

        return Ok(ParseSummaryResponse(payload));
    }

    [HttpGet("{htid}")]
    public async Task<ActionResult<HathiCatalogItem>> GetByHtid(string htid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(htid)) return BadRequest();

        var http = _httpClientFactory.CreateClient();
        using var resp = await http.GetAsync($"{_elasticUrl}/{_indexName}/_doc/{Uri.EscapeDataString(htid)}", cancellationToken);
        var payload = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            return StatusCode((int)resp.StatusCode, payload);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("_source", out var src))
            {
                return NotFound();
            }

            var es = JsonSerializer.Deserialize<EsHathiDocSource>(src.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var item = es == null ? null : MapToItem(es);
            return item == null ? NotFound() : Ok(item);
        }
        catch
        {
            return StatusCode(500, "Failed to parse Elasticsearch document response");
        }
    }

    private static object BuildSearchRequestBody(
        int from,
        int size,
        bool publicOnly,
        string? lang,
        string? rights,
        string? search,
        bool isExactHtid,
        string? sortField,
        bool sortDescending,
        object?[]? searchAfter,
        bool includeSource,
        bool trackTotalHits)
    {
        var filters = new List<object>();
        if (publicOnly)
        {
            filters.Add(new { term = new { access = "allow" } });
        }
        if (!string.IsNullOrWhiteSpace(lang))
        {
            filters.Add(new { term = new { lang } });
        }
        if (!string.IsNullOrWhiteSpace(rights))
        {
            filters.Add(new { term = new { rights } });
        }

        object query;
        if (string.IsNullOrWhiteSpace(search))
        {
            query = new { match_all = new { } };
        }
        else if (isExactHtid)
        {
            query = new { term = new { htid = search } };
        }
        else
        {
            query = new
            {
                multi_match = new
                {
                    query = search,
                    fields = new[] { "title", "author" },
                    fuzziness = "AUTO",
                    @operator = "and"
                }
            };
        }

        // Sort allow-list (avoid sorting on analyzed text fields).
        var sort = BuildSort(sortField, sortDescending, hasSearch: !string.IsNullOrWhiteSpace(search));

        if (searchAfter != null)
        {
            if (!includeSource)
            {
                // For deep paging, we frequently only need the sort values (search_after) and total, not full documents.
                // Disabling _source drastically reduces payload and deserialization costs.
                return new
                {
                    track_total_hits = trackTotalHits,
                    from = 0,
                    size,
                    query = new { @bool = new { must = new[] { query }, filter = filters } },
                    sort,
                    search_after = searchAfter,
                    _source = false
                };
            }

            return new
            {
                track_total_hits = trackTotalHits,
                from = 0,
                size,
                query = new { @bool = new { must = new[] { query }, filter = filters } },
                sort,
                search_after = searchAfter
            };
        }

        if (!includeSource)
        {
            return new
            {
                track_total_hits = trackTotalHits,
                from,
                size,
                query = new { @bool = new { must = new[] { query }, filter = filters } },
                sort,
                _source = false
            };
        }

        return new
        {
            track_total_hits = trackTotalHits,
            from,
            size,
            query = new { @bool = new { must = new[] { query }, filter = filters } },
            sort
        };
    }

    private static object[] BuildSort(string? sortField, bool desc, bool hasSearch)
    {
        var dir = desc ? "desc" : "asc";

        if (string.IsNullOrWhiteSpace(sortField))
        {
            // When searching, let ES rank by relevance. When browsing, sort by HTID.
            return hasSearch
                ? new object[] { new { _score = new { order = "desc" } }, new { htid = new { order = "asc" } } }
                : new object[] { new { htid = new { order = "asc" } } };
        }

        // Telerik sends property names like "Title", "Author", "Lang"...
        // Only allow fields that are mapped as keyword/number in our index.
        return sortField switch
        {
            // IMPORTANT for search_after: always include a deterministic tie-breaker.
            // Without this, equal values on the primary sort can lead to unstable paging.
            "Htid" => new object[] { new { htid = new { order = dir } } },
            "Title" => new object[] { new { title_sort = new { order = dir } }, new { htid = new { order = "asc" } } },
            "Author" => new object[] { new { author_sort = new { order = dir } }, new { htid = new { order = "asc" } } },
            "Access" => new object[] { new { access = new { order = dir } }, new { htid = new { order = "asc" } } },
            "Rights" => new object[] { new { rights = new { order = dir } }, new { htid = new { order = "asc" } } },
            "Lang" => new object[] { new { lang = new { order = dir } }, new { htid = new { order = "asc" } } },
            "RightsDateUsed" => new object[] { new { rights_date_used = new { order = dir } }, new { htid = new { order = "asc" } } },
            "HtBibKey" => new object[] { new { ht_bib_key = new { order = dir } }, new { htid = new { order = "asc" } } },
            "Source" => new object[] { new { source = new { order = dir } }, new { htid = new { order = "asc" } } },
            _ => hasSearch
                ? new object[] { new { _score = new { order = "desc" } }, new { htid = new { order = "asc" } } }
                : new object[] { new { htid = new { order = "asc" } } }
        };
    }

    private sealed record ParsedSearchResponse(List<HathiCatalogItem> Items, long TotalCount, object?[]? LastSort);

    private static ParsedSearchResponse ParseSearchResponseWithSort(string json, bool includeItems)
    {
        using var doc = JsonDocument.Parse(json);

        var hits = doc.RootElement.GetProperty("hits");
        var total = 0L;
        if (hits.TryGetProperty("total", out var totalObj) && totalObj.TryGetProperty("value", out var valueEl))
        {
            total = valueEl.GetInt64();
        }

        var items = new List<HathiCatalogItem>();
        object?[]? lastSort = null;

        foreach (var hit in hits.GetProperty("hits").EnumerateArray())
        {
            if (hit.TryGetProperty("sort", out var sortArray) && sortArray.ValueKind == JsonValueKind.Array)
            {
                lastSort = sortArray.EnumerateArray().Select(JsonElementToClr).ToArray();
            }

            if (!includeItems)
            {
                continue;
            }

            if (!hit.TryGetProperty("_source", out var src)) continue;

            var es = JsonSerializer.Deserialize<EsHathiDocSource>(src.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var item = es == null ? null : MapToItem(es);
            if (item != null) items.Add(item);
        }

        return new ParsedSearchResponse(items, total, lastSort);
    }

    private static object? JsonElementToClr(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.GetRawText()
        };
    }

    private async Task<PagedHathiResult> DeepPageAsync(
        int desiredOffset,
        int pageSize,
        bool publicOnly,
        string? lang,
        string? rights,
        string? search,
        bool isExactHtid,
        string? sortField,
        bool sortDescending,
        string totalCacheKey,
        long? cachedTotal,
        CancellationToken cancellationToken)
    {
        // search_after is fast for sequential paging, but jumping to a deep page requires walking forward.
        // The critical optimization: do NOT fetch _source while walking; only fetch _source for the final page.
        var remainingToSkip = desiredOffset;
        object?[]? searchAfter = null;

        // 1) Walk forward in large chunks returning ONLY sort values (no _source).
        // Use cached totals when available; otherwise get total once.
        var totalKnown = cachedTotal.HasValue;
        long total = cachedTotal ?? 0;

        // Walk forward in large steps (no _source) to avoid thousands of round-trips.
        while (remainingToSkip > 0)
        {
            var stepSize = Math.Min(DeepPageStepSize, remainingToSkip);

            var step = await ExecuteSearchAsync(
                from: 0,
                size: stepSize,
                publicOnly: publicOnly,
                lang: lang,
                rights: rights,
                search: search,
                isExactHtid: isExactHtid,
                sortField: sortField,
                sortDescending: sortDescending,
                searchAfter: searchAfter,
                includeSource: false,
                trackTotalHits: !totalKnown,
                cancellationToken: cancellationToken);

            if (!totalKnown)
            {
                total = step.TotalCount;
                totalKnown = true;

                _cache.Set(totalCacheKey, total, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = TimeSpan.FromMinutes(10),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                });
            }

            if (step.LastSort == null)
            {
                return new PagedHathiResult { Items = new List<HathiCatalogItem>(), TotalCount = total };
            }

            remainingToSkip -= stepSize;
            searchAfter = step.LastSort;
        }

        // 3) Fetch only the final page items with _source enabled.
        var final = await ExecuteSearchAsync(
            from: 0,
            size: pageSize,
            publicOnly: publicOnly,
            lang: lang,
            rights: rights,
            search: search,
            isExactHtid: isExactHtid,
            sortField: sortField,
            sortDescending: sortDescending,
            searchAfter: searchAfter,
            includeSource: true,
            trackTotalHits: !totalKnown,
            cancellationToken: cancellationToken);

        if (!totalKnown)
        {
            total = final.TotalCount;
            totalKnown = true;

            _cache.Set(totalCacheKey, total, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(10),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });
        }

        return new PagedHathiResult { Items = final.Items, TotalCount = total };
    }

    private async Task<ParsedSearchResponse> ExecuteSearchAsync(
        int from,
        int size,
        bool publicOnly,
        string? lang,
        string? rights,
        string? search,
        bool isExactHtid,
        string? sortField,
        bool sortDescending,
        object?[]? searchAfter,
        bool includeSource,
        bool trackTotalHits,
        CancellationToken cancellationToken)
    {
        var body = BuildSearchRequestBody(
            from: from,
            size: size,
            publicOnly: publicOnly,
            lang: lang,
            rights: rights,
            search: search,
            isExactHtid: isExactHtid,
            sortField: sortField,
            sortDescending: sortDescending,
            searchAfter: searchAfter,
            includeSource: includeSource,
            trackTotalHits: trackTotalHits);

        var http = _httpClientFactory.CreateClient();

        var url = includeSource
            ? $"{_elasticUrl}/{_indexName}/_search?filter_path=hits.total.value,hits.hits._source,hits.hits.sort"
            : $"{_elasticUrl}/{_indexName}/_search?filter_path=hits.total.value,hits.hits.sort";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        
        string payload = "";
        try
        {
            using var resp = await http.SendAsync(req, cancellationToken);
            payload = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Elasticsearch search failed: {Status} {Body}", resp.StatusCode, payload.Length > 400 ? payload[..400] : payload);
                return new ParsedSearchResponse(new List<HathiCatalogItem>(), 0, null);
            }
            return ParseSearchResponseWithSort(payload, includeItems: includeSource);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Elasticsearch search failed: "+ ex.Message);
        }
        return new ParsedSearchResponse(new List<HathiCatalogItem>(), 0, null);
    }

    private static HathiCatalogItem MapToItem(EsHathiDocSource src) => new()
    {
        Htid = src.Htid ?? "",
        Access = src.Access,
        Rights = src.Rights,
        HtBibKey = src.HtBibKey,
        Description = src.Description,
        Source = src.Source,
        SourceBibNum = src.SourceBibNum,
        OclcNum = src.OclcNum,
        Isbn = src.Isbn,
        Issn = src.Issn,
        Lccn = src.Lccn,
        Title = src.Title,
        Imprint = src.Imprint,
        RightsReasonCode = src.RightsReasonCode,
        RightsTimestamp = src.RightsTimestamp,
        UsGovDocFlag = src.UsGovDocFlag,
        RightsDateUsed = src.RightsDateUsed,
        PubPlace = src.PubPlace,
        Lang = src.Lang,
        BibFmt = src.BibFmt,
        CollectionCode = src.CollectionCode,
        ContentProviderCode = src.ContentProviderCode,
        ResponsibleEntityCode = src.ResponsibleEntityCode,
        DigitizationAgentCode = src.DigitizationAgentCode,
        AccessProfileCode = src.AccessProfileCode,
        Author = src.Author
    };

    private sealed class EsHathiDocSource
    {
        [JsonPropertyName("htid")]
        public string? Htid { get; set; }

        [JsonPropertyName("access")]
        public string? Access { get; set; }

        [JsonPropertyName("rights")]
        public string? Rights { get; set; }

        [JsonPropertyName("ht_bib_key")]
        public long? HtBibKey { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("source_bib_num")]
        public string? SourceBibNum { get; set; }

        [JsonPropertyName("oclc_num")]
        public string? OclcNum { get; set; }

        [JsonPropertyName("isbn")]
        public string? Isbn { get; set; }

        [JsonPropertyName("issn")]
        public string? Issn { get; set; }

        [JsonPropertyName("lccn")]
        public string? Lccn { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("imprint")]
        public string? Imprint { get; set; }

        [JsonPropertyName("rights_reason_code")]
        public string? RightsReasonCode { get; set; }

        [JsonPropertyName("rights_timestamp")]
        public DateTime? RightsTimestamp { get; set; }

        [JsonPropertyName("us_gov_doc_flag")]
        public bool? UsGovDocFlag { get; set; }

        [JsonPropertyName("rights_date_used")]
        public string? RightsDateUsed { get; set; }

        [JsonPropertyName("pub_place")]
        public string? PubPlace { get; set; }

        [JsonPropertyName("lang")]
        public string? Lang { get; set; }

        [JsonPropertyName("bib_fmt")]
        public string? BibFmt { get; set; }

        [JsonPropertyName("collection_code")]
        public string? CollectionCode { get; set; }

        [JsonPropertyName("content_provider_code")]
        public string? ContentProviderCode { get; set; }

        [JsonPropertyName("responsible_entity_code")]
        public string? ResponsibleEntityCode { get; set; }

        [JsonPropertyName("digitization_agent_code")]
        public string? DigitizationAgentCode { get; set; }

        [JsonPropertyName("access_profile_code")]
        public string? AccessProfileCode { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }
    }

    private static HathiCatalogSummary ParseSummaryResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var hits = doc.RootElement.GetProperty("hits");
        var total = hits.GetProperty("total").GetProperty("value").GetInt64();

        var aggs = doc.RootElement.GetProperty("aggregations");
        var publicCount = aggs.GetProperty("public_access").GetProperty("doc_count").GetInt64();
        var uniqueLangs = aggs.GetProperty("unique_langs").GetProperty("value").GetInt32();

        return new HathiCatalogSummary
        {
            TotalRecords = total,
            PublicAccess = publicCount,
            RestrictedAccess = Math.Max(0, total - publicCount),
            UniqueLanguages = uniqueLangs,
            WithIsbn = 0,
            WithOclc = 0
        };
    }

    private static bool LooksLikeHtid(string input)
    {
        // Common examples: "mdp.39015012345678", "uc1.b1234567", "loc.ark:/13960/t0..."
        return Regex.IsMatch(input, "^[a-z0-9]+\\.[a-z0-9\\-:/]+$", RegexOptions.IgnoreCase);
    }

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
        [System.Text.Json.Serialization.JsonPropertyName("items")]
        public List<HathiCatalogItem> Items { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("totalCount")]
        public long TotalCount { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("page")]
        public int Page { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("pageSize")]
        public int PageSize { get; set; }
    }
}
