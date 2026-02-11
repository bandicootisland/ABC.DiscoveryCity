using ABC.DiscoveryCity.PostgreSQL;
using Microsoft.AspNetCore.Mvc;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly DbService _dbService;

    public ImagesController(DbService dbService)
    {
        _dbService = dbService;
    }

    [HttpGet("list")]
    public IActionResult GetImages([FromQuery] string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath)) return BadRequest("Document path required");

        var images = _dbService.GetDocumentImages(documentPath)
            .Select(i => 
            {
                // Combine directory + filename for the full path
                var fullPath = !string.IsNullOrEmpty(i.FileName) && !string.IsNullOrEmpty(i.FilePath)
                    ? Path.Combine(i.FilePath.TrimEnd(Path.DirectorySeparatorChar, '/'), i.FileName)
                    : i.FilePath ?? "";
                return new ImageDto
                {
                    ImageType = i.ImageType,
                    ImageSize = i.ImageSize,
                    FilePath = fullPath,
                    FileName = i.FileName,
                    Width = i.Width,
                    Height = i.Height,
                    Url = $"/api/images/view?path={System.Net.WebUtility.UrlEncode(fullPath)}"
                };
            })
            .ToList();

        return Ok(images);
    }

    [HttpGet("view")]
    public IActionResult ViewFile([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return NotFound("File not found.");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        string contentType = extension switch
        {
            ".pdf" => "application/pdf",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };
        
        var stream = System.IO.File.OpenRead(path);
        return File(stream, contentType);
    }
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
