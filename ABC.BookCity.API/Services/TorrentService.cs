using MonoTorrent;
using MonoTorrent.Client;

namespace ABC.BookCity.API.Services;

public interface ITorrentService
{
    bool IsRunning { get; }
    string WatchFolder { get; }
    string DownloadFolder { get; }
    Task StartAsync();
    Task StopAsync();
    Task AddTorrentAsync(string torrentFilePath);
    Task AddTorrentFromBytesAsync(byte[] torrentData, string fileName);
    Task<byte[]> DownloadTorrentFromUrlAsync(string url);
    Task SaveTorrentFromUrlAsync(string url); // Save without starting
    List<Models.TorrentDownloadStatus> GetStatus();
    List<Models.TorrentFileInfo> GetTorrentFiles(); // List all torrent files with status
}

public class TorrentService : ITorrentService, IDisposable
{
    private const string LockExtension = ".processing";
    private const string CompletedExtension = ".completed";
    
    public string WatchFolder { get; } = @"H:\BookCity\Torrents";
    public string DownloadFolder { get; } = @"H:\BookCity\Downloads";
    public string InstanceId { get; } = $"API-{Environment.MachineName}-{Environment.ProcessId}";
    
    private ClientEngine? _engine;
    private FileSystemWatcher? _watcher;
    private readonly List<TorrentManager> _managers = new();
    private readonly object _lock = new();
    
    public bool IsRunning => _engine != null;
    
    public async Task StartAsync()
    {
        if (_engine != null) return;
        
        // Ensure directories exist
        Directory.CreateDirectory(WatchFolder);
        Directory.CreateDirectory(DownloadFolder);
        
        // Initialize engine
        var settings = new EngineSettingsBuilder()
            //.WithSaveDirectory(DownloadFolder)
            .ToSettings();
        _engine = new ClientEngine(settings);
        
        // Setup file watcher
        _watcher = new FileSystemWatcher(WatchFolder, "*.torrent");
        _watcher.Created += async (s, e) =>
        {
            await Task.Delay(1000); // Wait for file to be fully written
            await AddTorrentAsync(e.FullPath);
        };
        _watcher.EnableRaisingEvents = true;
        
        // Load existing torrents (that aren't locked by another process)
        foreach (var file in Directory.GetFiles(WatchFolder, "*.torrent"))
        {
            await AddTorrentAsync(file);
        }
    }
    
    public async Task StopAsync()
    {
        if (_engine == null) return;
        
        _watcher?.Dispose();
        _watcher = null;
        
        // Release all our locks
        foreach (var manager in _managers)
        {
            var torrentPath = Path.Combine(WatchFolder, manager.Torrent?.Name + ".torrent");
            ReleaseLock(torrentPath);
        }
        
        await _engine.StopAllAsync();
        _engine.Dispose();
        _engine = null;
        
        lock (_lock)
        {
            _managers.Clear();
        }
    }
    
    /// <summary>
    /// Try to acquire a lock on a torrent file. Returns true if lock acquired.
    /// </summary>
    private bool TryAcquireLock(string torrentFilePath)
    {
        var lockFile = torrentFilePath + LockExtension;
        
        try
        {
            // Check if lock exists and is valid
            if (File.Exists(lockFile))
            {
                var lockContent = File.ReadAllText(lockFile);
                // Lock exists - another process has it
                // Could add stale lock detection here (e.g., if lock is > 1 hour old)
                Console.WriteLine($"[TorrentService] Lock exists for {Path.GetFileName(torrentFilePath)}: {lockContent}");
                return false;
            }
            
            // Create lock file with our instance ID and timestamp
            var lockData = $"{InstanceId}|{DateTime.UtcNow:O}";
            File.WriteAllText(lockFile, lockData);
            Console.WriteLine($"[TorrentService] Acquired lock for {Path.GetFileName(torrentFilePath)}");
            return true;
        }
        catch (IOException)
        {
            // Race condition - another process grabbed it
            return false;
        }
    }
    
