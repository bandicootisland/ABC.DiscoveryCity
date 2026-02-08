using System.Net.Http.Json;

namespace ABC.DiscoveryCity.Services;

/// <summary>
/// Status of a single torrent download
/// </summary>
public class TorrentDownloadStatus
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public double Progress { get; set; }
    public double DownloadSpeed { get; set; }
    public int Peers { get; set; }
    public long TotalSize { get; set; }
    public long Downloaded { get; set; }
    public string? ProcessingBy { get; set; }
}

/// <summary>
/// Information about a torrent file in the watch folder
/// </summary>
public class TorrentFileInfo
{
    public string FileName { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long FileSize { get; set; }
    public TorrentFileStatus Status { get; set; }
    public string? ProcessingBy { get; set; }
    public DateTime? LastModified { get; set; }
    
    public string StatusDisplay => Status switch
    {
        TorrentFileStatus.NotStarted => "Not Started",
        TorrentFileStatus.Processing => "Processing",
        TorrentFileStatus.Completed => "Completed",
        _ => "Unknown"
    };
}

public enum TorrentFileStatus
{
    NotStarted = 0,
    Processing = 1,
    Completed = 2
}

/// <summary>
/// Overall engine status from API
/// </summary>
public class TorrentEngineStatus
{
    public bool IsRunning { get; set; }
    public string WatchFolder { get; set; } = "";
    public string DownloadFolder { get; set; } = "";
    public List<TorrentDownloadStatus> Downloads { get; set; } = new();
}

/// <summary>
/// Response from /api/torrent/files endpoint
/// </summary>
public class TorrentFilesResponse
{
    public string WatchFolder { get; set; } = "";
    public string DownloadFolder { get; set; } = "";
    public List<TorrentFileInfo> Files { get; set; } = new();
}

/// <summary>
/// Request to add a torrent
/// </summary>
public class AddTorrentRequest
{
    public string? Url { get; set; }
    public byte[]? TorrentData { get; set; }
    public string? FileName { get; set; }
    public bool AutoStart { get; set; } = false;
}

/// <summary>
/// TorrentService for Blazor WASM - calls the API to manage torrents
/// </summary>
public class TorrentService
{
    private readonly HttpClient _http;
    private TorrentEngineStatus _cachedStatus = new();
    
    public event Action? OnStatusChanged;
    
    public bool IsRunning => _cachedStatus.IsRunning;
    public string WatchFolder => _cachedStatus.WatchFolder;
    public string DownloadFolder => _cachedStatus.DownloadFolder;
    
    public TorrentService(HttpClient http)
    {
        _http = http;
    }
    
    public async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _http.GetFromJsonAsync<TorrentEngineStatus>("api/torrent/status");
            if (status != null)
            {
                _cachedStatus = status;
                OnStatusChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error refreshing status: {ex.Message}");
        }
    }
    
    public async Task StartAsync()
    {
        try
        {
            await _http.PostAsync("api/torrent/start", null);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting engine: {ex.Message}");
            throw;
        }
    }
    
    public async Task StopAsync()
    {
        try
        {
            await _http.PostAsync("api/torrent/stop", null);
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error stopping engine: {ex.Message}");
            throw;
        }
    }
    
    public async Task AddTorrentFromUrlAsync(string url)
    {
        try
        {
            var request = new AddTorrentRequest { Url = url };
            var response = await _http.PostAsJsonAsync("api/torrent/add", request);
            response.EnsureSuccessStatusCode();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding torrent: {ex.Message}");
            throw;
        }
    }
    
    public async Task AddTorrentFromBytesAsync(byte[] torrentData, string fileName)
    {
        try
        {
            var request = new AddTorrentRequest 
            { 
                TorrentData = torrentData, 
                FileName = fileName 
            };
            var response = await _http.PostAsJsonAsync("api/torrent/add", request);
            response.EnsureSuccessStatusCode();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error adding torrent: {ex.Message}");
            throw;
        }
    }
    
    public List<TorrentDownloadStatus> GetStatus()
    {
        return _cachedStatus.Downloads;
    }

    /// <summary>
    /// Get list of torrent files with their status (without needing engine running)
    /// </summary>
    public async Task<TorrentFilesResponse> GetTorrentFilesAsync()
    {
        try
        {
            var response = await _http.GetFromJsonAsync<TorrentFilesResponse>("api/torrent/files");
            return response ?? new TorrentFilesResponse();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting torrent files: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Save a torrent from URL without starting download
    /// </summary>
    public async Task SaveTorrentFromUrlAsync(string url)
    {
        try
        {
            var request = new AddTorrentRequest { Url = url, AutoStart = false };
            var response = await _http.PostAsJsonAsync("api/torrent/add", request);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving torrent: {ex.Message}");
            throw;
        }
    }
}
