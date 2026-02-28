using System.Net.Http.Json;

namespace ABC.DiscoveryCity.Services;

public class SearchService
{
    private readonly HttpClient _httpClient;
    private readonly NotificationService _notifications;
    private readonly SlidingWindowPageCache _pageCache = new();
    private string _activeCacheFingerprint = "";

    public SearchService(HttpClient httpClient, NotificationService notifications)
    {
        _httpClient = httpClient;
        _notifications = notifications;
    }

    /// <summary>API base URL (without trailing slash) for building image src URLs etc.</summary>
    public string ApiBaseUrl => _httpClient.BaseAddress!.ToString().TrimEnd('/');

    public async Task<List<SearchResultDto>> SearchAsync(string query, int limit = 20, bool exactMatch = false, List<string>? datasets = null, List<string>? names = null)
    {
        try
        {
            var url = $"api/search?query={Uri.EscapeDataString(query)}&limit={limit}&exactMatch={exactMatch}";
            if (datasets is { Count: > 0 })
                url += "&" + string.Join("&", datasets.Select(d => $"datasets={Uri.EscapeDataString(d)}"));
            if (names is { Count: > 0 })
                url += "&" + string.Join("&", names.Select(p => $"names={Uri.EscapeDataString(p)}"));
            var response = await _httpClient.GetFromJsonAsync<List<SearchResultDto>>(url);
            return response ?? new List<SearchResultDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Search error: {ex.Message}");
            _notifications.ShowError($"Search failed: {ex.Message}");
            return new List<SearchResultDto>();
        }
    }

    public async Task<List<SearchResultDto>> GetRecentDocumentsAsync(int limit = 10, List<string>? datasets = null, List<string>? names = null)
    {
        try
        {
            var url = $"api/search/recent?limit={limit}";
            if (datasets is { Count: > 0 })
                url += "&" + string.Join("&", datasets.Select(d => $"datasets={Uri.EscapeDataString(d)}"));
            if (names is { Count: > 0 })
                url += "&" + string.Join("&", names.Select(p => $"names={Uri.EscapeDataString(p)}"));
            var response = await _httpClient.GetFromJsonAsync<List<SearchResultDto>>(url);
            return response ?? new List<SearchResultDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Recent docs error: {ex.Message}");
            _notifications.ShowError($"Failed to load recent documents: {ex.Message}");
            return new List<SearchResultDto>();
        }
    }

    /// <summary>
    /// Server-side paged search with client-side sliding window cache.
    /// Cache HIT: returns instantly (no HTTP call). Cache MISS: fetches from API, stores, prefetches adjacent pages.
    /// </summary>
    public async Task<PagedSearchResult> SearchPagedAsync(
        string? query, int skip, int take, bool exactMatch = false,
        List<string>? datasets = null, List<string>? names = null,
        CancellationToken cancellationToken = default, bool filenameOnly = false)
    {
        try
        {
            var fingerprint = BuildCacheFingerprint(query, exactMatch, datasets, names, filenameOnly);
            if (fingerprint != _activeCacheFingerprint)
            {
                _pageCache.Clear();
                _activeCacheFingerprint = fingerprint;
            }

            // Cache HIT — return immediately, no HTTP call
            if (_pageCache.TryGetPage(skip, out var cachedItems, out var cachedTotal))
            {
                Console.WriteLine($"[ClientCache] HIT skip={skip}");
                _ = PrefetchAdjacentPagesAsync(skip, take, query, exactMatch, datasets, names, filenameOnly);
                return new PagedSearchResult { Items = cachedItems, TotalCount = cachedTotal };
            }

            // Cache MISS — fetch from API
            var url = BuildPagedUrl(query, skip, take, exactMatch, datasets, names, filenameOnly);
            var response = await _httpClient.GetFromJsonAsync<PagedSearchResult>(url, cancellationToken);
            var result = response ?? new PagedSearchResult();

            _pageCache.StorePage(skip, result.Items, result.TotalCount, skip);
            Console.WriteLine($"[ClientCache] MISS skip={skip}, stored ({result.Items.Count} items, total={result.TotalCount})");

            _ = PrefetchAdjacentPagesAsync(skip, take, query, exactMatch, datasets, names, filenameOnly);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Paged search error: {ex.Message}");
            _notifications.ShowError($"Search failed: {ex.Message}");
            return new PagedSearchResult();
        }
    }

