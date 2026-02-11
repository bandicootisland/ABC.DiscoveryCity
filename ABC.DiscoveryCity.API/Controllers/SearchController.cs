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
    public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] int limit = 20, [FromQuery] bool exactMatch = false)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Query is required.");
        }

        var results = exactMatch
            ? _dbService.SearchExactMatch(query, limit)
            : await _dbService.SearchSimilarAsync(query, limit);
        
        // Map to DTO — pass both OS paths so the client can resolve
        var dtos = results.Select(MapToDto).ToList();

        return Ok(dtos);
    }

    [HttpGet("recent")]
    public IActionResult SearchRecent([FromQuery] int limit = 10)
    {
        var results = _dbService.GetRecentDocuments(limit);
        var dtos = results.Select(MapToDto).ToList();
        return Ok(dtos);
    }

    private static SearchResultDto MapToDto(DocumentSearchResult r) => new()
    {
        FileName = r.FileName,
        FilePath = r.FilePath,
        WindowsFilePath = r.WindowsFilePath,
        LinuxFilePath = r.LinuxFilePath,
        Text = r.Text,
        Distance = r.Distance,
        Date = r.Date,
        PageCount = r.PageCount,
        ThumbnailPath = r.ThumbnailPath,
        WindowsThumbnailPath = r.WindowsThumbnailPath,
        LinuxThumbnailPath = r.LinuxThumbnailPath,
        FullImagePath = r.FullImagePath,
        WindowsFullImagePath = r.WindowsFullImagePath,
        LinuxFullImagePath = r.LinuxFullImagePath,
        SourceName = r.SourceName,
        DataSetName = r.DataSetName,
        People = ParsePeopleJson(r.People)
    };

    [HttpGet("counts")]
    public IActionResult GetCounts()
    {
        var (docs, images, chunks) = _dbService.GetCounts();
        return Ok(new { Documents = docs, Images = images, Chunks = chunks });
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

    /// <summary>
    /// Parse People JSON array string from JSONB metadata into a List.
    /// The DB returns it as a raw JSON string like ["Name1","Name2"].
    /// </summary>
    private static List<string>? ParsePeopleJson(string? peopleJson)
    {
        if (string.IsNullOrWhiteSpace(peopleJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(peopleJson);
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

    // Document paths — legacy + both OS variants
    public string? FilePath { get; set; }
    public string? WindowsFilePath { get; set; }
    public string? LinuxFilePath { get; set; }

    // Thumbnail paths
    public string? ThumbnailPath { get; set; }
    public string? WindowsThumbnailPath { get; set; }
    public string? LinuxThumbnailPath { get; set; }

    // Full image paths
    public string? FullImagePath { get; set; }
    public string? WindowsFullImagePath { get; set; }
    public string? LinuxFullImagePath { get; set; }

    // Content & metadata
    public string Text { get; set; } = string.Empty;
    public double Distance { get; set; }
    public DateTime? Date { get; set; }
    public int PageCount { get; set; }
    public string? SourceName { get; set; }
    public string? DataSetName { get; set; }
    public List<string>? People { get; set; }
}
