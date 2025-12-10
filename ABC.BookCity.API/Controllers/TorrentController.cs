using ABC.BookCity.API.Models;
using ABC.BookCity.API.Services;
using Microsoft.AspNetCore.Mvc;

namespace ABC.BookCity.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TorrentController : ControllerBase
{
    private readonly ITorrentService _torrentService;

    public TorrentController(ITorrentService torrentService)
    {
        _torrentService = torrentService;
    }

    /// <summary>
    /// Get current engine status and all downloads
    /// </summary>
    [HttpGet("status")]
    public ActionResult<TorrentEngineStatus> GetStatus()
    {
        return new TorrentEngineStatus
        {
            IsRunning = _torrentService.IsRunning,
            WatchFolder = _torrentService.WatchFolder,
            DownloadFolder = _torrentService.DownloadFolder,
            Downloads = _torrentService.GetStatus()
        };
    }

    /// <summary>
    /// Start the torrent engine
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult> Start()
    {
        await _torrentService.StartAsync();
        return Ok(new { message = "Engine started" });
    }

    /// <summary>
    /// Stop the torrent engine
    /// </summary>
    [HttpPost("stop")]
    public async Task<ActionResult> Stop()
    {
        await _torrentService.StopAsync();
        return Ok(new { message = "Engine stopped" });
    }

    /// <summary>
    /// Add a torrent from URL
    /// </summary>
    [HttpPost("add")]
    public async Task<ActionResult> AddTorrent([FromBody] AddTorrentRequest request)
    {
        try
        {
            if (!string.IsNullOrEmpty(request.Url))
            {
                if (request.AutoStart)
                {
                    // Old behavior: download and start immediately (requires engine running)
                    if (!_torrentService.IsRunning)
                    {
                        return BadRequest(new { error = "Engine not running. Start the engine first or set AutoStart=false." });
                    }
                    
                    var data = await _torrentService.DownloadTorrentFromUrlAsync(request.Url);
                    var fileName = Path.GetFileName(new Uri(request.Url).LocalPath);
                    if (!fileName.EndsWith(".torrent"))
                        fileName += ".torrent";
                    
                    await _torrentService.AddTorrentFromBytesAsync(data, fileName);
                    return Ok(new { message = $"Added and started: {fileName}" });
                }
                else
                {
                    // New behavior: just save the torrent file, don't start
                    await _torrentService.SaveTorrentFromUrlAsync(request.Url);
                    var fileName = Path.GetFileName(new Uri(request.Url).LocalPath);
                    return Ok(new { message = $"Saved: {fileName}" });
                }
            }
            else if (request.TorrentData != null && !string.IsNullOrEmpty(request.FileName))
            {
                // Save from bytes (don't auto-start unless requested)
                var path = Path.Combine(_torrentService.WatchFolder, request.FileName);
                if (!System.IO.File.Exists(path))
                {
                    await System.IO.File.WriteAllBytesAsync(path, request.TorrentData);
                }
                
                if (request.AutoStart && _torrentService.IsRunning)
                {
                    await _torrentService.AddTorrentAsync(path);
                    return Ok(new { message = $"Added and started: {request.FileName}" });
                }
                
                return Ok(new { message = $"Saved: {request.FileName}" });
            }
            else
            {
                return BadRequest(new { error = "Provide either a URL or torrent data with filename" });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get list of .torrent files in watch folder with full status info
    /// </summary>
    [HttpGet("files")]
    public ActionResult<TorrentFilesResponse> GetTorrentFiles()
    {
        return new TorrentFilesResponse
        {
            WatchFolder = _torrentService.WatchFolder,
            DownloadFolder = _torrentService.DownloadFolder,
            Files = _torrentService.GetTorrentFiles()
        };
    }
}
