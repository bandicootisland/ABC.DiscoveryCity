using System.Text.Json;

namespace ABC.DiscoveryCity.MariaDB;

/// <summary>
/// Tracks synchronization progress for each table.
/// Persisted to JSON for resume capability.
/// </summary>
public class SyncState
{
    private readonly string _stateFile;
    private Dictionary<string, TableSyncProgress> _progress;

    public SyncState(string stateFile)
    {
        _stateFile = stateFile;
        _progress = new Dictionary<string, TableSyncProgress>();
        Load();
    }

    public TableSyncProgress GetProgress(string tableName)
    {
        if (!_progress.TryGetValue(tableName, out var progress))
        {
            progress = new TableSyncProgress
            {
                TableName = tableName,
                LastSyncedId = 0,
                TotalRowsSynced = 0,
                LastRunUtc = null,
                Status = SyncStatus.NotStarted
            };
            _progress[tableName] = progress;
        }
        return progress;
    }

    public void UpdateProgress(string tableName, long lastSyncedId, long totalRowsSynced)
    {
        var progress = GetProgress(tableName);
        progress.LastSyncedId = lastSyncedId;
        progress.TotalRowsSynced = totalRowsSynced;  // Set directly, not add
        progress.LastRunUtc = DateTime.UtcNow;
        progress.Status = SyncStatus.InProgress;
        Save();
    }

    public void MarkCompleted(string tableName, long totalRows)
    {
        var progress = GetProgress(tableName);
        progress.TotalRowsSynced = totalRows;
        progress.LastRunUtc = DateTime.UtcNow;
        progress.Status = SyncStatus.Completed;
        Save();
    }

    public void MarkFailed(string tableName, string error)
    {
        var progress = GetProgress(tableName);
        progress.LastRunUtc = DateTime.UtcNow;
        progress.Status = SyncStatus.Failed;
        progress.LastError = error;
        Save();
    }

    public void Reset(string tableName)
    {
        if (_progress.ContainsKey(tableName))
        {
            _progress[tableName] = new TableSyncProgress
            {
                TableName = tableName,
                LastSyncedId = 0,
                TotalRowsSynced = 0,
                LastRunUtc = null,
                Status = SyncStatus.NotStarted
            };
            Save();
        }
    }

    public IEnumerable<TableSyncProgress> GetAllProgress() => _progress.Values;

    private void Load()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var json = File.ReadAllText(_stateFile);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, TableSyncProgress>>(json);
                if (loaded != null)
                {
                    _progress = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not load sync state: {ex.Message}");
            _progress = new Dictionary<string, TableSyncProgress>();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_stateFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_progress, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_stateFile, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not save sync state: {ex.Message}");
        }
    }
}

public class TableSyncProgress
{
    public string TableName { get; set; } = "";
    public long LastSyncedId { get; set; }
    public long TotalRowsSynced { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public SyncStatus Status { get; set; }
    public string? LastError { get; set; }
}

public enum SyncStatus
{
    NotStarted,
    InProgress,
    Completed,
    Failed
}
