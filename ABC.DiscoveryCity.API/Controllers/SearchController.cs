using ABC.DiscoveryCity.PostgreSQL;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly DbService _dbService;
    private readonly IMemoryCache _cache;

    // Tracks in-flight fan-out tasks so we don't launch duplicates
    private static readonly ConcurrentDictionary<string, Task> _inFlightSearches = new();

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
    /// Server-side paged search with tiered fan-out.
    /// First call launches parallel tier searches, returns as soon as fast tiers complete.
    /// Subsequent calls read from merged cache, triggering re-merge if new tiers finished.
    /// Response includes enrichment status so the client knows whether to poll again.
    /// </summary>
    [HttpGet("paged")]
    public async Task<IActionResult> SearchPaged(
        [FromQuery] string? query = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        [FromQuery] bool exactMatch = false,
        [FromQuery] List<string>? datasets = null,
        [FromQuery] List<string>? names = null,
        [FromQuery] bool filenameOnly = false)
    {
        var datasetNames = datasets?.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var nameValues = names?.Where(s => !string.IsNullOrEmpty(s)).ToList();

        // No search query — use cached two-phase browse (unchanged)
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(BrowsePaged(skip, take, datasetNames, nameValues));
        }

        // Filename-only mode — single tier, no fan-out needed
        if (filenameOnly)
        {
            return Ok(FilenamePaged(query, skip, take, datasetNames, nameValues));
        }

        // --- Tiered fan-out search ---
        var queryHash = DbService.ComputeQueryHash(query, exactMatch, filenameOnly, datasetNames, nameValues);
        var (_, isNew, existingStatus) = _dbService.GetOrCreateSearchQuery(
            query, exactMatch, filenameOnly, datasetNames, nameValues);

        if (isNew)
        {
            // Launch fan-out: all tiers in parallel, don't await all
            LaunchFanOut(queryHash, query, exactMatch, datasetNames, nameValues);

            // Wait for fast tiers (1+2) to finish, with timeout
            var fastDeadline = Task.Delay(500);
            while (!fastDeadline.IsCompleted)
            {
                var status = _dbService.GetSearchQueryStatus(queryHash);
                if (status != null && status.AllFastDone)
                {
                    // At least one merge has happened — return what we have
                    break;
                }
                await Task.Delay(20);
            }

            // Trigger first merge with whatever tiers are done
            _dbService.ExecuteRrfMerge(queryHash, mergeVersion: 1);
        }
        else if (existingStatus != null && existingStatus.Enriching)
        {
            // Existing query still enriching — re-merge to pick up any newly completed tiers
            var currentStatus = _dbService.GetSearchQueryStatus(queryHash);
            if (currentStatus != null && currentStatus.Enriching)
            {
                int version = currentStatus.AllDone ? 3 : currentStatus.AllMediumDone ? 2 : 1;
                _dbService.ExecuteRrfMerge(queryHash, mergeVersion: Math.Max(version, currentStatus.MergeCount));
            }
            else if (currentStatus is { AllDone: true, MergeCount: < 3 })
            {
                // All tiers just finished — do final merge
                _dbService.ExecuteRrfMerge(queryHash, mergeVersion: 3);
            }
        }

        // Read merged results for this page
        var finalStatus = _dbService.GetSearchQueryStatus(queryHash);
        var (mergedIds, totalCount) = _dbService.GetMergedResultIds(queryHash, skip, take);

        // Hydrate the page
        var hydrated = mergedIds.Length > 0
            ? _dbService.HydrateByIds(mergedIds, query)
            : new List<DocumentSearchResult>();

        var dtos = hydrated.Select(MapToDto).ToList();

        return Ok(new PagedSearchResult
        {
            Items = dtos,
            TotalCount = totalCount,
            Enriching = finalStatus?.Enriching ?? false,
            MergeVersion = finalStatus?.MergeCount ?? 0,
            CompletedTiers = finalStatus?.CompletedTierCount ?? 0
        });
    }

    /// <summary>
    /// Launches all tier searches in parallel. Fire-and-forget — results go to DB tier tables.
    /// Merge checkpoints happen on the next poll from the client.
    /// </summary>
    private void LaunchFanOut(string queryHash, string query, bool exactMatch,
        List<string>? datasetNames, List<string>? nameValues)
    {
        if (_inFlightSearches.ContainsKey(queryHash)) return;

        var fanOutTask = Task.Run(async () =>
        {
            try
            {
                // Launch all tiers in parallel
                var tier1 = Task.Run(() =>
                {
                    try { _dbService.ExecuteTier1_Filename(queryHash, query, datasetNames, nameValues); }
                    catch (Exception ex) { Console.WriteLine($"[Tier1] Error: {ex.Message}"); }
                });

                var tier2 = Task.Run(() =>
                {
                    try { _dbService.ExecuteTier2_Metadata(queryHash, query, datasetNames, nameValues); }
                    catch (Exception ex) { Console.WriteLine($"[Tier2] Error: {ex.Message}"); }
                });

                var tier3 = Task.Run(() =>
                {
                    try { _dbService.ExecuteTier3_FullText(queryHash, query, datasetNames, nameValues); }
                    catch (Exception ex) { Console.WriteLine($"[Tier3] Error: {ex.Message}"); }
                });

                // Tier 4 only for non-exact-match (semantic search)
                var tier4 = !exactMatch
                    ? Task.Run(async () =>
                    {
                        try { await _dbService.ExecuteTier4_VectorAsync(queryHash, query, datasetNames, nameValues); }
                        catch (Exception ex) { Console.WriteLine($"[Tier4] Error: {ex.Message}"); }
                    })
                    : Task.CompletedTask;

                // Wait for all tiers
                await Task.WhenAll(tier1, tier2, tier3, tier4);

                // Final merge after all tiers complete
                _dbService.ExecuteRrfMerge(queryHash, mergeVersion: 3);
                Console.WriteLine($"[FanOut] All tiers complete for {queryHash[..8]}");
            }
            finally
            {
                _inFlightSearches.TryRemove(queryHash, out _);
            }
        });

        _inFlightSearches.TryAdd(queryHash, fanOutTask);
    }

    /// <summary>
    /// Status endpoint for polling — returns tier completion state without triggering new work.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetSearchStatus(
        [FromQuery] string? query = null,
        [FromQuery] bool exactMatch = false,
        [FromQuery] bool filenameOnly = false,
        [FromQuery] List<string>? datasets = null,
        [FromQuery] List<string>? names = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Ok(new SearchQueryStatus { Tier1Done = true, Tier2Done = true, Tier3Done = true, Tier4Done = true, MergeCount = 3 });

        var datasetNames = datasets?.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var nameValues = names?.Where(s => !string.IsNullOrEmpty(s)).ToList();
        var queryHash = DbService.ComputeQueryHash(query, exactMatch, filenameOnly, datasetNames, nameValues);
        var status = _dbService.GetSearchQueryStatus(queryHash);
        return Ok(status ?? new SearchQueryStatus());
    }

    // -----------------------------------------------------------------------
    // Browse (no query) — unchanged, uses existing ID cache
    // -----------------------------------------------------------------------

    private PagedSearchResult BrowsePaged(int skip, int take,
        List<string>? datasetNames, List<string>? nameValues)
    {
        var browseCacheKey = BuildBrowseCacheKey(datasetNames, nameValues);

        var browseEntry = _cache.GetOrCreate(browseCacheKey, cacheEntry =>
        {
            cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
            Console.WriteLine("[BrowseCache] MISS — loading browse IDs");
            var (ids, realTotal) = _dbService.GetBrowseDocumentIds(datasetNames, nameValues);
            return new BrowseCacheEntry { Ids = ids, TotalCount = realTotal };
        })!;

        var browsePageIds = browseEntry.Ids.Skip(skip).Take(take).ToArray();
        var browseReadAheadIds = browseEntry.Ids.Skip(skip + take).Take(ReadAhead).ToArray();
        var browseAllNeeded = browsePageIds.Concat(browseReadAheadIds).ToArray();
        var browseMissing = browseAllNeeded.Where(id => !browseEntry.Records.ContainsKey(id)).ToArray();

        if (browseMissing.Length > 0)
        {
            var cacheHits = browseAllNeeded.Length - browseMissing.Length;
            Console.WriteLine($"[BrowseCache] Hydrating {browseMissing.Length} records ({cacheHits} cache hits, {browseReadAheadIds.Length} read-ahead)");
            var hydrated = _dbService.HydrateByIds(browseMissing);
            foreach (var record in hydrated)
                browseEntry.Records[record.Id] = MapToDto(record);
        }
        else
        {
            Console.WriteLine($"[BrowseCache] Full cache hit — {browsePageIds.Length} records from memory");
        }

        var browseDtos = new List<SearchResultDto>(browsePageIds.Length);
        foreach (var id in browsePageIds)
        {
            if (browseEntry.Records.TryGetValue(id, out var dto))
                browseDtos.Add(dto);
        }

        return new PagedSearchResult { Items = browseDtos, TotalCount = browseEntry.TotalCount };
    }

    // -----------------------------------------------------------------------
    // Filename-only search — single tier, no fan-out
    // -----------------------------------------------------------------------

    private PagedSearchResult FilenamePaged(string query, int skip, int take,
        List<string>? datasetNames, List<string>? nameValues)
    {
        var cacheKey = BuildCacheKey(query, false, datasetNames, nameValues, filenameOnly: true);

        var entry = _cache.GetOrCreate(cacheKey, cacheEntry =>
        {
            cacheEntry.SlidingExpiration = TimeSpan.FromMinutes(5);
            Console.WriteLine($"[SearchCache] MISS — filename search for: {query}");
            var ids = _dbService.SearchByFileNameIds(query, datasetNames, nameValues);
            return new SearchCacheEntry { Ids = ids };
        })!;

        var pageIds = entry.Ids.Skip(skip).Take(take).ToArray();
        var readAheadIds = entry.Ids.Skip(skip + take).Take(ReadAhead).ToArray();
        var allNeededIds = pageIds.Concat(readAheadIds).ToArray();
        var missingIds = allNeededIds.Where(id => !entry.Records.ContainsKey(id)).ToArray();

        if (missingIds.Length > 0)
        {
            var hydrated = _dbService.HydrateByIds(missingIds, query);
            foreach (var record in hydrated)
                entry.Records[record.Id] = MapToDto(record);
        }

        var dtos = new List<SearchResultDto>(pageIds.Length);
        foreach (var id in pageIds)
        {
            if (entry.Records.TryGetValue(id, out var dto))
                dtos.Add(dto);
        }

        return new PagedSearchResult { Items = dtos, TotalCount = entry.Ids.Length };
    }

    // -----------------------------------------------------------------------
    // Cache entry types
    // -----------------------------------------------------------------------

    private class SearchCacheEntry
    {
        public Guid[] Ids { get; set; } = Array.Empty<Guid>();
        public Dictionary<Guid, SearchResultDto> Records { get; } = new();
    }

    private class BrowseCacheEntry
    {
        public Guid[] Ids { get; set; } = Array.Empty<Guid>();
        public int TotalCount { get; set; }
        public Dictionary<Guid, SearchResultDto> Records { get; } = new();
    }

    private static string BuildCacheKey(string query, bool exactMatch,
        List<string>? datasets, List<string>? names, bool filenameOnly = false)
    {
        var sb = new StringBuilder();
        sb.Append("search:");
        sb.Append(query.ToLowerInvariant());
        sb.Append(':');
        sb.Append(exactMatch ? "exact" : "fuzzy");
        if (filenameOnly) sb.Append(":fn");
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

    private static string BuildBrowseCacheKey(List<string>? datasets, List<string>? names)
    {
        var sb = new StringBuilder();
        sb.Append("browse:");
        if (datasets is { Count: > 0 })
        {
            sb.Append("ds=");
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
        Names = ParseJsonStringArray(r.Names),
        Terms = ParseJsonStringArray(r.Terms),
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
    /// Parse a JSON string array from JSONB metadata into a List.
    /// The DB returns it as a raw JSON string like ["Item1","Item2"].
    /// </summary>
    private static List<string>? ParseJsonStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
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
    public List<string>? Terms { get; set; }
    public string MetadataJson { get; set; } = "{}";
}

public class PagedSearchResult
{
    public List<SearchResultDto> Items { get; set; } = new();
    public int TotalCount { get; set; }

    /// <summary>True if background tiers are still running and results may improve on next poll.</summary>
    public bool Enriching { get; set; }

    /// <summary>How many RRF merges have run (1=fast tiers, 2=medium, 3=all complete).</summary>
    public int MergeVersion { get; set; }

    /// <summary>How many of the 4 search tiers have completed.</summary>
    public int CompletedTiers { get; set; }
}
