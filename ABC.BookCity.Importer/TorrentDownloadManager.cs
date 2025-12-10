using System.IO.Compression;

namespace ABC.BookCity.Importer;

/// <summary>
/// Manages completed torrent downloads and determines appropriate import actions.
/// Scans the Torrents folder for .completed files and matches them to Downloads folder.
/// </summary>
public class TorrentDownloadManager
{
    private readonly string _torrentsFolder;
    private readonly string _downloadsFolder;

    public TorrentDownloadManager(string torrentsFolder, string downloadsFolder)
    {
        _torrentsFolder = torrentsFolder;
        _downloadsFolder = downloadsFolder;
    }

    /// <summary>
    /// Represents a completed torrent download with detected type information.
    /// </summary>
    public class CompletedDownload
    {
        public required string TorrentFile { get; init; }
        public required string TorrentName { get; init; }
        public required string DownloadPath { get; init; }
        public required DownloadType Type { get; init; }
        public required bool DownloadExists { get; init; }
        public long SizeBytes { get; init; }
        public int FileCount { get; init; }
        public bool IsEstimated { get; init; }
        public string? Description { get; init; }
    }

    public enum DownloadType
    {
        Unknown,
        JsonlSeekableZst,      // .jsonl.seekable.zst - Compressed JSON Lines metadata
        TarZst,                // .tar.zst - Compressed tar archive
        AacidDataFolder,       // annas_archive_data__aacid__* folder - ZIP content files
        AacidMetaFolder,       // annas_archive_meta__aacid__* folder - Metadata
        BookFilesFolder,       // Numeric folders (f_XXXXXXX, XXXXXXX) - Book files (PDF, CBR, etc.)
        DatGzFile,             // .dat.gz - CSV/TSV data files
        SqlGzFile,             // .sql.gz - Schema files
    }

