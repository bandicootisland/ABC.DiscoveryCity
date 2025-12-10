using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Net;
using ABC.BookCity.API.Models;
using Microsoft.AspNetCore.Hosting;

using MonoTorrent;
using MonoTorrent.Client;

namespace ABC.BookCity.API.Services
{
    public class ImportService : IImportService
    {
        private readonly string _rootPath;
        private readonly string _dataImportsPath;
        private readonly string _scriptsPath;
        private readonly string _downloadDir = @"H:\BookCity\Books"; // Centralized download config

        public ImportService(IWebHostEnvironment env)
        {
            _rootPath = env.ContentRootPath;
            // Assuming DataImports is at the solution level, one level up from the project
            // H:\Developer.BookCity\ABC.BookCity\ABC.BookCity.API (Project)
            // H:\Developer.BookCity\ABC.BookCity\DataImports (Data)
            _dataImportsPath = Path.GetFullPath(Path.Combine(_rootPath, "..", "DataImports"));
            
            // Prefer data-imports/scripts if it exists, otherwise fallback to scripts
            var preferredScriptsPath = Path.Combine(_dataImportsPath, "data-imports", "scripts");
            if (Directory.Exists(preferredScriptsPath))
            {
                _scriptsPath = preferredScriptsPath;
            }
            else
            {
                _scriptsPath = Path.Combine(_dataImportsPath, "scripts");
            }
        }

        public async Task<List<DatasetInfo>> GetAvailableDatasetsAsync()
        {
            var datasets = new List<DatasetInfo>();
            var scriptsDir = _scriptsPath;
            
            // 1. ISBNdb (Manual entry for now as it has custom logic)
            var isbnDbScriptPath = Path.Combine(scriptsDir, "helpers", "process_isbndb.py");
            datasets.Add(new DatasetInfo
            {
                Name = "ISBNdb",
                Description = "ISBN database dump (Sept 2022)",
                ScriptPath = isbnDbScriptPath,
                ScriptContent = File.Exists(isbnDbScriptPath) ? await File.ReadAllTextAsync(isbnDbScriptPath) : "Script not found",
                FileName = "isbndb_2022_09.jsonl",
                IsDownloaded = File.Exists(Path.Combine(_downloadDir, "isbndb_2022_09.jsonl")),
                IsProcessed = File.Exists(Path.Combine(_downloadDir, "isbndb_processed.csv")),
                IsImported = false
            });

            // 2. Scan for other download scripts
            if (Directory.Exists(scriptsDir))
            {
                var downloadScripts = Directory.GetFiles(scriptsDir, "download_*.sh");
                foreach (var script in downloadScripts)
                {
                    var name = Path.GetFileNameWithoutExtension(script).Replace("download_", "");
                    if (name == "pilimi_isbndb") continue; // Skip the one we manually added

                    var scriptContent = await File.ReadAllTextAsync(script);
                    var targetPaths = DetermineTargetPaths(scriptContent, name);
                    
                    var isDownloaded = false;
                    var fileName = "Unknown (Check Script)";

                    if (targetPaths.Any())
                    {
                        isDownloaded = targetPaths.All(p => 
                        {
                            if (File.Exists(p)) return true;
                            if (Directory.Exists(p))
                            {
                                // Must contain files to be considered downloaded
                                return Directory.EnumerateFileSystemEntries(p).Any();
                            }
                            return false;
                        });
                        fileName = string.Join(", ", targetPaths.Select(Path.GetFileName));
                    }

                    datasets.Add(new DatasetInfo
                    {
                        Name = name,
                        Description = $"Auto-detected script: {Path.GetFileName(script)}",
                        ScriptPath = script,
                        ScriptContent = scriptContent,
                        FileName = fileName,
                        IsDownloaded = isDownloaded,
                        IsProcessed = false,
                        IsImported = false
                    });
                }
            }

            return datasets;
        }

