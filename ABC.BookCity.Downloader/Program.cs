using MonoTorrent;
using MonoTorrent.Client;

namespace ABC.BookCity.Downloader;
class Program
{
    private const string LockExtension = ".processing";
    private const string CompletedExtension = ".completed";
    private static string WatchFolder = @"H:\BookCity\Torrents";    
    private static string DownloadFolder = @"H:\BookCity\Downloads";
    private static ClientEngine Engine = null!;
    private static string InstanceId = $"Console-{Environment.MachineName}-{Environment.ProcessId}";
    private static readonly List<string> OurLocks = new();
    private static readonly Dictionary<string, TorrentManager> ActiveDownloads = new();

    static async Task Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         ABC.BookCity Torrent Downloader                      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"🔑 Instance ID: {InstanceId}");
        Console.WriteLine();

        EnsureDirectories();

        // Initialize MonoTorrent Engine
        var settings = new EngineSettings();
        Engine = new ClientEngine(settings);

        Console.WriteLine($"📁 WATCH FOLDER:    {WatchFolder}");
        Console.WriteLine($"💾 DOWNLOAD FOLDER: {DownloadFolder}");
        Console.WriteLine();

        // Set up FileSystemWatcher for new torrents
        using var watcher = new FileSystemWatcher(WatchFolder, "*.torrent");
        watcher.Created += OnTorrentFileCreated;
        watcher.EnableRaisingEvents = true;

        // Interactive menu loop
        await RunMenuLoopAsync();

        Console.WriteLine("Shutting down engine...");
        await Engine.StopAllAsync();
        
        // Release all our locks
        ReleaseAllLocks();
        
        Console.WriteLine("Engine stopped. Exiting.");
    }

    private static async Task RunMenuLoopAsync()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════════════════════════════");
            Console.WriteLine("                    TORRENT MENU");
            Console.WriteLine("════════════════════════════════════════════════════════════════");
            
            var torrents = GetTorrentFiles();
            
            if (torrents.Count == 0)
            {
                Console.WriteLine("  No .torrent files found in watch folder.");
                Console.WriteLine($"  Add torrents to: {WatchFolder}");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("  #  | Status       | Size        | Name");
                Console.WriteLine("  ---|--------------|-------------|-------------------------------------");
                
                for (int i = 0; i < torrents.Count; i++)
                {
                    var info = torrents[i];
                    var statusIcon = info.Status switch
                    {
                        TorrentStatus.NotStarted => "⬚ Not Started",
                        TorrentStatus.Downloading => "⬇ Downloading",
                        TorrentStatus.Completed => "✓ Completed  ",
                        TorrentStatus.Locked => "🔒 Locked     ",
                        _ => "? Unknown    "
                    };
                    
                    Console.WriteLine($"  {i + 1,2} | {statusIcon} | {FormatSize(info.Size),11} | {info.Name}");
                }
            }
            
            Console.WriteLine();
            Console.WriteLine("────────────────────────────────────────────────────────────────");
            Console.WriteLine("  Commands:");
            Console.WriteLine("    [1-9]  Start download for torrent #");
            Console.WriteLine("    [S]    Show active download status");
            Console.WriteLine("    [R]    Refresh list");
            Console.WriteLine("    [A]    Start ALL not-started torrents");
            Console.WriteLine("    [Q]    Quit");
            Console.WriteLine("────────────────────────────────────────────────────────────────");
            Console.Write("  Enter choice (or # + Enter): ");
            
            var input = Console.ReadLine()?.Trim().ToUpperInvariant() ?? "";
            
            if (input == "Q")
            {
                break;
            }
            else if (input == "R" || string.IsNullOrEmpty(input))
            {
                Console.WriteLine("  Refreshing...");
                continue;
            }
            else if (input == "S")
            {
                await ShowActiveDownloadsAsync();
            }
            else if (input == "A")
            {
                await StartAllNotStartedAsync(torrents);
            }
            else if (int.TryParse(input, out int index))
            {
                index--; // Convert to 0-based
                if (index >= 0 && index < torrents.Count)
                {
                    await StartDownloadAsync(torrents[index].FullPath);
                }
                else
                {
                    Console.WriteLine($"  Invalid selection: {index + 1}. Valid range: 1-{torrents.Count}");
                }
            }
            else
            {
                Console.WriteLine($"  Unknown command: {input}");
            }
        }
    }

    private static List<TorrentFileInfo> GetTorrentFiles()
    {
        var result = new List<TorrentFileInfo>();
        
        foreach (var file in Directory.GetFiles(WatchFolder, "*.torrent"))
        {
            if (file.EndsWith(LockExtension) || file.EndsWith(CompletedExtension))
                continue;
                
            var fileInfo = new FileInfo(file);
            var status = GetTorrentStatus(file);
            
            result.Add(new TorrentFileInfo
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                Size = fileInfo.Length,
                Status = status
            });
        }
        
        return result.OrderBy(t => t.Name).ToList();
    }

    private static TorrentStatus GetTorrentStatus(string torrentFilePath)
    {
        var lockFile = torrentFilePath + LockExtension;
        var completedFile = torrentFilePath + CompletedExtension;
        
        if (File.Exists(completedFile))
            return TorrentStatus.Completed;
            
        if (ActiveDownloads.ContainsKey(torrentFilePath))
            return TorrentStatus.Downloading;
            
        if (File.Exists(lockFile))
            return TorrentStatus.Locked;
            
        return TorrentStatus.NotStarted;
    }

    private static async Task ShowActiveDownloadsAsync()
    {
        Console.WriteLine();
        Console.WriteLine("  ═══════════════════════════════════════════════════════════");
        Console.WriteLine("  ACTIVE DOWNLOADS");
        Console.WriteLine("  ═══════════════════════════════════════════════════════════");
        
        if (ActiveDownloads.Count == 0)
        {
            Console.WriteLine("  No active downloads.");
            return;
        }
        
        foreach (var kvp in ActiveDownloads)
        {
            var manager = kvp.Value;
            var name = Path.GetFileName(kvp.Key);
            
            Console.WriteLine();
            Console.WriteLine($"  📦 {name}");
            Console.WriteLine($"     State: {manager.State}");
            Console.WriteLine($"     Progress: {manager.Progress:F2}%");
            Console.WriteLine($"     Speed: {manager.Monitor.DownloadRate / 1024.0:N0} KB/s");
            Console.WriteLine($"     Peers: {manager.Peers.Available}");
            
            // Show prioritized file progress
            var prioritized = manager.Files.Where(f => f.Priority == Priority.Highest).ToList();
            if (prioritized.Any())
            {
                var completed = prioritized.Count(f => f.BitField.PercentComplete >= 100.0);
                Console.WriteLine($"     Prioritized Files: {completed}/{prioritized.Count} complete");
                
                var downloading = prioritized.Where(f => f.BitField.PercentComplete > 0 && f.BitField.PercentComplete < 100).Take(3);
                foreach (var file in downloading)
                {
                    Console.WriteLine($"       ⬇ {Path.GetFileName(file.Path)}: {file.BitField.PercentComplete:F2}%");
                }
            }
        }
        
        Console.WriteLine();
        Console.WriteLine("  Press any key to continue...");
        Console.ReadKey(intercept: true);
    }

    private static async Task StartAllNotStartedAsync(List<TorrentFileInfo> torrents)
    {
        var notStarted = torrents.Where(t => t.Status == TorrentStatus.NotStarted).ToList();
        
        if (notStarted.Count == 0)
        {
            Console.WriteLine("  No torrents to start.");
            return;
        }
        
        Console.WriteLine($"  Starting {notStarted.Count} torrents...");
        
        foreach (var torrent in notStarted)
        {
            await StartDownloadAsync(torrent.FullPath);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:F1} {sizes[order]}";
    }

    private static void EnsureDirectories()
    {
        if (!Directory.Exists(WatchFolder)) Directory.CreateDirectory(WatchFolder);
        if (!Directory.Exists(DownloadFolder)) Directory.CreateDirectory(DownloadFolder);
    }

    /// <summary>
    /// Try to acquire a lock on a torrent file. Returns true if lock acquired.
    /// </summary>
    private static bool TryAcquireLock(string torrentFilePath)
    {
        var lockFile = torrentFilePath + LockExtension;
        
        try
        {
            // Check if lock exists and is valid
            if (File.Exists(lockFile))
            {
                var lockContent = File.ReadAllText(lockFile);
                Console.WriteLine($"🔒 Lock exists for {Path.GetFileName(torrentFilePath)}: {lockContent}");
                return false;
            }
            
            // Create lock file with our instance ID and timestamp
            var lockData = $"{InstanceId}|{DateTime.UtcNow:O}";
            File.WriteAllText(lockFile, lockData);
            
            lock (OurLocks)
            {
                OurLocks.Add(torrentFilePath);
            }
            
            Console.WriteLine($"🔓 Acquired lock for {Path.GetFileName(torrentFilePath)}");
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
    private static void ReleaseLock(string torrentFilePath)
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
                    Console.WriteLine($"🔓 Released lock for {Path.GetFileName(torrentFilePath)}");
                }
            }
            
            lock (OurLocks)
            {
                OurLocks.Remove(torrentFilePath);
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine($"⚠️ Could not release lock: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Release all locks we hold
    /// </summary>
    private static void ReleaseAllLocks()
    {
        List<string> locksToRelease;
        lock (OurLocks)
        {
            locksToRelease = OurLocks.ToList();
        }
        
        foreach (var torrentPath in locksToRelease)
        {
            ReleaseLock(torrentPath);
        }
    }

    /// <summary>
    /// Mark a torrent as completed by creating a .completed marker file
    /// </summary>
    private static void MarkAsCompleted(string torrentFilePath)
    {
        var completedFile = torrentFilePath + CompletedExtension;
        
        try
        {
            if (!File.Exists(completedFile))
            {
                File.WriteAllText(completedFile, $"{InstanceId}|{DateTime.UtcNow:O}");
                Console.WriteLine($"✅ Marked as completed: {Path.GetFileName(torrentFilePath)}");
            }
            
            // Remove from active downloads
            lock (ActiveDownloads)
            {
                ActiveDownloads.Remove(torrentFilePath);
            }
            
            // Release the lock
            ReleaseLock(torrentFilePath);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"⚠️ Could not mark as completed: {ex.Message}");
        }
    }

    private static async void OnTorrentFileCreated(object sender, FileSystemEventArgs e)
    {
        // Skip lock and completed files
        if (e.FullPath.EndsWith(LockExtension) || e.FullPath.EndsWith(CompletedExtension)) return;
        
        // Small delay to ensure file handle is released by the writer
        await Task.Delay(1000);
        
        // Notify user - they can choose to start it from the menu
        Console.WriteLine($"\n📥 New torrent detected: {Path.GetFileName(e.FullPath)}");
        Console.WriteLine("   Press 'R' to refresh the menu and select it.");
    }

    private static async Task StartDownloadAsync(string torrentFilePath)
    {
        // Skip lock files
        if (torrentFilePath.EndsWith(LockExtension)) return;
        
        // Try to acquire lock
        if (!TryAcquireLock(torrentFilePath))
        {
            Console.WriteLine($"⏭️ Skipping {Path.GetFileName(torrentFilePath)} - locked by another process");
            return;
        }
        
        try
        {
            Console.WriteLine($"Found torrent: {Path.GetFileName(torrentFilePath)}");

            var torrent = await Torrent.LoadAsync(torrentFilePath);
            var manager = await Engine.AddAsync(torrent, DownloadFolder);

            // --- NEW: Selective Download Logic ---
            // 1. Default all to DoNotDownload
            foreach (var file in manager.Files)
            {
                await manager.SetFilePriorityAsync(file, Priority.Normal);
            }

            // 2. Prioritize Schema/SQL files AND specific data files we want to inspect
            var filesToDownload = manager.Files
                .Where(f => (f.Path.EndsWith("schema.sql.gz", StringComparison.OrdinalIgnoreCase) || 
                             f.Path.EndsWith("create.sql.gz", StringComparison.OrdinalIgnoreCase) ||
                             (f.Length < 1024 * 1024 && f.Path.EndsWith(".sql.gz", StringComparison.OrdinalIgnoreCase)) ||
                             // Prioritize the ISBN DB files so we can inspect them
                             f.Path.Contains("isbndb_isbns", StringComparison.OrdinalIgnoreCase) ||
                             // Prioritize a smaller data file for testing import logic
                             f.Path.Contains("rgb_for_lookup", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (filesToDownload.Any())
            {
                Console.WriteLine($"Found {filesToDownload.Count} small/schema files to download:");
                foreach (var file in filesToDownload)
                {
                    Console.WriteLine($" - Prioritizing: {file.Path}");
                    await manager.SetFilePriorityAsync(file, Priority.Highest);
                }
            }
            else
            {
                Console.WriteLine("No schema files found. Downloading everything (default behavior).");
                foreach (var file in manager.Files)
                {
                    await manager.SetFilePriorityAsync(file, Priority.Normal);
                }
            }
            // -------------------------------------

            manager.TorrentStateChanged += (s, e) =>
            {
                Console.WriteLine($"[{torrent.Name}] State: {e.NewState}");
                
                // Mark as completed when seeding or stopped after completion
                if (e.NewState == TorrentState.Seeding)
                {
                    MarkAsCompleted(torrentFilePath);
                }
            };

            // Track this download
            lock (ActiveDownloads)
            {
                ActiveDownloads[torrentFilePath] = manager;
            }

            await manager.StartAsync();
            Console.WriteLine($"[{torrent.Name}] Download started...");

            // --- NEW: Background Progress Monitor ---
            _ = Task.Run(async () =>
            {
                while (manager.State != TorrentState.Stopped)
                {
                    var prioritized = manager.Files.Where(f => f.Priority == Priority.Highest).ToList();
                    var completedCount = prioritized.Count(f => f.BitField.PercentComplete >= 100.0);
                    var downloadingFiles = prioritized.Where(f => f.BitField.PercentComplete > 0.0 && f.BitField.PercentComplete < 100).ToList();
                    var downloadingCount = downloadingFiles.Count;
                    var totalCount = prioritized.Count;
                    
                    // Find our specific target file for inspection
                    var targetFile = prioritized.FirstOrDefault(f => f.Path.Contains("isbndb_isbns.00000.dat.gz", StringComparison.OrdinalIgnoreCase));                    
                    Console.WriteLine($"[{DateTime.Now:T}] Status: {manager.State}");
                    Console.WriteLine($"  {torrent.Name}]");
                    //Console.WriteLine($"  {targetFile}");
                    Console.WriteLine($"  Speed: {manager.Monitor.DownloadRate / 1024.0:N0} KB/s (Peers: {manager.Peers.Available})");
                    Console.WriteLine($"  Downloading: {downloadingCount}");
                    
                    if (downloadingFiles.Any())
                    {
                        Console.WriteLine($"  Files currently downloading:");
                        foreach (var file in downloadingFiles)
                        {
                            Console.WriteLine($"    - {Path.GetFileName(file.Path)}: {file.BitField.PercentComplete:F6}%");
                        }
                    }
                    
                    Console.WriteLine($"  Prioritized Files: {completedCount}/{totalCount} completed.");
                    
                    if (targetFile != null)
                    {
                        double percent = targetFile.BitField.PercentComplete;
                        Console.WriteLine($"  Target File ({Path.GetFileName(targetFile.Path)}): {percent:F6}%");
                    }
                    Console.WriteLine("------------------------------------------------");

                    await Task.Delay(5000); // Update every 5 seconds
                }
                
                // Download complete or stopped - release lock
                ReleaseLock(torrentFilePath);
            });
            // ----------------------------------------
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting download for {torrentFilePath}: {ex.Message}");
            ReleaseLock(torrentFilePath); // Release lock on error
        }
    }
}

public enum TorrentStatus
{
    NotStarted,
    Downloading,
    Completed,
    Locked
}

public class TorrentFileInfo
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public long Size { get; set; }
    public TorrentStatus Status { get; set; }
}
