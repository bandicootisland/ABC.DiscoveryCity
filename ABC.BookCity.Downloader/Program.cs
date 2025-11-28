using MonoTorrent;
using MonoTorrent.Client;

namespace ABC.BookCity.Downloader;
class Program
{
    //H:\BookCity\Annas-Archive\annas-archive\data-imports\scripts\torrents\isbndb_2022_09.torrent
    private static string WatchFolder = @"H:\BookCity\Annas-Archive\annas-archive\data-imports\scripts\torrents\";    
    
    private static string DownloadFolder = @"H:\BookCity\Books";
    private static ClientEngine Engine;

    static async Task Main(string[] args)
    {
        Console.WriteLine("ABC.BookCity Downloader Service (NET 10.0)");
        Console.WriteLine("--------------------------------------------");

        EnsureDirectories();

        // Initialize MonoTorrent Engine
        var settings = new EngineSettings();
        Engine = new ClientEngine(settings);

        Console.WriteLine($"Watching for .torrent files in: {WatchFolder}");
        Console.WriteLine($"Downloads will be saved to:     {DownloadFolder}");

        // Set up FileSystemWatcher
        using var watcher = new FileSystemWatcher(WatchFolder, "*.torrent");
        watcher.Created += OnTorrentFileCreated;
        watcher.EnableRaisingEvents = true;

        // Process any existing files
        foreach (var file in Directory.GetFiles(WatchFolder, "*.torrent"))
        {
             await StartDownloadAsync(file); 
        }

        // ----------------------------------------------
        Console.WriteLine("Press Ctrl+C to exit...");
        
        // Setup cancellation for clean shutdown
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; // Prevent immediate process kill
            Console.WriteLine("\nStopping downloads... Please wait.");
            tcs.TrySetResult();
        };

        await tcs.Task;

        Console.WriteLine("Shutting down engine...");
        await Engine.StopAllAsync();
        Console.WriteLine("Engine stopped. Exiting.");
    }

    private static void EnsureDirectories()
    {
        if (!Directory.Exists(WatchFolder)) Directory.CreateDirectory(WatchFolder);
        if (!Directory.Exists(DownloadFolder)) Directory.CreateDirectory(DownloadFolder);
    }

    private static async void OnTorrentFileCreated(object sender, FileSystemEventArgs e)
    {
        // Small delay to ensure file handle is released by the writer
        await Task.Delay(1000);
        await StartDownloadAsync(e.FullPath);
    }

    private static async Task StartDownloadAsync(string torrentFilePath)
    {
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
            };

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
            });
            // ----------------------------------------
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error starting download for {torrentFilePath}: {ex.Message}");
        }
    }
}
