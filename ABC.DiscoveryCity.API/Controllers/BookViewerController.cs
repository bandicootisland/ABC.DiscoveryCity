using Microsoft.AspNetCore.Mvc;
using ABC.DiscoveryCity.MariaDB;

namespace ABC.DiscoveryCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BookViewerController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;

    public BookViewerController(IConfiguration configuration)
    {
        _configuration = configuration;
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
    }

    /// <summary>
    /// Gets a list of HathiTrust books with metadata.
    /// </summary>
    [HttpGet("hathitrust")]
    public async Task<ActionResult<List<HathiTrustBookListItem>>> GetHathiTrustBooks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] bool linkedOnly = false)
    {
        try
        {
            var service = new BookViewerService(_connectionString);
            var books = await service.GetHathiTrustBooksAsync(page, pageSize, search, linkedOnly);
            return Ok(books);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets detailed information about a specific book.
    /// </summary>
    [HttpGet("hathitrust/{fileId}")]
    public async Task<ActionResult<HathiTrustBookInfo>> GetBookInfo(Guid fileId)
    {
        try
        {
            var linker = new HathiTrustLinker(_connectionString);
            var bookInfo = await linker.GetBookInfoAsync(fileId);
            
            if (bookInfo == null)
                return NotFound(new { error = "Book not found" });

            return Ok(bookInfo);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets a specific page's OCR text content.
    /// </summary>
    [HttpGet("hathitrust/{fileId}/page/{pageNumber}")]
    public async Task<ActionResult<PageContent>> GetPage(Guid fileId, int pageNumber)
    {
        try
        {
            var service = new BookViewerService(_connectionString);
            var content = await service.GetPageContentAsync(fileId, pageNumber);
            
            if (content == null)
                return NotFound(new { error = "Page not found" });

            return Ok(content);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the page list for a book.
    /// </summary>
    [HttpGet("hathitrust/{fileId}/pages")]
    public async Task<ActionResult<PageListResponse>> GetPageList(Guid fileId)
    {
        try
        {
            var service = new BookViewerService(_connectionString);
            var pageList = await service.GetPageListAsync(fileId);
            return Ok(pageList);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // Static storage for link job status (simple in-memory approach)
    private static LinkJobStatus? _currentLinkJob;
    private static readonly object _lockObj = new();
    private static CancellationTokenSource? _linkCts;

    /// <summary>
    /// Triggers the linking process for HathiTrust files.
    /// Starts in background and returns immediately - poll status endpoint for progress.
    /// </summary>
    [HttpPost("hathitrust/link")]
    public ActionResult<LinkJobStatus> LinkFiles([FromQuery] bool relinkAll = false)
    {
        try
        {
            // Check if a job is already running
            lock (_lockObj)
            {
                if (_currentLinkJob != null && _currentLinkJob.IsRunning)
                {
                    return Conflict(new { error = "A linking job is already in progress", status = _currentLinkJob });
                }

                // Initialize job status
                _currentLinkJob = new LinkJobStatus
                {
                    IsRunning = true,
                    StartedAt = DateTime.UtcNow,
                    RelinkAll = relinkAll
                };
                _linkCts = new CancellationTokenSource();
            }

            // Fire and forget - run in background
            var connectionString = _connectionString;
            _ = Task.Run(async () =>
            {
                var linker = new HathiTrustLinker(connectionString, batchSize: 500);
                try
                {
                    var (linked, failed, skipped) = await linker.LinkFilesAsync(
                        relinkAll: relinkAll,
                        progress: (current, total, l, f, s) =>
                        {
                            lock (_lockObj)
                            {
                                if (_currentLinkJob != null)
                                {
                                    _currentLinkJob.Current = current;
                                    _currentLinkJob.Total = total;
                                    _currentLinkJob.Linked = l;
                                    _currentLinkJob.Failed = f;
                                    _currentLinkJob.Skipped = s;
                                }
                            }
                        },
                        cancellationToken: _linkCts?.Token ?? CancellationToken.None);

                    lock (_lockObj)
                    {
                        if (_currentLinkJob != null)
                        {
                            _currentLinkJob.IsRunning = false;
                            _currentLinkJob.CompletedAt = DateTime.UtcNow;
                            _currentLinkJob.Linked = linked;
                            _currentLinkJob.Failed = failed;
                            _currentLinkJob.Skipped = skipped;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_lockObj)
                    {
                        if (_currentLinkJob != null)
                        {
                            _currentLinkJob.IsRunning = false;
                            _currentLinkJob.Error = ex.Message;
                        }
                    }
                }
            });

            // Return immediately with the job status
            return Ok(_currentLinkJob);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancels the current linking job if one is running.
    /// </summary>
    [HttpPost("hathitrust/link/cancel")]
    public ActionResult CancelLinking()
    {
        lock (_lockObj)
        {
            if (_currentLinkJob == null || !_currentLinkJob.IsRunning)
            {
                return BadRequest(new { error = "No linking job is currently running" });
            }

            _linkCts?.Cancel();
            return Ok(new { message = "Cancellation requested" });
        }
    }

    /// <summary>
    /// Gets the status of the current or last linking job.
    /// </summary>
    [HttpGet("hathitrust/link/status")]
    public ActionResult<LinkJobStatus> GetLinkStatus()
    {
        lock (_lockObj)
        {
            if (_currentLinkJob == null)
            {
                return Ok(new LinkJobStatus { IsRunning = false });
            }
            return Ok(_currentLinkJob);
        }
    }

    /// <summary>
    /// Gets summary statistics for the HathiTrust collection.
    /// </summary>
    [HttpGet("hathitrust/summary")]
    public async Task<ActionResult<HathiTrustSummary>> GetSummary()
    {
        try
        {
            var service = new BookViewerService(_connectionString);
            var summary = await service.GetSummaryAsync();
            return Ok(summary);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // Static storage for catalog import job status
    private static CatalogImportJobStatus? _currentCatalogJob;
    private static readonly object _catalogLockObj = new();
    private static CancellationTokenSource? _catalogCts;

    /// <summary>
    /// Syncs the HathiTrust file list from their server.
    /// </summary>
    [HttpPost("hathitrust/catalog/sync")]
    public async Task<ActionResult> SyncCatalogFileList()
    {
        try
        {
            var downloadPath = _configuration["HathiTrust:DownloadPath"] ?? Path.Combine(Path.GetTempPath(), "hathifiles");
            var importer = new HathiCatalogImporter(_connectionString, downloadPath);
            var count = await importer.SyncFileListAsync();
            return Ok(new { message = $"Synced {count} files from HathiTrust file list" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the list of HathiTrust catalog files (full and updates).
    /// </summary>
    [HttpGet("hathitrust/catalog/files")]
    public async Task<ActionResult> GetCatalogFiles()
    {
        try
        {
            var downloadPath = _configuration["HathiTrust:DownloadPath"] ?? Path.Combine(Path.GetTempPath(), "hathifiles");
            var importer = new HathiCatalogImporter(_connectionString, downloadPath);
            
            var pending = await importer.GetPendingDownloadsAsync();
            var toImport = await importer.GetPendingImportsAsync();
            var latestFull = await importer.GetLatestFullFileAsync();
            
            return Ok(new 
            { 
                latestFullFile = latestFull,
                pendingDownloads = pending.Select(f => new { fileName = f.fileName, fileType = f.fileType, fileDate = f.fileDate }),
                pendingImports = toImport.Select(f => new { fileName = f.fileName, fileType = f.fileType, fileDate = f.fileDate })
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Downloads a specific HathiTrust catalog file.
    /// Runs in background - poll status endpoint for progress.
    /// </summary>
    [HttpPost("hathitrust/catalog/download")]
    public ActionResult DownloadCatalogFile([FromQuery] string? fileName = null)
    {
        try
        {
            lock (_catalogLockObj)
            {
                if (_currentCatalogJob != null && _currentCatalogJob.IsRunning)
                {
                    return Conflict(new { error = "A catalog operation is already in progress", status = _currentCatalogJob });
                }

                _currentCatalogJob = new CatalogImportJobStatus
                {
                    IsRunning = true,
                    Operation = "download",
                    FileName = fileName ?? "latest",
                    StartedAt = DateTime.UtcNow
                };
                _catalogCts = new CancellationTokenSource();
            }

            var downloadPath = _configuration["HathiTrust:DownloadPath"] ?? Path.Combine(Path.GetTempPath(), "hathifiles");
            var connectionString = _connectionString;
            
            _ = Task.Run(async () =>
            {
                var importer = new HathiCatalogImporter(connectionString, downloadPath);
                try
                {
                    // Get the latest full file if none specified
                    var targetFile = fileName ?? await importer.GetLatestFullFileAsync();
                    if (string.IsNullOrEmpty(targetFile))
                    {
                        // Sync file list first
                        await importer.SyncFileListAsync(_catalogCts?.Token ?? CancellationToken.None);
                        targetFile = await importer.GetLatestFullFileAsync();
                    }

                    if (string.IsNullOrEmpty(targetFile))
                    {
                        throw new Exception("No HathiTrust catalog files found");
                    }

                    lock (_catalogLockObj)
                    {
                        if (_currentCatalogJob != null)
                            _currentCatalogJob.FileName = targetFile;
                    }

                    await importer.DownloadFileAsync(
                        targetFile,
                        new Progress<(long downloaded, long total)>(p =>
                        {
                            lock (_catalogLockObj)
                            {
                                if (_currentCatalogJob != null)
                                {
                                    _currentCatalogJob.DownloadedBytes = p.downloaded;
                                    _currentCatalogJob.TotalBytes = p.total;
                                }
                            }
                        }),
                        _catalogCts?.Token ?? CancellationToken.None);

                    lock (_catalogLockObj)
                    {
                        if (_currentCatalogJob != null)
                        {
                            _currentCatalogJob.IsRunning = false;
                            _currentCatalogJob.CompletedAt = DateTime.UtcNow;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_catalogLockObj)
                    {
                        if (_currentCatalogJob != null)
                        {
                            _currentCatalogJob.IsRunning = false;
                            _currentCatalogJob.Error = ex.Message;
                        }
                    }
                }
            });

            return Ok(_currentCatalogJob);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Imports a downloaded HathiTrust catalog file into the database.
    /// Runs in background - poll status endpoint for progress.
    /// Use resume=true to continue a stopped import (uses INSERT IGNORE).
    /// </summary>
    [HttpPost("hathitrust/catalog/import")]
    public ActionResult ImportCatalogFile([FromQuery] string fileName, [FromQuery] bool isFullLoad = false, [FromQuery] bool resume = false)
    {
        try
        {
            lock (_catalogLockObj)
            {
                if (_currentCatalogJob != null && _currentCatalogJob.IsRunning)
                {
                    return Conflict(new { error = "A catalog operation is already in progress", status = _currentCatalogJob });
                }

                _currentCatalogJob = new CatalogImportJobStatus
                {
                    IsRunning = true,
                    Operation = resume ? "import-resume" : "import",
                    FileName = fileName,
                    StartedAt = DateTime.UtcNow
                };
                _catalogCts = new CancellationTokenSource();
            }

            var downloadPath = _configuration["HathiTrust:DownloadPath"] ?? Path.Combine(Path.GetTempPath(), "hathifiles");
            var connectionString = _connectionString;

            _ = Task.Run(async () =>
            {
                var importer = new HathiCatalogImporter(connectionString, downloadPath);
                try
                {
                    var (imported, updated, skipped) = await importer.ImportFileAsync(
                        fileName,
                        isFullLoad,
                        resume,
                        new Progress<(int processed, int imported)>(p =>
                        {
                            lock (_catalogLockObj)
                            {
                                if (_currentCatalogJob != null)
                                {
                                    _currentCatalogJob.RowsProcessed = p.processed;
                                    _currentCatalogJob.RowsImported = p.imported;
                                }
                            }
                        }),
                        _catalogCts?.Token ?? CancellationToken.None);

                    lock (_catalogLockObj)
                    {
                        if (_currentCatalogJob != null)
                        {
                            _currentCatalogJob.IsRunning = false;
                            _currentCatalogJob.CompletedAt = DateTime.UtcNow;
                            _currentCatalogJob.RowsImported = imported;
                            _currentCatalogJob.RowsUpdated = updated;
                            _currentCatalogJob.RowsSkipped = skipped;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (_catalogLockObj)
                    {
                        if (_currentCatalogJob != null)
                        {
                            _currentCatalogJob.IsRunning = false;
                            _currentCatalogJob.Error = ex.Message;
                        }
                    }
                }
            });

            return Ok(_currentCatalogJob);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets the status of the current or last catalog operation.
    /// </summary>
    [HttpGet("hathitrust/catalog/status")]
    public ActionResult<CatalogImportJobStatus> GetCatalogStatus()
    {
        lock (_catalogLockObj)
        {
            if (_currentCatalogJob == null)
            {
                return Ok(new CatalogImportJobStatus { IsRunning = false });
            }
            return Ok(_currentCatalogJob);
        }
    }

    /// <summary>
    /// Cancels the current catalog operation if one is running.
    /// </summary>
    [HttpPost("hathitrust/catalog/cancel")]
    public ActionResult CancelCatalogOperation()
    {
        lock (_catalogLockObj)
        {
            if (_currentCatalogJob == null || !_currentCatalogJob.IsRunning)
            {
                return BadRequest(new { error = "No catalog operation is currently running" });
            }

            _catalogCts?.Cancel();
            return Ok(new { message = "Cancellation requested" });
        }
    }
}

public class LinkJobStatus
{
    public bool IsRunning { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int Current { get; set; }
    public int Total { get; set; }
    public int Linked { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public bool RelinkAll { get; set; }
    public string? Error { get; set; }
}

public class HathiTrustBookListItem
{
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string? Htid { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Language { get; set; }
    public string? Access { get; set; }
    public string? PublicUrl { get; set; }
    public int PageCount { get; set; }
    public bool HasMetadata { get; set; }
}

public class PageContent
{
    public int PageNumber { get; set; }
    public int TotalPages { get; set; }
    public string Text { get; set; } = "";
    public string? Htid { get; set; }
    public string? PageImageUrl { get; set; }
    public string? PublicUrl { get; set; }
}

public class PageListResponse
{
    public Guid FileId { get; set; }
    public string? Htid { get; set; }
    public int TotalPages { get; set; }
    public List<string> PageNames { get; set; } = new();
}

public class LinkResult
{
    public int Linked { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
}

public class HathiTrustSummary
{
    public int TotalFiles { get; set; }
    public int LinkedFiles { get; set; }
    public int UnlinkedFiles { get; set; }
    public int WithMetadata { get; set; }
    public long TotalPages { get; set; }
    public double TotalSizeGB { get; set; }
}

public class CatalogImportJobStatus
{
    public bool IsRunning { get; set; }
    public string Operation { get; set; } = "";  // "download" or "import"
    public string FileName { get; set; } = "";
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    // Download progress
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    
    // Import progress
    public int RowsProcessed { get; set; }
    public int RowsImported { get; set; }
    public int RowsUpdated { get; set; }
    public int RowsSkipped { get; set; }
    
    public string? Error { get; set; }
}