    /// <summary>
    /// Checks the client cache without fetching. Used by OnGridRead to skip debounce for cache hits.
    /// </summary>
    public PagedSearchResult? TryGetCachedPage(
        string? query, int skip, bool exactMatch,
        List<string>? datasets, List<string>? names, bool filenameOnly = false)
    {
        var fingerprint = BuildCacheFingerprint(query, exactMatch, datasets, names, filenameOnly);
        if (fingerprint != _activeCacheFingerprint) return null;
        if (_pageCache.TryGetPage(skip, out var items, out var total))
            return new PagedSearchResult { Items = items, TotalCount = total };
        return null;
    }

    /// <summary>
    /// Clears the client-side page cache. Called when query or filters change.
    /// </summary>
    public void ClearPageCache()
    {
        _pageCache.Clear();
        _activeCacheFingerprint = "";
    }

    private static string BuildPagedUrl(string? query, int skip, int take,
        bool exactMatch, List<string>? datasets, List<string>? names, bool filenameOnly = false)
    {
        var url = $"api/search/paged?skip={skip}&take={take}&exactMatch={exactMatch.ToString().ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(query))
            url += $"&query={Uri.EscapeDataString(query)}";
        if (filenameOnly)
            url += "&filenameOnly=true";
        if (datasets is { Count: > 0 })
            url += "&" + string.Join("&", datasets.Select(d => $"datasets={Uri.EscapeDataString(d)}"));
        if (names is { Count: > 0 })
            url += "&" + string.Join("&", names.Select(p => $"names={Uri.EscapeDataString(p)}"));
        return url;
    }

    private static string BuildCacheFingerprint(string? query, bool exactMatch,
        List<string>? datasets, List<string>? names, bool filenameOnly = false)
    {
        var parts = new List<string>();
        parts.Add(query ?? "");
        parts.Add(exactMatch ? "1" : "0");
        if (filenameOnly) parts.Add("fn=1");
        if (datasets is { Count: > 0 })
            parts.Add("ds=" + string.Join(",", datasets.OrderBy(d => d)));
        if (names is { Count: > 0 })
            parts.Add("nm=" + string.Join(",", names.OrderBy(n => n)));
        return string.Join("|", parts);
    }

