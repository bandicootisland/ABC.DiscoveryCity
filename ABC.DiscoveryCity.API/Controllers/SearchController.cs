using ABC.DiscoveryCity.PostgreSQL;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly DbService _dbService;

    public SearchController(DbService dbService)
    {
        _dbService = dbService;
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

    /// <summary>
    /// Server-side paged search for virtual scrolling grid.
    /// Returns { items: [...], totalCount: N }
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

        var (items, totalCount) = await _dbService.SearchPagedAsync(
            query, skip, take, exactMatch, datasetNames, nameValues);

        var dtos = items.Select(MapToDto).ToList();
        return Ok(new PagedSearchResult { Items = dtos, TotalCount = totalCount });
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
