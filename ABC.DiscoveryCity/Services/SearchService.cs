using System.Net.Http.Json;

namespace ABC.DiscoveryCity.Services;

public class SearchService
{
    private readonly HttpClient _httpClient;

    public SearchService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

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
            return new List<SearchResultDto>();
        }
    }

    /// <summary>
    /// Server-side paged search for virtual scrolling grid.
    /// </summary>
    public async Task<PagedSearchResult> SearchPagedAsync(
        string? query, int skip, int take, bool exactMatch = false,
        List<string>? datasets = null, List<string>? names = null)
    {
        try
        {
            var url = $"api/search/paged?skip={skip}&take={take}&exactMatch={exactMatch}";
            if (!string.IsNullOrWhiteSpace(query))
                url += $"&query={Uri.EscapeDataString(query)}";
            if (datasets is { Count: > 0 })
                url += "&" + string.Join("&", datasets.Select(d => $"datasets={Uri.EscapeDataString(d)}"));
            if (names is { Count: > 0 })
                url += "&" + string.Join("&", names.Select(p => $"names={Uri.EscapeDataString(p)}"));
            var response = await _httpClient.GetFromJsonAsync<PagedSearchResult>(url);
            return response ?? new PagedSearchResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Paged search error: {ex.Message}");
            return new PagedSearchResult();
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
            return new List<string>();
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