    /// <summary>
    /// Background-prefetches pages N-1 and N+1 if not already cached.
    /// Fire-and-forget — does not block the caller. Errors are swallowed.
    /// </summary>
    private async Task PrefetchAdjacentPagesAsync(int currentSkip, int take,
        string? query, bool exactMatch, List<string>? datasets, List<string>? names,
        bool filenameOnly = false)
    {
        var adjacentSkips = new List<int>();
        var prevSkip = currentSkip - take;
        var nextSkip = currentSkip + take;

        if (prevSkip >= 0 && !_pageCache.HasPage(prevSkip))
            adjacentSkips.Add(prevSkip);
        if (!_pageCache.HasPage(nextSkip))
            adjacentSkips.Add(nextSkip);

        if (adjacentSkips.Count == 0) return;

        var fingerprint = BuildCacheFingerprint(query, exactMatch, datasets, names, filenameOnly);

        foreach (var adjSkip in adjacentSkips)
        {
            try
            {
                // Guard: if filters changed while prefetching, discard
                if (fingerprint != _activeCacheFingerprint) return;

                var url = BuildPagedUrl(query, adjSkip, take, exactMatch, datasets, names, filenameOnly);
                var response = await _httpClient.GetFromJsonAsync<PagedSearchResult>(url);
                if (response is { Items.Count: > 0 } && fingerprint == _activeCacheFingerprint)
                {
                    _pageCache.StorePage(adjSkip, response.Items, response.TotalCount, currentSkip);
                    Console.WriteLine($"[ClientCache] PREFETCH skip={adjSkip} stored");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClientCache] Prefetch skip={adjSkip} failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Fetches raw PDF bytes from the API for TelerikPdfViewer.
    /// </summary>
    public async Task<byte[]> GetPdfBytesAsync(string filePath)
    {
        try
        {
            var url = $"api/images/view?path={Uri.EscapeDataString(filePath)}";
            return await _httpClient.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PDF fetch error: {ex.Message}");
            _notifications.ShowError($"Failed to load PDF: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Fetches spreadsheet as .xlsx bytes (API converts .xls/.csv on the fly).
    /// </summary>
    public async Task<byte[]> GetSpreadsheetBytesAsync(string filePath)
    {
        try
        {
            var url = $"api/images/spreadsheet?path={Uri.EscapeDataString(filePath)}";
            return await _httpClient.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Spreadsheet fetch error: {ex.Message}");
            _notifications.ShowError($"Failed to load spreadsheet: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    public async Task<List<ImageDto>> GetImagesAsync(string documentPath)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<List<ImageDto>>($"api/images/list?documentPath={Uri.EscapeDataString(documentPath)}");
            return response ?? new List<ImageDto>();
        }
        catch (Exception ex)
        {
             Console.WriteLine($"GetImages error: {ex.Message}");
             _notifications.ShowError($"Failed to load images: {ex.Message}");
             return new List<ImageDto>();
        }
    }

    public async Task<SystemStatsDto?> GetSystemStatsAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<SystemStatsDto>("api/search/stats");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetSystemStats error: {ex.Message}");
            _notifications.ShowError($"Failed to load system stats: {ex.Message}");
            return null;
        }
    }

    public async Task<List<DataSetStatsDto>> GetDataSetStatsAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<List<DataSetStatsDto>>("api/search/stats/datasets");
            return response ?? new List<DataSetStatsDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetDataSetStats error: {ex.Message}");
            _notifications.ShowError($"Failed to load dataset stats: {ex.Message}");
            return new List<DataSetStatsDto>();
        }
    }

    public async Task<List<string>> GetDataSetNamesAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<List<string>>("api/search/datasets");
            return response ?? new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetDataSetNames error: {ex.Message}");
            _notifications.ShowError($"Failed to load dataset names: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Bounded sliding-window page cache: keeps at most MaxPages pages in memory.
    /// Evicts the page farthest from the current scroll position when full.
    /// </summary>
    private sealed class SlidingWindowPageCache
    {
        private const int MaxPages = 5; // 5 × 50 = 250 records max
        private readonly Dictionary<int, CachedPage> _pages = new(); // keyed by skip value

        public bool TryGetPage(int skip, out List<SearchResultDto> items, out int totalCount)
        {
            if (_pages.TryGetValue(skip, out var page))
            {
                items = page.Items;
                totalCount = page.TotalCount;
                return true;
            }
            items = new List<SearchResultDto>();
            totalCount = 0;
            return false;
        }

        public void StorePage(int skip, List<SearchResultDto> items, int totalCount, int currentSkip)
        {
            if (_pages.ContainsKey(skip))
            {
                _pages[skip] = new CachedPage { Skip = skip, Items = items, TotalCount = totalCount };
                return;
            }

            // Evict farthest page if at capacity
            if (_pages.Count >= MaxPages)
            {
                var farthestKey = _pages.Keys
                    .OrderByDescending(k => Math.Abs(k - currentSkip))
                    .First();
                _pages.Remove(farthestKey);
            }

            _pages[skip] = new CachedPage { Skip = skip, Items = items, TotalCount = totalCount };
        }

        public bool HasPage(int skip) => _pages.ContainsKey(skip);

        public void Clear() => _pages.Clear();

        private sealed class CachedPage
        {
            public int Skip { get; init; }
            public List<SearchResultDto> Items { get; init; } = new();
            public int TotalCount { get; init; }
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
}

public class ImageDto
{
    public string ImageType { get; set; } = string.Empty;
    public string ImageSize { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class SystemStatsDto
{
    public long TotalDocuments { get; set; }
    public long TotalPages { get; set; }
    public long TotalImages { get; set; }
    public long TotalSentences { get; set; }
    public long SourceCount { get; set; }
    public long DataSetCount { get; set; }
    public long DocumentsWithEmbeddings { get; set; }
    public double AvgPagesPerDocument { get; set; }
    public DateTime? LastProcessedAt { get; set; }
}

public class DataSetStatsDto
{
    public string SourceName { get; set; } = "";
    public string DataSetName { get; set; } = "";
    public long DocumentCount { get; set; }
    public long TotalPages { get; set; }
    public long ImageCount { get; set; }
    public long SentenceCount { get; set; }
    public DateTime? FirstProcessed { get; set; }
    public DateTime? LastProcessed { get; set; }
}
