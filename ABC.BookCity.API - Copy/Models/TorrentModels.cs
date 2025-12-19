namespace ABC.BookCity.API.Models;

public class TorrentDownloadStatus
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
    public double Progress { get; set; }
    public double DownloadSpeed { get; set; }
    public int Peers { get; set; }
    public long TotalSize { get; set; }
    public long Downloaded { get; set; }
    public string? ProcessingBy { get; set; } // Which downloader has the lock
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
}

public enum TorrentFileStatus
{
    NotStarted,
    Processing,
    Completed
}

public class TorrentEngineStatus
{
    public bool IsRunning { get; set; }
    public string WatchFolder { get; set; } = "";
    public string DownloadFolder { get; set; } = "";
    public List<TorrentDownloadStatus> Downloads { get; set; } = new();
}

public class TorrentFilesResponse
{
    public string WatchFolder { get; set; } = "";
    public string DownloadFolder { get; set; } = "";
    public List<TorrentFileInfo> Files { get; set; } = new();
}

public class AddTorrentRequest
{
    public string? Url { get; set; }
    public byte[]? TorrentData { get; set; }
    public string? FileName { get; set; }
    public bool AutoStart { get; set; } = false; // Default to NOT auto-starting
}