    /// <summary>
    /// Scans for completed torrent downloads and returns information about each.
    /// </summary>
    public List<CompletedDownload> GetCompletedDownloads()
    {
        var results = new List<CompletedDownload>();

        if (!Directory.Exists(_torrentsFolder))
        {
            Console.WriteLine($"Torrents folder not found: {_torrentsFolder}");
            return results;
        }

        var completedFiles = Directory.GetFiles(_torrentsFolder, "*.completed")
            .OrderBy(f => f)
            .ToList();

        foreach (var completedFile in completedFiles)
        {
            var torrentName = Path.GetFileName(completedFile)
                .Replace(".torrent.completed", "")
                .Replace(".completed", "");

            // Determine expected download path
            string downloadPath = DetermineDownloadPath(torrentName);
            bool exists = File.Exists(downloadPath) || Directory.Exists(downloadPath);
            var type = DetectDownloadType(torrentName, downloadPath, exists);

            long size = 0;
            int fileCount = 0;
            bool isEstimated = false;

            if (exists)
            {
                if (File.Exists(downloadPath))
                {
                    size = new FileInfo(downloadPath).Length;
                    fileCount = 1;
                }
                else if (Directory.Exists(downloadPath))
                {
                    // Fast count using EnumerateFiles (doesn't load all into memory)
                    fileCount = Directory.EnumerateFiles(downloadPath, "*", SearchOption.AllDirectories).Take(50001).Count();
                    
                    if (fileCount <= 1000)
                    {
                        // Small folder - get exact size
                        size = Directory.EnumerateFiles(downloadPath, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
                    }
                    else
                    {
                        // Large folder - estimate from first 100 files
                        var sampleFiles = Directory.EnumerateFiles(downloadPath, "*", SearchOption.AllDirectories).Take(100).ToList();
                        if (sampleFiles.Count > 0)
                        {
                            var avgSize = sampleFiles.Average(f => new FileInfo(f).Length);
                            size = (long)(avgSize * fileCount);
                            isEstimated = true;
                        }
                    }
                    
                    // Cap display at 50000+
                    if (fileCount > 50000) fileCount = 50000;
                }
            }

            results.Add(new CompletedDownload
            {
                TorrentFile = completedFile,
                TorrentName = torrentName,
                DownloadPath = downloadPath,
                Type = type,
                DownloadExists = exists,
                SizeBytes = size,
                FileCount = fileCount,
                IsEstimated = isEstimated,
                Description = GetTypeDescription(type)
            });
        }

        return results;
    }

    private string DetermineDownloadPath(string torrentName)
    {
        // Check if it's a file (has extension like .zst, .gz)
        if (torrentName.Contains('.'))
        {
            return Path.Combine(_downloadsFolder, torrentName);
        }
        
        // Otherwise it's likely a folder
        // Handle f_XXXXXXX pattern -> XXXXXXX folder
        if (torrentName.StartsWith("f_"))
        {
            return Path.Combine(_downloadsFolder, torrentName.Substring(2));
        }

        return Path.Combine(_downloadsFolder, torrentName);
    }

    private DownloadType DetectDownloadType(string torrentName, string downloadPath, bool exists)
    {
        // Check file extensions first
        if (torrentName.EndsWith(".jsonl.seekable.zst"))
            return DownloadType.JsonlSeekableZst;

        if (torrentName.EndsWith(".tar.zst"))
            return DownloadType.TarZst;

        if (torrentName.EndsWith(".dat.gz"))
            return DownloadType.DatGzFile;

        if (torrentName.EndsWith(".sql.gz"))
            return DownloadType.SqlGzFile;

        // Check folder patterns
        if (torrentName.StartsWith("annas_archive_data__aacid__"))
            return DownloadType.AacidDataFolder;

        if (torrentName.StartsWith("annas_archive_meta__aacid__"))
            return DownloadType.AacidMetaFolder;

        // Check for numeric folder pattern (f_XXXXXXX or just XXXXXXX)
        var folderName = torrentName.StartsWith("f_") ? torrentName.Substring(2) : torrentName;
        if (long.TryParse(folderName, out _))
            return DownloadType.BookFilesFolder;

        return DownloadType.Unknown;
    }

    private string GetTypeDescription(DownloadType type)
    {
        return type switch
        {
            DownloadType.JsonlSeekableZst => "JSONL metadata (compressed, needs zstd)",
            DownloadType.TarZst => "TAR archive (compressed, needs extraction)",
            DownloadType.AacidDataFolder => "Content files (ZIP archives)",
            DownloadType.AacidMetaFolder => "Metadata files",
            DownloadType.BookFilesFolder => "Book files (PDF, CBR, etc.)",
            DownloadType.DatGzFile => "Data file (CSV/TSV, gzipped)",
            DownloadType.SqlGzFile => "Schema file (SQL, gzipped)",
            _ => "Unknown format"
        };
    }

    /// <summary>
    /// Get a suggested loader/action for a download type.
    /// </summary>
    public string GetSuggestedAction(CompletedDownload download)
    {
        return download.Type switch
        {
            DownloadType.JsonlSeekableZst when download.TorrentName.Contains("worldcat") 
                => "Use WorldCatJsonLoader (J option)",
            DownloadType.JsonlSeekableZst when download.TorrentName.Contains("hathitrust")
                => "JSONL HathiTrust metadata - needs HathiTrustJsonLoader",
            DownloadType.JsonlSeekableZst 
                => "JSONL metadata - may need custom loader",
            
            DownloadType.AacidDataFolder when download.TorrentName.Contains("hathitrust")
                => "HathiTrust content ZIPs - catalog only (no DB import)",
            DownloadType.AacidDataFolder 
                => "Content ZIP files - catalog only (no DB import)",
            
            DownloadType.BookFilesFolder 
                => "Book files - catalog only (no DB import)",
            
            DownloadType.DatGzFile when download.TorrentName.Contains("aarecords_codes")
                => "Use AarecordsCodesLoader (C option)",
            DownloadType.DatGzFile 
                => "Data file - use appropriate loader",
            
            DownloadType.TarZst 
                => "Needs extraction first (tar + zstd)",
            
            _ => "Manual inspection required"
        };
    }

    /// <summary>
    /// Detect the specific data source from the torrent name for routing to correct loader.
    /// </summary>
    public string? DetectDataSource(string torrentName)
    {
        // Extract the source type from patterns like:
        // annas_archive_meta__aacid__hathitrust_files__...
        // annas_archive_data__aacid__worldcat__...

        if (torrentName.Contains("__aacid__"))
        {
            var parts = torrentName.Split("__aacid__");
            if (parts.Length > 1)
            {
                var sourcePart = parts[1];
                // Extract up to the next __ or timestamp
                var endIdx = sourcePart.IndexOf("__");
                if (endIdx > 0)
                {
                    return sourcePart.Substring(0, endIdx);
                }
            }
        }

        // Check for specific patterns
        if (torrentName.Contains("hathitrust")) return "hathitrust";
        if (torrentName.Contains("worldcat")) return "worldcat";
        if (torrentName.Contains("libgen")) return "libgen";
        if (torrentName.Contains("aarecords_codes")) return "aarecords_codes";

        return null;
    }
}
