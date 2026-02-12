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
                // Resolve the path for the current OS (handles Windows↔Linux translation)
                var resolvedPath = DbService.ResolveFilePathForCurrentOs(i.FilePath ?? i.FileName ?? "");
                return new ImageDto
                {
                    ImageType = i.ImageType,
                    ImageSize = i.ImageSize,
                    FilePath = resolvedPath,
                    FileName = i.FileName,
                    Width = i.Width,
                    Height = i.Height,
                    HasData = i.ImageData != null,
                    Url = $"/api/images/view?path={System.Net.WebUtility.UrlEncode(i.FileName ?? resolvedPath)}"
                };
            })
            .ToList();

        return Ok(images);
    }

    [HttpGet("view")]
    public IActionResult ViewFile([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return NotFound("File not found.");

        // Resolve the path for the current OS — handles Windows paths on Linux and vice versa
        var resolvedPath = DbService.ResolveFilePathForCurrentOs(path);

        if (System.IO.File.Exists(resolvedPath))
        {
            var extension = Path.GetExtension(resolvedPath).ToLowerInvariant();
            string contentType = extension switch
            {
                ".pdf" => "application/pdf",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".json" => "application/json",
                _ => "application/octet-stream"
            };
            
            var stream = System.IO.File.OpenRead(resolvedPath);
            return File(stream, contentType);
        }

        // File not on disk — try serving from DB binary data
        var imageData = _dbService.GetImageData(path);
        if (imageData != null)
        {
            return File(imageData, "image/jpeg");
        }

        return NotFound($"File not found: {resolvedPath}");
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
    public bool HasData { get; set; }
    public string Url { get; set; } = string.Empty;
}