    /// <summary>
    /// Release our lock on a torrent file
    /// </summary>
    private void ReleaseLock(string torrentFilePath)
    {
        var lockFile = torrentFilePath + LockExtension;
        
        try
        {
            if (File.Exists(lockFile))
            {
                var lockContent = File.ReadAllText(lockFile);
                // Only delete if it's our lock
                if (lockContent.StartsWith(InstanceId))
                {
                    File.Delete(lockFile);
                    Console.WriteLine($"[TorrentService] Released lock for {Path.GetFileName(torrentFilePath)}");
                }
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[TorrentService] Could not release lock: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Get who holds the lock on a torrent file
    /// </summary>
    private string? GetLockHolder(string torrentFilePath)
    {
        var lockFile = torrentFilePath + LockExtension;
        
        try
        {
            if (File.Exists(lockFile))
            {
                var content = File.ReadAllText(lockFile);
                var parts = content.Split('|');
                return parts.Length > 0 ? parts[0] : "Unknown";
            }
        }
        catch { }
        
        return null;
    }
    
    public async Task AddTorrentAsync(string torrentFilePath)
    {
        if (_engine == null) return;
        
        // Try to acquire lock
        if (!TryAcquireLock(torrentFilePath))
        {
            Console.WriteLine($"[TorrentService] Skipping {Path.GetFileName(torrentFilePath)} - locked by another process");
            return;
        }
        
        try
        {
            var torrent = await Torrent.LoadAsync(torrentFilePath);
            
            // Check if already added
            lock (_lock)
            {
                if (_managers.Any(m => m.InfoHashes == torrent.InfoHashes))
                {
                    Console.WriteLine($"[TorrentService] Already tracking: {torrent.Name}");
                    return;
                }
            }
            
            var manager = await _engine.AddAsync(torrent, DownloadFolder);
            
            lock (_lock)
            {
                _managers.Add(manager);
            }
            
            await manager.StartAsync();
            Console.WriteLine($"[TorrentService] Started: {torrent.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TorrentService] Error adding torrent: {ex.Message}");
            ReleaseLock(torrentFilePath); // Release lock on error
        }
    }
    
    public async Task AddTorrentFromBytesAsync(byte[] torrentData, string fileName)
    {
        // Save to watch folder - FileSystemWatcher will pick it up
        var path = Path.Combine(WatchFolder, fileName);
        await File.WriteAllBytesAsync(path, torrentData);
    }
    
    public async Task<byte[]> DownloadTorrentFromUrlAsync(string url)
    {
        using var http = new HttpClient();
        return await http.GetByteArrayAsync(url);
    }
    
    public List<Models.TorrentDownloadStatus> GetStatus()
    {
        lock (_lock)
        {
            var result = _managers.Select(m => new Models.TorrentDownloadStatus
            {
                Name = m.Torrent?.Name ?? "Unknown",
                State = m.State.ToString(),
                Progress = m.Progress,
                DownloadSpeed = m.Monitor.DownloadRate / 1024.0,
                Peers = m.Peers.Available,
                TotalSize = m.Torrent?.Size ?? 0,
                Downloaded = (long)(m.Progress / 100.0 * (m.Torrent?.Size ?? 0)),
                ProcessingBy = InstanceId
            }).ToList();
            
            // Also show torrents that are locked by other processes
            foreach (var file in Directory.GetFiles(WatchFolder, "*.torrent"))
            {
                var holder = GetLockHolder(file);
                if (holder != null && holder != InstanceId)
                {
                    // This torrent is being processed by another instance
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!result.Any(r => r.Name == name))
                    {
                        result.Add(new Models.TorrentDownloadStatus
                        {
                            Name = name,
                            State = "Processing (External)",
                            Progress = 0,
                            ProcessingBy = holder
                        });
                    }
                }
            }
            
            return result;
        }
    }

    /// <summary>
    /// Save a torrent file from URL without starting the download
    /// </summary>
    public async Task SaveTorrentFromUrlAsync(string url)
    {
        var data = await DownloadTorrentFromUrlAsync(url);
        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        if (!fileName.EndsWith(".torrent"))
            fileName += ".torrent";
        
        var path = Path.Combine(WatchFolder, fileName);
        
        // Don't overwrite if exists
        if (!File.Exists(path))
        {
            await File.WriteAllBytesAsync(path, data);
            Console.WriteLine($"[TorrentService] Saved torrent: {fileName}");
        }
        else
        {
            Console.WriteLine($"[TorrentService] Torrent already exists: {fileName}");
        }
    }

    /// <summary>
    /// Get list of all torrent files in watch folder with their status
    /// </summary>
    public List<Models.TorrentFileInfo> GetTorrentFiles()
    {
        var result = new List<Models.TorrentFileInfo>();
        
        if (!Directory.Exists(WatchFolder))
            return result;

        foreach (var file in Directory.GetFiles(WatchFolder, "*.torrent"))
        {
            var fileInfo = new FileInfo(file);
            var lockFile = file + LockExtension;
            var completedFile = file + CompletedExtension;
            
            var status = Models.TorrentFileStatus.NotStarted;
            string? processingBy = null;
            
            if (File.Exists(completedFile))
            {
                status = Models.TorrentFileStatus.Completed;
            }
            else if (File.Exists(lockFile))
            {
                status = Models.TorrentFileStatus.Processing;
                try
                {
                    var content = File.ReadAllText(lockFile);
                    var parts = content.Split('|');
                    processingBy = parts.Length > 0 ? parts[0] : "Unknown";
                }
                catch { processingBy = "Unknown"; }
            }
            
            result.Add(new Models.TorrentFileInfo
            {
                FileName = Path.GetFileName(file),
                FullPath = file,
                FileSize = fileInfo.Length,
                Status = status,
                ProcessingBy = processingBy,
                LastModified = fileInfo.LastWriteTime
            });
        }
        
        return result.OrderBy(f => f.FileName).ToList();
    }
    
    public void Dispose()
    {
        _watcher?.Dispose();
        _engine?.Dispose();
    }
}
