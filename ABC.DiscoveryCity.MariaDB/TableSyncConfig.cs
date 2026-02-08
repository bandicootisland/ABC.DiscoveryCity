namespace ABC.DiscoveryCity.MariaDB;

/// <summary>
/// Configuration for syncing a specific table.
/// Defines primary key, chunk size, and column mappings.
/// </summary>
public class TableSyncConfig
{
    public string SourceDatabase { get; set; } = "libgen_new";
    public string TargetDatabase { get; set; } = "allthethings";
    public string TableName { get; set; } = "";
    public string PrimaryKeyColumn { get; set; } = "id";
    public int ChunkSize { get; set; } = 10000;
    
    /// <summary>
    /// If null, all columns are synced. Otherwise, only these columns.
    /// </summary>
    public List<string>? Columns { get; set; }
    
    /// <summary>
    /// If true, uses simple INSERT (faster for empty target tables).
    /// If false, uses INSERT ... ON DUPLICATE KEY UPDATE.
    /// </summary>
    public bool UseSimpleInsert { get; set; } = false;

    /// <summary>
    /// Pre-defined configurations for known tables.
    /// </summary>
    public static Dictionary<string, TableSyncConfig> KnownTables => new()
    {
        ["libgenli_files"] = new TableSyncConfig
        {
            TableName = "libgenli_files",
            PrimaryKeyColumn = "f_id",
            ChunkSize = 10000
        },
        ["libgenli_editions"] = new TableSyncConfig
        {
            TableName = "libgenli_editions",
            PrimaryKeyColumn = "e_id",
            ChunkSize = 5000  // Larger rows, smaller chunks
        },
        ["libgenli_editions_to_files"] = new TableSyncConfig
        {
            TableName = "libgenli_editions_to_files",
            PrimaryKeyColumn = "ef_id",
            ChunkSize = 20000  // Small rows, larger chunks
        },
        ["libgenli_files_add_descr"] = new TableSyncConfig
        {
            TableName = "libgenli_files_add_descr",
            PrimaryKeyColumn = "fad_id",
            ChunkSize = 5000
        },
        ["libgenli_editions_add_descr"] = new TableSyncConfig
        {
            TableName = "libgenli_editions_add_descr",
            PrimaryKeyColumn = "ead_id",
            ChunkSize = 5000
        },
        ["libgenli_series"] = new TableSyncConfig
        {
            TableName = "libgenli_series",
            PrimaryKeyColumn = "s_id",
            ChunkSize = 10000
        },
        ["libgenli_series_add_descr"] = new TableSyncConfig
        {
            TableName = "libgenli_series_add_descr",
            PrimaryKeyColumn = "s_add_id",
            ChunkSize = 10000
        },
        ["libgenli_publishers"] = new TableSyncConfig
        {
            TableName = "libgenli_publishers",
            PrimaryKeyColumn = "p_id",
            ChunkSize = 10000
        },
        ["libgenli_elem_descr"] = new TableSyncConfig
        {
            TableName = "libgenli_elem_descr",
            PrimaryKeyColumn = "id",
            ChunkSize = 10000
        }
    };

    public static TableSyncConfig GetConfig(string tableName)
    {
        if (KnownTables.TryGetValue(tableName, out var config))
        {
            return config;
        }
        
        // Default config for unknown tables
        return new TableSyncConfig
        {
            TableName = tableName,
            PrimaryKeyColumn = "id",
            ChunkSize = 10000
        };
    }
}
