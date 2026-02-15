using ABC.DiscoveryCity.PostgreSQL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly DbService _dbService;
    private readonly IMemoryCache _cache;

    public SearchController(DbService dbService, IMemoryCache cache)
    {
        _dbService = dbService;
        _cache = cache;
    }

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] int limit = 0, [FromQuery] bool exactMatch = false, [FromQuery] List<string>? datasets = null, [FromQuery] List<string>? names = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Query is required.");
        }

        var datasetNames = datasets?.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var nameValues = names?.Where(s => !string.IsNullOrEmpty(s)).ToList();

        var results = exactMatch
            ? _dbService.SearchExactMatch(query, limit, datasetNames, nameValues)
            : await _dbService.SearchSimilarAsync(query, limit, datasetNames, nameValues);

        // Map to DTO
        var dtos = results.Select(MapToDto).ToList();

        return Ok(dtos);
    }

    [HttpGet("recent")]
    public IActionResult SearchRecent([FromQuery] int limit = 0, [FromQuery] List<string>? datasets = null, [FromQuery] List<string>? names = null)
    {
        var datasetNames = datasets?.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var nameValues = names?.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var results = _dbService.GetRecentDocuments(limit, datasetNames, nameValues);
        var dtos = results.Select(MapToDto).ToList();
        return Ok(dtos);
    }

    private const int ReadAhead = 50; // Pre-fetch this many extra records beyond the requested page

    /// <summary>
    /// Server-side paged search for virtual scrolling grid.
    /// Two-phase with record caching: first request runs CTE and caches IDs,
    /// subsequent requests hydrate from cached DTOs (with read-ahead prefetch).
    /// Scrolling back to already-viewed pages is instant — no DB hit.
    /// </summary>
    [HttpGet("paged")]
    public async Task<IActionResult> SearchPaged(
        [FromQuery] string? query = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] bool exactMatch = false,
        [FromQuery] List<string>? datasets = null,
        [FromQuery] List<string>? names = null)
    {
        var datasetNames = datasets?.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var nameValues = names?.Where(s => !string.IsNullOrEmpty(s)).ToList();

        // No search query — use direct OFFSET/LIMIT (no caching benefit for browsing all docs)
        if (string.IsNullOrWhiteSpace(query))
        {
            var (items, totalCount) = await _dbService.SearchPagedAsync(
                null, skip, take, exactMatch, datasetNames, nameValues);
            var recentDtos = items.Select(MapToDto).ToList();
            return Ok(new PagedSearchResult { Items = recentDtos, TotalCount = totalCount });
        }

        // Search query present — use cached two-phase approach
        var cacheKey = BuildCacheKey(query, exactMatch, datasetNames, nameValues);

        // Get or create the full cache entry (IDs + hydrated records)
        var entry = _cache.GetOrCreate(cacheKey, cacheEntry =>
        {
            cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
            Console.WriteLine($"[SearchCache] MISS — running CTE for: {query}");
            var ids = _dbService.SearchMatchingIds(query, exactMatch, datasetNames, nameValues);
            return new SearchCacheEntry { Ids = ids };
        })!;

        // Determine the requested page IDs
        var pageIds = entry.Ids.Skip(skip).Take(take).ToArray();

        // Read-ahead: also fetch extra IDs beyond the page for prefetch
        var readAheadIds = entry.Ids.Skip(skip + take).Take(ReadAhead).ToArray();
        var allNeededIds = pageIds.Concat(readAheadIds).ToArray();

        // Check which IDs are NOT yet in the record cache
        var missingIds = allNeededIds.Where(id => !entry.Records.ContainsKey(id)).ToArray();

        if (missingIds.Length > 0)
        {
            var cacheHits = allNeededIds.Length - missingIds.Length;
            Console.WriteLine($"[SearchCache] Hydrating {missingIds.Length} records ({cacheHits} cache hits, {readAheadIds.Length} read-ahead)");
            var hydrated = _dbService.HydrateByIds(missingIds);
            foreach (var record in hydrated)
            {
                var dto = MapToDto(record);
                entry.Records[record.Id] = dto;
            }
        }
        else
        {
            Console.WriteLine($"[SearchCache] Full cache hit — {pageIds.Length} records from memory");
        }

        // Return only the requested page (not the read-ahead) from cache
        var dtos = new List<SearchResultDto>(pageIds.Length);
        foreach (var id in pageIds)
        {
            if (entry.Records.TryGetValue(id, out var dto))
                dtos.Add(dto);
        }

        return Ok(new PagedSearchResult { Items = dtos, TotalCount = entry.Ids.Length });
    }

    /// <summary>
    /// Holds cached search state: the matching ID list + already-hydrated record DTOs.
    /// </summary>
    private class SearchCacheEntry
    {
        public int[] Ids { get; set; } = Array.Empty<int>();
        public Dictionary<int, SearchResultDto> Records { get; } = new();
    }

    private static string BuildCacheKey(string query, bool exactMatch,
        List<string>? datasets, List<string>? names)
    {
        var sb = new StringBuilder();
        sb.Append("search:");
        sb.Append(query.ToLowerInvariant());
        sb.Append(':');
        sb.Append(exactMatch ? "exact" : "fuzzy");
        if (datasets is { Count: > 0 })
        {
            sb.Append(":ds=");
            sb.Append(string.Join(",", datasets.OrderBy(d => d)));
        }
        if (names is { Count: > 0 })
        {
            sb.Append(":nm=");
            sb.Append(string.Join(",", names.OrderBy(n => n)));
        }
        return sb.ToString();
    }

    private static SearchResultDto MapToDto(DocumentSearchResult r) => new()
    {
        FileName = r.FileName,
        FilePath = r.ResolvedFilePath,
        ThumbnailPath = r.ResolvedThumbnailPath,
        FullImagePath = r.ResolvedFullImagePath,
        Text = r.Text,
        Distance = r.Distance,
        Date = r.Date,
        PageCount = r.PageCount,
        SourceName = r.SourceName,
        DataSetName = r.DataSetName,
        SourceUrl = r.SourceUrl,
        Names = ParseNamesJson(r.Names),
        MetadataJson = r.MetadataJson
    };

    [HttpGet("counts")]
    public IActionResult GetCounts()
    {
        var (docs, images, sentences) = _dbService.GetCounts();
        return Ok(new { Documents = docs, Images = images, Sentences = sentences });
    }

    [HttpGet("stats")]
    public IActionResult GetSystemStats()
    {
        var stats = _dbService.GetSystemStats();
        return Ok(stats);
    }

    [HttpGet("stats/datasets")]
    public IActionResult GetDataSetStats()
    {
        var stats = _dbService.GetDataSetStats();
        return Ok(stats);
    }

    [HttpGet("datasets")]
    public IActionResult GetDataSetNames()
    {
        var names = _dbService.GetDataSetNames();
        return Ok(names);
    }

    /// <summary>
    /// Parse Names JSON array string from JSONB metadata into a List.
    /// The DB returns it as a raw JSON string like ["Name1","Name2"].
    /// </summary>
    private static List<string>? ParseNamesJson(string? namesJson)
    {
        if (string.IsNullOrWhiteSpace(namesJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(namesJson);
        }
        catch
        {
            return null;
        }
    }
}

public class SearchResultDto
{
    public string FileName { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? FullImagePath { get; set; }

    // Content & metadata
    public string Text { get; set; } = string.Empty;
    public double Distance { get; set; }
    public DateTime? Date { get; set; }
    public int PageCount { get; set; }
    public string? SourceName { get; set; }
    public string? DataSetName { get; set; }
    public string? SourceUrl { get; set; }
    public List<string>? Names { get; set; }
    public string MetadataJson { get; set; } = "{}";
}

public class PagedSearchResult
{
    public List<SearchResultDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
}
