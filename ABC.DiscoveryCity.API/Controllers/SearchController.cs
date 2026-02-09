using ABC.DiscoveryCity.PostgreSQL;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest("Query is required.");
        }

        var results = await _dbService.SearchSimilarAsync(query, limit);
        
        // Map to DTO for frontend
        var dtos = results.Select(r => new SearchResultDto
        {
            FilePath = r.FilePath,
            FileName = Path.GetFileName(r.FilePath),
            Text = r.Text,
            Distance = r.Distance,
            Date = r.Date,
            PageCount = r.PageCount,
            ThumbnailPath = r.Thumbnail,
            FullImagePath = r.FullImage,
            SourceName = r.SourceName,
            DataSetName = r.DataSetName
        }).ToList();

        return Ok(dtos);
    }

    [HttpGet("recent")]
    public IActionResult SearchRecent([FromQuery] int limit = 10)
    {
        var results = _dbService.GetRecentDocuments(limit);

        var dtos = results.Select(r => new SearchResultDto
        {
            FilePath = r.FilePath,
            FileName = Path.GetFileName(r.FilePath),
            Text = r.Text,
            Distance = r.Distance,
            Date = r.Date,
            PageCount = r.PageCount,
            ThumbnailPath = r.Thumbnail,
            FullImagePath = r.FullImage,
            SourceName = r.SourceName,
            DataSetName = r.DataSetName
        }).ToList();

        return Ok(dtos);
    }

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