        public async Task RunDownloadAsync(string datasetName, Action<string> onLog)
        {
            // Special case for ISBNdb (legacy/manual)
            if (datasetName == "ISBNdb")
            {
                var torrentFile = Path.Combine(_scriptsPath, "torrents", "isbndb_2022_09.torrent");
                onLog($"Starting download for {datasetName}...");
                onLog($"Torrent: {torrentFile}");
                onLog($"Destination: {_downloadDir}");

                await RunCommandAsync("webtorrent", $"\"{torrentFile}\" --out \"{_downloadDir}\"", onLog);
                return;
            }

            // Generic Handler
            var scriptPath = Path.Combine(_scriptsPath, $"download_{datasetName}.sh");
            if (!File.Exists(scriptPath)) 
            {
                onLog($"Script not found: {scriptPath}");
                return;
            }

            var scriptContent = await File.ReadAllTextAsync(scriptPath);
            
            // Strategy 1: WebTorrent (Updated to use TorrentDownloader)
            if (scriptContent.Contains("webtorrent") || scriptContent.Contains(".torrent"))
            {
                string? torrentFileName = null;
                string? torrentUrl = null;

                // Check for curl/wget download of the torrent file first
                var urlMatch = Regex.Match(scriptContent, @"(https?://\S+\.torrent)");
                if (urlMatch.Success)
                {
                    torrentUrl = urlMatch.Groups[1].Value;
                    torrentFileName = Path.GetFileName(new Uri(torrentUrl).LocalPath);
                }

                // Fallback: look for webtorrent command argument
                if (string.IsNullOrEmpty(torrentFileName))
                {
                    // Matches: webtorrent ... download filename.torrent OR webtorrent filename.torrent
                    var match = Regex.Match(scriptContent, @"webtorrent\s+.*?(?:download\s+)?(\S+\.torrent)");
                    if (match.Success)
                    {
                        torrentFileName = Path.GetFileName(match.Groups[1].Value);
                    }
                }

                if (!string.IsNullOrEmpty(torrentFileName))
                {
                    var torrentPath = Path.Combine(_scriptsPath, "torrents", torrentFileName);
                    
                    // Download torrent file if missing and we have a URL
                    if (!File.Exists(torrentPath) && !string.IsNullOrEmpty(torrentUrl))
                    {
                        onLog($"Downloading torrent file from {torrentUrl}...");
                        try 
                        {
                            using var client = new HttpClient();
                            var bytes = await client.GetByteArrayAsync(torrentUrl);
                            // Ensure directory exists
                            var torrentDir = Path.GetDirectoryName(torrentPath);
                            if (torrentDir != null) Directory.CreateDirectory(torrentDir);
                            
                            await File.WriteAllBytesAsync(torrentPath, bytes);
                            onLog($"Saved torrent file to {torrentPath}");
                        }
                        catch (Exception ex)
                        {
                            onLog($"Failed to download torrent file: {ex.Message}");
                            return;
                        }
                    }

                    if (File.Exists(torrentPath))
                    {
                        onLog($"Starting Torrent Download: {torrentFileName}");
                        onLog($"Destination: {_downloadDir}");

                        try 
                        {
                            var downloader = new TorrentDownloader();
                            var torrent = await downloader.LoadAsync(torrentPath);
                            var manager = await downloader.ManageAsync(torrent, _downloadDir);
                            
                            if (manager.State == TorrentState.Stopped || manager.State == TorrentState.Paused)
                            {
                                await manager.StartAsync();
                            }

                            var lastProgress = -1.0;
                            // Loop until seeding or stopped
                            while (manager.State != TorrentState.Seeding && manager.State != TorrentState.Stopped && manager.State != TorrentState.Error)
                            {
                                // Update log every 2 seconds or if progress changes significantly
                                if (Math.Abs(manager.Progress - lastProgress) > 0.1 || manager.State == TorrentState.Hashing || manager.State == TorrentState.Downloading || manager.State == TorrentState.Metadata)
                                {
                                    onLog($"Progress: {manager.Progress:F2}% - State: {manager.State} - Speed: {manager.Monitor.DownloadRate / 1024.0 / 1024.0:F2} MB/s - Peers: {manager.Peers.Available} (Conn: {manager.OpenConnections})");
                                    lastProgress = manager.Progress;
                                }
                                await Task.Delay(2000);
                            }

                            if (manager.State == TorrentState.Error)
                            {
                                onLog($"Torrent Error: {manager.Error?.Reason.ToString() ?? "Unknown Error"}");
                            }
                            else
                            {
                                onLog("Download finished (Seeding/Stopped).");
                            }

                            await manager.StopAsync();
                        }
                        catch (Exception ex)
                        {
                            onLog($"Torrent Execution Error: {ex.Message}");
                        }
                    }
                    else
                    {
                        onLog($"Torrent file not found: {torrentPath}");
                    }
                    return;
                }
            }

            // Strategy 2: Aria2c / HTTP Direct Download
            if (scriptContent.Contains("aria2c"))
            {
                // Matches: aria2c ... 'http://url' OR aria2c ... http://url
                // Captures the URL, handling optional single/double quotes
                var matches = Regex.Matches(scriptContent, @"aria2c\s+.*?['""]?(https?://[^'""\s]+)['""]?");
                if (matches.Count > 0)
                {
                    onLog($"Detected {matches.Count} HTTP download(s).");
                    using var client = new HttpClient();
                    // Add User-Agent to mimic a browser, as some sites block requests without it
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                    // Increase timeout for large files
                    client.Timeout = TimeSpan.FromHours(24);
                    
                    foreach (Match m in matches)
                    {
                        var url = m.Groups[1].Value;
                        var fileName = Path.GetFileName(new Uri(url).LocalPath);
                        var destPath = Path.Combine(_downloadDir, fileName);

                        onLog($"Downloading {url}...");
                        onLog($"Saving to {destPath}");
                        
                        try 
                        {
                            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            
                            if (!response.IsSuccessStatusCode)
                            {
                                onLog($"Error: Server returned {response.StatusCode} {response.ReasonPhrase}");
                                continue;
                            }

                            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                            onLog($"File Size: {(totalBytes != -1 ? (totalBytes/1024.0/1024.0).ToString("F2") + " MB" : "Unknown")}");

                            using (var s = await response.Content.ReadAsStreamAsync())
                            using (var fs = new FileStream(destPath, FileMode.Create))
                            {
                                var buffer = new byte[81920];
                                int bytesRead;
                                long totalRead = 0;
                                var lastLog = DateTime.Now;

                                while ((bytesRead = await s.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await fs.WriteAsync(buffer, 0, bytesRead);
                                    totalRead += bytesRead;

                                    if ((DateTime.Now - lastLog).TotalSeconds > 2)
                                    {
                                        var percent = totalBytes != -1 ? (double)totalRead / totalBytes * 100 : -1;
                                        var mbRead = totalRead / 1024.0 / 1024.0;
                                        onLog($"Downloaded {mbRead:F2} MB {(percent != -1 ? $"({percent:F2}%)" : "")}");
                                        lastLog = DateTime.Now;
                                    }
                                }
                            }
                            onLog($"Successfully downloaded {fileName}.");
                        }
                        catch (Exception ex)
                        {
                            onLog($"Error downloading {url}: {ex.Message}");
                        }
                    }
                    return;
                }
            }

            // Strategy 3: Rclone (FTP)
            if (scriptContent.Contains("rclone") && scriptContent.Contains(":ftp:"))
            {
                // Parse FTP details
                // Example: rclone copy :ftp:/upload/dbbackup/ ... --ftp-host=ftp.libgen.bz --ftp-user=anonymous
                
                var hostMatch = Regex.Match(scriptContent, @"--ftp-host=(\S+)");
                var userMatch = Regex.Match(scriptContent, @"--ftp-user=(\S+)");
                var pathMatch = Regex.Match(scriptContent, @":ftp:(\S+)\s+");

                if (hostMatch.Success && pathMatch.Success)
                {
                    var host = hostMatch.Groups[1].Value;
                    var user = userMatch.Success ? userMatch.Groups[1].Value : "anonymous";
                    var sourcePath = pathMatch.Groups[1].Value; // e.g. /upload/dbbackup/
                    var pass = "anonymous@example.com"; // Default for anonymous

                    onLog($"Detected FTP download via rclone script.");
                    onLog($"Host: {host}");
                    onLog($"Source: {sourcePath}");

                    // Create subdirectory if needed (script does mkdir libgenli_db)
                    var targetDir = Path.Combine(_downloadDir, datasetName + "_db"); 
                    if (scriptContent.Contains("mkdir libgenli_db"))
                    {
                         targetDir = Path.Combine(_downloadDir, "libgenli_db");
                    }
                    
                    Directory.CreateDirectory(targetDir);
                    onLog($"Target Directory: {targetDir}");

                    await DownloadFtpDirectoryAsync(host, user, pass, sourcePath, targetDir, onLog);
                    return;
                }
            }

            // Strategy 4: HathiTrust Special Case
            if (scriptContent.Contains("hathi_file_list.json"))
            {
                onLog("Detected HathiTrust download script.");
                await DownloadHathiTrustAsync(onLog);
                return;
            }

            // Strategy 5: Curl with Proxies (Libgen.li)
            if (scriptContent.Contains("curl") && scriptContent.Contains("socks5-hostname"))
            {
                onLog("Detected Curl with Proxies download script.");
                await DownloadCurlProxiesAsync(scriptContent, onLog);
                return;
            }

            onLog($"No supported download method (webtorrent/aria2c/rclone-ftp) found in {Path.GetFileName(scriptPath)}.");
        }

        private async Task DownloadFtpDirectoryAsync(string host, string user, string pass, string path, string localDir, Action<string> onLog)
        {
            try 
            {
                var request = (FtpWebRequest)WebRequest.Create($"ftp://{host}{path}");
                request.Method = WebRequestMethods.Ftp.ListDirectory;
                request.Credentials = new NetworkCredential(user, pass);
                
                onLog($"Listing files from ftp://{host}{path}...");
                
                using var response = (FtpWebResponse)await request.GetResponseAsync();
                using var stream = response.GetResponseStream();
                using var reader = new StreamReader(stream);
                
                var files = new List<string>();
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line) && line != "." && line != "..")
                    {
                        // Some servers return full path, some just name.
                        // We want just the name.
                        var name = Path.GetFileName(line);
                        files.Add(name);
                    }
                }

