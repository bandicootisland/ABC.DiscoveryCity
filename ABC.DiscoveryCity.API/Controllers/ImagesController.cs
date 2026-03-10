using System.Collections.Concurrent;
using ABC.DiscoveryCity.DevExpressProcessing;
using ABC.DiscoveryCity.PostgreSQL;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders.OpenXml.Xlsx;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders.Xls;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly DbService _dbService;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _renderLocks = new();

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
                var resolvedPath = DbService.ResolveFilePathForCurrentOs(i.FilePath.Length > 0 ? i.FilePath : i.FileName);
                return new ImageDto
                {
                    ImageType = i.ImageType,
                    ImageSize = i.ImageSize,
                    FilePath = resolvedPath,
                    FileName = i.FileName,
                    Width = i.Width,
                    Height = i.Height,
                    HasData = i.ImageData.Length > 0,
                    Url = $"/api/images/view?path={System.Net.WebUtility.UrlEncode(i.FileName.Length > 0 ? i.FileName : resolvedPath)}"
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
                ".mp4" => "video/mp4",
                ".m4a" => "audio/mp4",
                ".avi" => "video/x-msvideo",
                ".vob" => "video/mpeg",
                _ => "application/octet-stream"
            };
            
            return PhysicalFile(resolvedPath, contentType, enableRangeProcessing: true);
        }

        // File not on disk — try serving from DB binary data
        var imageData = _dbService.GetImageData(path);
        if (imageData.Length > 0)
        {
            return File(imageData, "image/jpeg");
        }

        return NotFound($"File not found: {resolvedPath}");
    }

    /// <summary>
    /// Serves image blob directly from DB by parent document Id and size.
    /// </summary>
    [HttpGet("blob")]
    public IActionResult ViewBlob([FromQuery] Guid parentId, [FromQuery] string size = "thumb")
    {
        var data = _dbService.GetImageDataByParentId(parentId, size);
        if (data.Length == 0)
            return NotFound("No image data found.");

        return File(data, "image/png");
    }

    /// <summary>
    /// Serves spreadsheet files as .xlsx bytes for the TelerikSpreadsheet viewer.
    /// Converts .xls and .csv on the fly; .xlsx is served directly.
    /// </summary>
    [HttpGet("spreadsheet")]
    public IActionResult ViewSpreadsheet([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return NotFound("File not found.");

        var resolvedPath = DbService.ResolveFilePathForCurrentOs(path);
        if (!System.IO.File.Exists(resolvedPath))
            return NotFound($"File not found: {resolvedPath}");

        var ext = Path.GetExtension(resolvedPath).ToLowerInvariant();

        // .xlsx can be served directly
        if (ext == ".xlsx")
        {
            var stream = System.IO.File.OpenRead(resolvedPath);
            return File(stream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        }

        // .xls → convert to .xlsx via Telerik SpreadProcessing
        if (ext == ".xls")
        {
            var importProvider = new XlsFormatProvider();
            Telerik.Windows.Documents.Spreadsheet.Model.Workbook workbook;
            using (var input = System.IO.File.OpenRead(resolvedPath))
            {
                workbook = importProvider.Import(input);
            }

            var xlsxProvider = new XlsxFormatProvider();
            var output = new MemoryStream();
            xlsxProvider.Export(workbook, output);
            output.Position = 0;
            return File(output, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        }

        // .csv → read text and build a simple workbook
        if (ext == ".csv")
        {
            var workbook = new Telerik.Windows.Documents.Spreadsheet.Model.Workbook();
            var worksheet = workbook.Worksheets.Add();
            var lines = System.IO.File.ReadAllLines(resolvedPath);
            for (int row = 0; row < lines.Length; row++)
            {
                var cells = lines[row].Split(',');
                for (int col = 0; col < cells.Length; col++)
                {
                    worksheet.Cells[row, col].SetValue(cells[col].Trim().Trim('"'));
                }
            }

            var xlsxProvider = new XlsxFormatProvider();
            var output = new MemoryStream();
            xlsxProvider.Export(workbook, output);
            output.Position = 0;
            return File(output, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        }

        return BadRequest($"Unsupported spreadsheet format: {ext}");
    }

    // -----------------------------------------------------------------------
    // Lazy on-demand rendering
    // -----------------------------------------------------------------------

    /// <summary>
    /// Lazy thumbnail: returns cached thumb/full, or renders page 1 on demand and caches.
    /// </summary>
    [HttpGet("thumbnail")]
    public async Task<IActionResult> GetThumbnail([FromQuery] Guid parentId, [FromQuery] string size = "thumb")
    {
        if (parentId == Guid.Empty) return BadRequest("parentId required");
        if (size != "thumb" && size != "full") return BadRequest("size must be 'thumb' or 'full'");

        // Check cache first
        var cached = _dbService.GetImageDataByParentId(parentId, size);
        if (cached.Length > 0)
            return File(cached, "image/png");

        // Render on demand with concurrency guard
        var semaphore = _renderLocks.GetOrAdd(parentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            cached = _dbService.GetImageDataByParentId(parentId, size);
            if (cached.Length > 0)
                return File(cached, "image/png");

            var filePath = _dbService.GetFilePathByParentId(parentId);
            if (filePath == null || !System.IO.File.Exists(filePath))
                return NotFound("PDF file not found");

            var pdfBytes = await System.IO.File.ReadAllBytesAsync(filePath);
            var renderer = new DevExpressPdfPageRenderer();
            var pageData = renderer.RenderFirstPage(pdfBytes, imageScaleFactor: 0.5f);
            if (pageData == null || pageData.Length == 0)
                return NotFound("Could not render page");

            using var img = Image.Load(pageData);
            int w = img.Width, h = img.Height;

            // Store full preview
            _dbService.InsertPageImage(parentId, "full", pageData, w, h);

            // Generate and store thumb
            int thumbW = 100, thumbH = w > 0 ? (int)(100.0 * h / w) : 0;
            using var thumbImg = img.Clone(x => x.Resize(thumbW, thumbH));
            using var thumbMs = new MemoryStream();
            thumbImg.SaveAsPng(thumbMs);
            var thumbData = thumbMs.ToArray();
            _dbService.InsertPageImage(parentId, "thumb", thumbData, thumbW, thumbH);

            return File(size == "thumb" ? thumbData : pageData, "image/png");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Render failed: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Lazy page render: returns cached page image, or renders on demand and caches.
    /// </summary>
    [HttpGet("page")]
    public async Task<IActionResult> GetPage([FromQuery] Guid parentId, [FromQuery] int page = 1, [FromQuery] float scale = 0.5f)
    {
        if (parentId == Guid.Empty) return BadRequest("parentId required");
        if (page < 1) return BadRequest("page must be >= 1");

        string imageSize = $"page_{page}";

        // Check cache
        var cached = _dbService.GetImageDataByParentId(parentId, imageSize);
        if (cached.Length > 0)
        {
            Response.Headers["Cache-Control"] = "public, max-age=86400";
            return File(cached, "image/png");
        }

        var semaphore = _renderLocks.GetOrAdd(parentId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try
        {
            // Double-check
            cached = _dbService.GetImageDataByParentId(parentId, imageSize);
            if (cached.Length > 0)
            {
                Response.Headers["Cache-Control"] = "public, max-age=86400";
                return File(cached, "image/png");
            }

            var filePath = _dbService.GetFilePathByParentId(parentId);
            if (filePath == null || !System.IO.File.Exists(filePath))
                return NotFound("PDF file not found");

            var pdfBytes = await System.IO.File.ReadAllBytesAsync(filePath);
            var renderer = new DevExpressPdfPageRenderer();
            var pageData = renderer.RenderSinglePage(pdfBytes, page, scale);
            if (pageData == null || pageData.Length == 0)
                return NotFound($"Could not render page {page}");

            using var img = Image.Load(pageData);
            _dbService.InsertPageImage(parentId, imageSize, pageData, img.Width, img.Height);

            Response.Headers["Cache-Control"] = "public, max-age=86400";
            return File(pageData, "image/png");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Render failed: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    // -----------------------------------------------------------------------
    // User file uploads
    // -----------------------------------------------------------------------

    private static readonly string UserUploadsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "DiscoveryCity", "UserUploads");

    [HttpPost("upload")]
    public async Task<IActionResult> UploadUserFile(
        [FromForm] IFormFile file,
        [FromForm] string documentPath)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file provided.");
        if (string.IsNullOrWhiteSpace(documentPath))
            return BadRequest("Document path is required.");

        Directory.CreateDirectory(UserUploadsDir);

        // Store with a unique name to avoid collisions
        var safeFileName = Path.GetFileName(file.FileName);
        var storedName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{safeFileName}";
        var storedPath = Path.Combine(UserUploadsDir, storedName);

        await using (var stream = new FileStream(storedPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        var id = _dbService.InsertUserEdit(documentPath, safeFileName, file.Length, contentType, storedPath);

        return Ok(new { id, storedPath, fileName = safeFileName });
    }

    [HttpGet("user-edits")]
    public IActionResult GetUserEdits([FromQuery] string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
            return BadRequest("Document path is required.");

        var edits = _dbService.GetUserEdits(documentPath);
        return Ok(edits);
    }

    [HttpGet("user-edit/{id}")]
    public IActionResult ViewUserEdit(int id)
    {
        var edit = _dbService.GetUserEdit(id);
        if (edit == null) return NotFound();

        if (!System.IO.File.Exists(edit.StoredPath))
            return NotFound("File not found on disk.");

        return PhysicalFile(edit.StoredPath, edit.ContentType, enableRangeProcessing: true);
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
