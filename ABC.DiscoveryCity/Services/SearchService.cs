using System.Net.Http.Json;

namespace ABC.DiscoveryCity.Services;

public class SearchService
{
    private readonly HttpClient _httpClient;

    public SearchService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<SearchResultDto>> SearchAsync(string query, int limit = 20)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<List<SearchResultDto>>($"api/search?query={Uri.EscapeDataString(query)}&limit={limit}");
            return response ?? new List<SearchResultDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Search error: {ex.Message}");
            return new List<SearchResultDto>();
        }
    }

    public async Task<List<SearchResultDto>> GetRecentDocumentsAsync(int limit = 10)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<List<SearchResultDto>>($"api/search/recent?limit={limit}");
            return response ?? new List<SearchResultDto>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Recent docs error: {ex.Message}");
            return new List<SearchResultDto>();
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
}

public class SearchResultDto
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double Distance { get; set; }
    public DateTime? Date { get; set; }
    public int PageCount { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? FullImagePath { get; set; }
    public string? SourceName { get; set; }
    public string? DataSetName { get; set; }
}

public class ImageDto
{
    public string ImageType { get; set; } = string.Empty;
    public string ImageSize { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class SystemStatsDto
{
    public long TotalDocuments { get; set; }
    public long TotalPages { get; set; }
    public long TotalImages { get; set; }
    public long TotalChunks { get; set; }
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
    public long ChunkCount { get; set; }
    public DateTime? FirstProcessed { get; set; }
    public DateTime? LastProcessed { get; set; }
}