                onLog($"Found {files.Count} files.");

                foreach (var file in files)
                {
                    // Ensure path ends with / before appending file
                    var cleanPath = path.EndsWith("/") ? path : path + "/";
                    var remoteUri = $"ftp://{host}{cleanPath}{file}";
                    var localPath = Path.Combine(localDir, file);

                    // Check if file exists and compare size
                    long remoteSize = -1;
                    try 
                    {
                        var sizeRequest = (FtpWebRequest)WebRequest.Create(remoteUri);
                        sizeRequest.Method = WebRequestMethods.Ftp.GetFileSize;
                        sizeRequest.Credentials = new NetworkCredential(user, pass);
                        using var sizeResponse = (FtpWebResponse)await sizeRequest.GetResponseAsync();
                        remoteSize = sizeResponse.ContentLength;
                    }
                    catch
                    {
                        onLog($"Warning: Could not get size for {file}. Proceeding with download check.");
                    }

                    if (File.Exists(localPath))
                    {
                        var localInfo = new FileInfo(localPath);
                        if (remoteSize != -1 && localInfo.Length == remoteSize)
                        {
                            onLog($"Skipping {file} (already downloaded, size matches).");
                            continue;
                        }
                        else
                        {
                            onLog($"Overwriting {file} (Local: {localInfo.Length}, Remote: {remoteSize})...");
                        }
                    }
                    else
                    {
                        onLog($"Downloading {file}...");
                    }
                    
                    try
                    {
                        var downloadRequest = (FtpWebRequest)WebRequest.Create(remoteUri);
                        downloadRequest.Method = WebRequestMethods.Ftp.DownloadFile;
                        downloadRequest.Credentials = new NetworkCredential(user, pass);

                        using var downloadResponse = (FtpWebResponse)await downloadRequest.GetResponseAsync();
                        using var sourceStream = downloadResponse.GetResponseStream();
                        using var targetStream = new FileStream(localPath, FileMode.Create);
                        
                        await sourceStream.CopyToAsync(targetStream);
                        onLog($"Downloaded {file}.");
                    }
                    catch (Exception ex)
                    {
                        onLog($"Failed to download {file}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                onLog($"FTP Error: {ex.Message}");
            }
        }

        public async Task RunProcessAsync(string datasetName, Action<string> onLog)
        {
            if (datasetName == "ISBNdb")
            {
                var scriptPath = Path.Combine(_scriptsPath, "helpers", "process_isbndb.py");
                onLog($"Processing {datasetName}...");
                onLog($"Script: {scriptPath}");
                await RunCommandAsync("python", $"\"{scriptPath}\"", onLog);
                return;
            }

            if (datasetName == "libgenli")
            {
                onLog($"Processing {datasetName} (Extraction)...");
                
                // Check both specific subdir and root dir
                var downloadDir = Path.Combine(_downloadDir, "libgenli_db");
                var rarFile = Directory.Exists(downloadDir) 
                    ? Directory.GetFiles(downloadDir, "libgen_new*.part001.rar").FirstOrDefault() 
                    : null;

                if (rarFile == null)
                {
                    // Fallback to root download dir
                    downloadDir = _downloadDir;
                    rarFile = Directory.GetFiles(downloadDir, "libgen_new*.part001.rar").FirstOrDefault();
                }
                
                if (rarFile == null)
                {
                    onLog($"Error: Main archive (libgen_new*.part001.rar) not found in {_downloadDir} or subdirectory.");
                    return;
                }

                onLog($"Found archive: {rarFile}");
                onLog("Attempting to extract using 7z (preferred) or unrar...");

                // Try 7z
                try 
                {
                    // 7z x archive.rar -oOutput -y
                    // We want to extract into _downloadDir so we get a 'libgen_new' folder there
                    await RunCommandAsync("7z", $"x \"{rarFile}\" -o\"{_downloadDir}\" -y", onLog);
                    onLog("Extraction with 7z completed.");
                    return;
                }
                catch
                {
                    onLog("7z failed or not found. Trying unrar...");
                }

                // Try unrar
                try
                {
                    // unrar x archive.rar Destination\ -y
                    await RunCommandAsync("unrar", $"x \"{rarFile}\" \"{_downloadDir}\\\" -y", onLog);
                    onLog("Extraction with unrar completed.");
                    return;
                }
                catch
                {
                    onLog("Error: Extraction failed. Please ensure 7z or unrar is installed and in your PATH.");
                }
                return;
            }

            // Generic Process Handler (Placeholder)
            // For libgenrs, the load script handles unrar. 
            // We might want to look for load_{datasetName}.sh and see if it has unrar commands.
            var loadScriptPath = Path.Combine(_scriptsPath, $"load_{datasetName}.sh");
            if (File.Exists(loadScriptPath))
            {
                var content = await File.ReadAllTextAsync(loadScriptPath);
                
                // Check for unrar
                if (content.Contains("unrar"))
                {
                    onLog("Detected 'unrar' in load script. Attempting to extract archives...");
                    // Find .rar files in download dir matching the dataset? 
                    // Or parse the script again.
                    // Script says: unrar e libgen.rar
                    
                    var matches = Regex.Matches(content, @"unrar\s+e\s+(\S+\.rar)");
                    foreach (Match m in matches)
                    {
                        var rarFile = m.Groups[1].Value;
                        var rarPath = Path.Combine(_downloadDir, rarFile);
                        
                        if (File.Exists(rarPath))
                        {
                            onLog($"Extracting {rarFile}...");
                            // Assuming 'unrar' is in PATH or we use a library. 
                            // Windows usually doesn't have unrar in PATH unless installed.
                            // We can try running it.
                            await RunCommandAsync("unrar", $"e -y \"{rarPath}\"", onLog); 
                        }
                        else
                        {
                            onLog($"Archive not found: {rarPath}");
                        }
                    }
                    return;
                }
            }

            onLog($"No automated processing logic available for {datasetName}.");
        }

        public async Task RunImportAsync(string datasetName, Action<string> onLog)
        {
            if (datasetName == "ISBNdb")
            {
                onLog($"Importing {datasetName} into MariaDB...");
                
                var csvPath = @"H:\BookCity\Books\isbndb_processed.csv";
                var containerName = "bookcity-mariadb";

                onLog("Step 1: Copying CSV to container...");
                await RunCommandAsync("docker", $"cp \"{csvPath}\" {containerName}:/tmp/isbndb_processed.csv", onLog);

                onLog("Step 2: Creating table...");
                var createTableSql = "CREATE DATABASE IF NOT EXISTS allthethings; USE allthethings; DROP TABLE IF EXISTS isbndb_isbns; CREATE TABLE isbndb_isbns (isbn13 CHAR(13) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, isbn10 CHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL, json longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL CHECK (json_valid(json)), PRIMARY KEY (isbn13,isbn10), KEY isbn10 (isbn10)) ENGINE=MyISAM;";
                await RunCommandAsync("docker", $"exec -i {containerName} mariadb -u root -ppassword -e \"{createTableSql}\"", onLog);

                onLog("Step 3: Loading data...");
                var loadSql = "USE allthethings; LOAD DATA INFILE '/tmp/isbndb_processed.csv' INTO TABLE isbndb_isbns FIELDS TERMINATED BY '\\t' ENCLOSED BY '' ESCAPED BY '';";
                var tempSqlFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempSqlFile, loadSql);
                
                await RunCommandAsync("docker", $"cp \"{tempSqlFile}\" {containerName}:/tmp/import.sql", onLog);
                await RunCommandAsync("docker", $"exec -i {containerName} mariadb -u root -ppassword -e \"source /tmp/import.sql\"", onLog);
                
                onLog("Import completed.");
                return;
            }

            if (datasetName == "libgenli")
            {
                onLog($"Importing {datasetName} into MariaDB...");
                var containerName = "bookcity-mariadb";
                var extractedDir = Path.Combine(_downloadDir, "libgen_new");

                if (!Directory.Exists(extractedDir))
                {
                    onLog($"Error: Extracted directory {extractedDir} not found. Did you run Process?");
                    return;
                }

                onLog("Step 1: Copying database files to container (this may take a while)...");
                // Copy to /var/lib/mysql/libgen_new
                await RunCommandAsync("docker", $"cp \"{extractedDir}\" {containerName}:/var/lib/mysql/", onLog);

                onLog("Step 2: Fixing permissions...");
                await RunCommandAsync("docker", $"exec -u 0 {containerName} chown -R mysql:mysql /var/lib/mysql/libgen_new", onLog);

                onLog("Step 3: Repairing tables and dropping triggers...");
                // We'll run a combined SQL script or commands
                // 1. Repair
                await RunCommandAsync("docker", $"exec {containerName} mysqlcheck -u root -ppassword --auto-repair --check libgen_new", onLog);

                // 2. Drop Triggers (Simplified list for now, can expand)
                var dropTriggersSql = @"
                    USE libgen_new;
                    DROP TRIGGER IF EXISTS authors_before_ins_tr; 
                    DROP TRIGGER IF EXISTS authors_add_descr_before_ins_tr;
                    DROP TRIGGER IF EXISTS editions_before_ins_tr1;
                    DROP TRIGGER IF EXISTS files_before_ins_tr;
                    -- Add more as needed from the script
                ";
                
                var tempSqlFile = Path.GetTempFileName();
                await File.WriteAllTextAsync(tempSqlFile, dropTriggersSql);
                await RunCommandAsync("docker", $"cp \"{tempSqlFile}\" {containerName}:/tmp/drop_triggers.sql", onLog);
                await RunCommandAsync("docker", $"exec -i {containerName} mariadb -u root -ppassword -e \"source /tmp/drop_triggers.sql\"", onLog);

                onLog("Import completed.");
                return;
            }

            onLog($"No automated import logic available for {datasetName}.");
        }

