using ABC.DiscoveryCity.PostgreSQL;
using Microsoft.AspNetCore.Mvc;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders.OpenXml.Xlsx;
using Telerik.Windows.Documents.Spreadsheet.FormatProviders.Xls;

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