        public async Task<string> GetReadmeAsync()
        {
            var path = Path.Combine(_dataImportsPath, "README.md");
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }
            return "README.md not found.";
        }

        private async Task RunCommandAsync(string fileName, string arguments, Action<string> onLog)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) onLog(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) onLog($"ERROR: {e.Data}"); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();
        }

        private async Task DownloadHathiTrustAsync(Action<string> onLog)
        {
            try
            {
                // 1. Download the file list
                var listUrl = "https://www.hathitrust.org/files/hathifiles/hathi_file_list.json";
                onLog($"Downloading file list from {listUrl}...");
                
                using var client = new HttpClient();
                // Add User-Agent
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
                
                var json = await client.GetStringAsync(listUrl);
                
                // 2. Parse JSON to find the URL
                // Logic: [.[]| select(.full == true)| select(.filename | startswith("hathi_full_"))]| sort_by(.filename)| last| .url
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                string? downloadUrl = null;
                string? filename = null;
                
                var items = root.EnumerateArray()
                    .Where(x => x.GetProperty("full").GetBoolean() == true && 
                                (x.GetProperty("filename").GetString() ?? "").StartsWith("hathi_full_"))
                    .OrderBy(x => x.GetProperty("filename").GetString())
                    .ToList();

                if (items.Any())
                {
                    var last = items.Last();
                    downloadUrl = last.GetProperty("url").GetString();
                    filename = "hathi_full.txt.gz"; // The script uses -o 'hathi_full.txt.gz'
                }

                if (string.IsNullOrEmpty(downloadUrl) || string.IsNullOrEmpty(filename))
                {
                    onLog("Could not find HathiTrust download URL in JSON.");
                    return;
                }

                onLog($"Found download URL: {downloadUrl}");
                
                // 3. Download the file
                var destPath = Path.Combine(_downloadDir, filename);
                onLog($"Downloading to {destPath}...");
                
                using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    onLog($"Error: Server returned {response.StatusCode}");
                    return;
                }
                
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                onLog($"File Size: {(totalBytes != -1 ? (totalBytes/1024.0/1024.0).ToString("F2") + " MB" : "Unknown")}");

                using var s = await response.Content.ReadAsStreamAsync();
                using var fs = new FileStream(destPath, FileMode.Create);
                
                var buffer = new byte[81920];
                int bytesRead;
                long totalRead = 0;
                var lastLog = DateTime.Now;

                while ((bytesRead = await s.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if ((DateTime.Now - lastLog).TotalSeconds > 2)
                    {
                        var percent = totalBytes != -1 ? (double)totalRead / totalBytes * 100 : -1;
                        var mbRead = totalRead / 1024.0 / 1024.0;
                        onLog($"Downloaded {mbRead:F2} MB {(percent != -1 ? $"({percent:F2}%)" : "")}");
                        lastLog = DateTime.Now;
                    }
                }
                
                onLog("Download finished.");
            }
            catch (Exception ex)
            {
                onLog($"HathiTrust Download Error: {ex.Message}");
            }
        }

        private List<string> DetermineTargetPaths(string scriptContent, string datasetName)
        {
            var paths = new List<string>();

            // 1. HathiTrust (Special Case)
            if (scriptContent.Contains("hathi_file_list.json"))
            {
                paths.Add(Path.Combine(_downloadDir, "hathi_full.txt.gz"));
                return paths;
            }

            // 2. Cleanup Heuristic (Primary Method)
            // Most scripts start with 'rm -rf /temp-dir/folder' or 'rm -f file' to ensure idempotency.
            // This is usually the most reliable indicator of the output.
            var rmMatches = Regex.Matches(scriptContent, @"rm\s+-(?:rf|f)\s+(?:/temp-dir/)?(\S+)");
            foreach (Match m in rmMatches)
            {
                var target = m.Groups[1].Value;
                if (target == "." || target == ".." || target.StartsWith("-") || target.Contains("*")) continue;
                
                var name = Path.GetFileName(target);
                if (!string.IsNullOrEmpty(name))
                {
                    // Ignore .torrent files as they are not the dataset itself
                    if (name.EndsWith(".torrent")) continue;

                    var potentialPath = Path.Combine(_downloadDir, name);
                    if (!paths.Any(p => p.Equals(potentialPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        paths.Add(potentialPath);
                    }
                }
            }

            // If we found targets via rm, return them.
            if (paths.Count > 0) return paths;

            // 3. Fallback: Parse Download Commands
            
            // Rclone / FTP
            if (scriptContent.Contains("rclone") && scriptContent.Contains(":ftp:"))
            {
                if (scriptContent.Contains("mkdir libgenli_db"))
                {
                    paths.Add(Path.Combine(_downloadDir, "libgenli_db"));
                }
                else
                {
                    paths.Add(Path.Combine(_downloadDir, datasetName + "_db"));
                }
            }

            // Aria2c
            var ariaOutputMatches = Regex.Matches(scriptContent, @"aria2c\s+.*?-o\s+['""]?([^'""\s]+)['""]?");
            foreach (Match m in ariaOutputMatches)
            {
                paths.Add(Path.Combine(_downloadDir, m.Groups[1].Value));
            }

            if (paths.Count == 0)
            {
                 var ariaUrlMatches = Regex.Matches(scriptContent, @"aria2c\s+.*?['""]?(https?://[^'""\s]+)['""]?");
                 foreach (Match m in ariaUrlMatches)
                 {
                     var url = m.Groups[1].Value;
                     try {
                        var uri = new Uri(url);
                        paths.Add(Path.Combine(_downloadDir, Path.GetFileName(uri.LocalPath)));
                     } catch {}
                 }
            }

            // Curl
            var curlOutputMatches = Regex.Matches(scriptContent, @"curl\s+.*?-o\s+['""]?([^'""\s]+)['""]?");
            foreach (Match m in curlOutputMatches)
            {
                paths.Add(Path.Combine(_downloadDir, m.Groups[1].Value));
            }

            // Curl -O (remote name)
            var curlRemoteMatches = Regex.Matches(scriptContent, @"curl\s+.*?-O\s+(\S+)");
            foreach (Match m in curlRemoteMatches)
            {
                var url = m.Groups[1].Value;
                url = url.TrimEnd('&');
                try {
                    var uri = new Uri(url);
                    paths.Add(Path.Combine(_downloadDir, Path.GetFileName(uri.LocalPath)));
                } catch {}
            }

            // WebTorrent
            var torrentMatches = Regex.Matches(scriptContent, @"webtorrent\s+.*?(?:download\s+)?(\S+\.torrent)");
            foreach (Match m in torrentMatches)
            {
                var torrentFile = m.Groups[1].Value;
                var name = Path.GetFileNameWithoutExtension(torrentFile);
                paths.Add(Path.Combine(_downloadDir, name));
            }

            // Filter out .torrent files again just in case
            paths = paths.Where(p => !p.EndsWith(".torrent")).ToList();

            return paths;
        }

        private async Task DownloadCurlProxiesAsync(string scriptContent, Action<string> onLog)
        {
            // Parse lines like: curl ... --socks5-hostname proxy -O url &
            // We look for -O followed by the URL
            var matches = Regex.Matches(scriptContent, @"curl\s+.*?-O\s+(\S+)");
            
            if (matches.Count == 0)
            {
                onLog("No curl download URLs found.");
                return;
            }

            onLog($"Found {matches.Count} files to download.");
            onLog("Note: Downloading sequentially and ignoring proxies (using local connection).");
            
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromHours(24);
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");

            foreach (Match match in matches)
            {
                var url = match.Groups[1].Value;
                // Remove trailing & if captured (though \S+ shouldn't capture it if there is a space)
                url = url.TrimEnd('&'); 
                
                var fileName = Path.GetFileName(new Uri(url).LocalPath);
                var destPath = Path.Combine(_downloadDir, fileName);

                if (File.Exists(destPath))
                {
                    onLog($"Skipping {fileName} (already exists).");
                    continue;
                }

                onLog($"Downloading {fileName}...");
                try
                {
                    // Note: We are ignoring the proxy for now and trying direct download.
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        onLog($"Error downloading {fileName}: {response.StatusCode}");
                        continue;
                    }
                    
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    using var s = await response.Content.ReadAsStreamAsync();
                    using var fs = new FileStream(destPath, FileMode.Create);
                    
                    var buffer = new byte[81920];
                    int bytesRead;
                    long totalRead = 0;
                    var lastLog = DateTime.Now;

                    while ((bytesRead = await s.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, bytesRead);
                        totalRead += bytesRead;

                        if ((DateTime.Now - lastLog).TotalSeconds > 2)
                        {
                            var percent = totalBytes != -1 ? (double)totalRead / totalBytes * 100 : -1;
                            var mbRead = totalRead / 1024.0 / 1024.0;
                            onLog($"Downloaded {mbRead:F2} MB {(percent != -1 ? $"({percent:F2}%)" : "")}");
                            lastLog = DateTime.Now;
                        }
                    }
                    
                    onLog($"Downloaded {fileName}.");
                }
                catch (Exception ex)
                {
                    onLog($"Failed to download {fileName}: {ex.Message}");
                }
                
                // Add a small delay to be nice
                await Task.Delay(1000);
            }
        }

        public async Task<string> SaveScriptAsync(DatasetInfo dataset)
        {
            // Target directory: H:\Developer.BookCity\ABC.BookCity\ABC.BookCity.Scripting
            // We can try to resolve it relative to _rootPath
            // _rootPath is ...\ABC.BookCity.API
            // Scripting is ...\ABC.BookCity.Scripting
            
            var scriptingDir = Path.GetFullPath(Path.Combine(_rootPath, "..", "ABC.BookCity.Scripting"));
            if (!Directory.Exists(scriptingDir))
            {
                Directory.CreateDirectory(scriptingDir);
            }

            // Sanitize filename
            var safeName = string.Join("_", dataset.Name.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"Download_{safeName}.cs";
            var filePath = Path.Combine(scriptingDir, fileName);

            await File.WriteAllTextAsync(filePath, dataset.CSharpContent);
            return filePath;
        }

        public async Task<string> GetScriptTemplateAsync(DatasetInfo dataset)
        {
            var scriptingDir = Path.GetFullPath(Path.Combine(_rootPath, "..", "ABC.BookCity.Scripting"));
            
            // TODO: Logic to select template based on dataset type (Curl vs Torrent)
            var templateFileName = "DownloadUsingCurl.cs";
            
            var templatePath = Path.Combine(scriptingDir, templateFileName);
            
            if (File.Exists(templatePath))
            {
                var content = await File.ReadAllTextAsync(templatePath);
                
                // Replace script location
                // Regex to find: var scriptlocation=@"...";
                var regex = new System.Text.RegularExpressions.Regex(@"var scriptlocation=@"".*?"";");
                var newLine = $"var scriptlocation=@\"{dataset.ScriptPath}\";";
                
                return regex.Replace(content, newLine);
            }
            
            return "// Template not found at " + templatePath;
        }
    }
}
