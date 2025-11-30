using System.Data;
using System.Diagnostics;
using System.Text;
using MySqlConnector;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// Moves data between MariaDB instances in manageable chunks.
/// Supports resume via SyncState and uses UPSERT for idempotency.
/// </summary>
public class ChunkedDataMover
{
    private readonly string _sourceConnectionString;
    private readonly string _targetConnectionString;
    private readonly SyncState _syncState;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;

    public ChunkedDataMover(
        string sourceConnectionString, 
        string targetConnectionString, 
        SyncState syncState,
        int maxRetries = 3,
        int retryDelayMs = 5000)
    {
        _sourceConnectionString = sourceConnectionString;
        _targetConnectionString = targetConnectionString;
        _syncState = syncState;
        _maxRetries = maxRetries;
        _retryDelayMs = retryDelayMs;
    }

    /// <summary>
    /// Synchronize a table from source to target using chunked reads and upserts.
    /// </summary>
    public async Task SyncTableAsync(TableSyncConfig config, CancellationToken cancellationToken = default)
    {
        var progress = _syncState.GetProgress(config.TableName);
        var startId = progress.LastSyncedId;
        
        Console.WriteLine($"\n{'=',-60}");
        Console.WriteLine($"Syncing: {config.SourceDatabase}.{config.TableName}");
        Console.WriteLine($"Target:  {config.TargetDatabase}.{config.TableName}");
        Console.WriteLine($"PK:      {config.PrimaryKeyColumn}");
        Console.WriteLine($"Chunk:   {config.ChunkSize:N0} rows");
        Console.WriteLine($"Resume:  Starting from ID > {startId:N0}");
        Console.WriteLine($"{'=',-60}\n");

        // Get total count from source
        long totalSourceRows = await GetSourceCountAsync(config);
        Console.WriteLine($"Source has {totalSourceRows:N0} total rows");

        // Get columns from source table
        var columns = await GetTableColumnsAsync(config);
        if (columns.Count == 0)
        {
            Console.WriteLine("ERROR: Could not retrieve column information.");
            return;
        }
        Console.WriteLine($"Columns: {columns.Count} ({string.Join(", ", columns.Take(5))}...)");

        // Ensure target table exists with same schema
        await EnsureTargetTableExistsAsync(config);

        // Check if target is empty (for optimization)
        long targetRows = await GetTargetCountAsync(config);
        bool useSimpleInsert = config.UseSimpleInsert || targetRows == 0;
        Console.WriteLine($"Target has {targetRows:N0} rows. Using {(useSimpleInsert ? "INSERT" : "UPSERT")} mode.");

        var stopwatch = Stopwatch.StartNew();
        long rowsSyncedThisRun = 0;
        long rowsSkippedThisRun = 0;
        long initialRowsSynced = progress.TotalRowsSynced; // Capture initial value
        long lastId = startId;
        int chunkNumber = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                chunkNumber++;
                
                // Read chunk from source
                var (rows, maxIdInChunk, skipped) = await ReadChunkAsync(config, columns, lastId);
                rowsSkippedThisRun += skipped;
                
                if (rows.Count == 0)
                {
                    Console.WriteLine("\nNo more rows to sync. Completed!");
                    _syncState.MarkCompleted(config.TableName, initialRowsSynced + rowsSyncedThisRun);
                    break;
                }

                // Write chunk to target
                int written = await WriteChunkWithRetryAsync(config, columns, rows, useSimpleInsert);
                
                rowsSyncedThisRun += written;
                lastId = maxIdInChunk;
                
                // Update state (don't add to TotalRowsSynced, set it directly)
                _syncState.UpdateProgress(config.TableName, lastId, initialRowsSynced + rowsSyncedThisRun);

                // Progress display
                long totalSynced = initialRowsSynced + rowsSyncedThisRun;
                double pct = totalSourceRows > 0 ? (double)totalSynced / totalSourceRows * 100 : 0;
                double rowsPerSec = rowsSyncedThisRun / stopwatch.Elapsed.TotalSeconds;
                long remaining = totalSourceRows - totalSynced;
                TimeSpan eta = rowsPerSec > 0 ? TimeSpan.FromSeconds(remaining / rowsPerSec) : TimeSpan.Zero;
                
                Console.Write($"\rChunk {chunkNumber}: {totalSynced:N0} / {totalSourceRows:N0} ({pct:F1}%) | {rowsPerSec:N0} rows/sec | ETA: {eta:hh\\:mm\\:ss}   ");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n\nERROR: {ex.Message}");
            _syncState.MarkFailed(config.TableName, ex.Message);
            throw;
        }

        stopwatch.Stop();
        Console.WriteLine($"\n\nSync completed in {stopwatch.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Total rows synced this run: {rowsSyncedThisRun:N0}");
        if (rowsSkippedThisRun > 0)
        {
            Console.WriteLine($"Rows skipped due to errors: {rowsSkippedThisRun:N0}");
            Console.WriteLine($"See error log: {_errorLogFile}");
        }
    }

    private async Task<long> GetSourceCountAsync(TableSyncConfig config)
    {
        await using var conn = new MySqlConnection(_sourceConnectionString);
        await conn.OpenAsync();
        
        var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{config.TableName}`", conn);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private async Task<long> GetTargetCountAsync(TableSyncConfig config)
    {
        try
        {
            await using var conn = new MySqlConnection(_targetConnectionString);
            await conn.OpenAsync();
            
            var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{config.TableName}`", conn);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }
        catch
        {
            return 0; // Table doesn't exist yet
        }
    }

    private async Task<List<string>> GetTableColumnsAsync(TableSyncConfig config)
    {
        var columns = new List<string>();
        
        await using var conn = new MySqlConnection(_sourceConnectionString);
        await conn.OpenAsync();
        
        var cmd = new MySqlCommand(
            $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS " +
            $"WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION", conn);
        cmd.Parameters.AddWithValue("@db", config.SourceDatabase);
        cmd.Parameters.AddWithValue("@table", config.TableName);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }
        
        // Filter if specific columns are requested
        if (config.Columns != null && config.Columns.Count > 0)
        {
            columns = columns.Where(c => config.Columns.Contains(c)).ToList();
        }
        
        return columns;
    }

    private async Task EnsureTargetTableExistsAsync(TableSyncConfig config)
    {
        await using var sourceConn = new MySqlConnection(_sourceConnectionString);
        await sourceConn.OpenAsync();
        
        // Get CREATE TABLE statement from source
        var showCmd = new MySqlCommand($"SHOW CREATE TABLE `{config.TableName}`", sourceConn);
        await using var reader = await showCmd.ExecuteReaderAsync();
        
        string createStatement = "";
        if (await reader.ReadAsync())
        {
            createStatement = reader.GetString(1);
        }
        reader.Close();
        
        if (string.IsNullOrEmpty(createStatement))
        {
            Console.WriteLine("WARNING: Could not get CREATE TABLE statement.");
            return;
        }

        // Try to create on target (will fail silently if exists)
        await using var targetConn = new MySqlConnection(_targetConnectionString);
        await targetConn.OpenAsync();
        
        try
        {
            // Modify to CREATE TABLE IF NOT EXISTS
            createStatement = createStatement.Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS");
            var createCmd = new MySqlCommand(createStatement, targetConn);
            await createCmd.ExecuteNonQueryAsync();
            Console.WriteLine("Target table verified/created.");
        }
        catch (MySqlException ex)
        {
            // Table might already exist with slightly different schema
            Console.WriteLine($"Note: {ex.Message}");
        }
    }

    private async Task<(List<object?[]> rows, long maxId, int skippedRows)> ReadChunkAsync(
        TableSyncConfig config, 
        List<string> columns, 
        long afterId)
    {
        var rows = new List<object?[]>();
        long maxId = afterId;
        int skippedRows = 0;
        
        await using var conn = new MySqlConnection(_sourceConnectionString);
        await conn.OpenAsync();
        
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));
        var sql = $"SELECT {columnList} FROM `{config.TableName}` " +
                  $"WHERE `{config.PrimaryKeyColumn}` > @afterId " +
                  $"ORDER BY `{config.PrimaryKeyColumn}` LIMIT @limit";
        
        var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@afterId", afterId);
        cmd.Parameters.AddWithValue("@limit", config.ChunkSize);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        int pkIndex = columns.IndexOf(config.PrimaryKeyColumn);
        
        while (await reader.ReadAsync())
        {
            try
            {
                var row = new object?[columns.Count];
                long? currentRowId = null;
                
                for (int i = 0; i < columns.Count; i++)
                {
                    try
                    {
                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                    catch (Exception colEx)
                    {
                        // Try to get raw value as string for problematic columns
                        try
                        {
                            row[i] = reader.GetString(i);
                        }
                        catch
                        {
                            // Last resort: set to null and log
                            row[i] = null;
                            LogError(config.TableName, currentRowId, columns[i], colEx.Message);
                        }
                    }
                }
                
                rows.Add(row);
                
                // Track max ID
                if (pkIndex >= 0 && row[pkIndex] != null)
                {
                    currentRowId = Convert.ToInt64(row[pkIndex]);
                    if (currentRowId > maxId) maxId = currentRowId.Value;
                }
            }
            catch (Exception rowEx)
            {
                // Skip this entire row if we can't read it
                skippedRows++;
                LogError(config.TableName, null, "ROW_READ", rowEx.Message);
                
                // Still need to track max ID for progress - try to get PK directly
                try
                {
                    if (pkIndex >= 0 && !reader.IsDBNull(pkIndex))
                    {
                        long rowId = reader.GetInt64(pkIndex);
                        if (rowId > maxId) maxId = rowId;
                    }
                }
                catch { /* Ignore if we can't even get the PK */ }
            }
        }
        
        return (rows, maxId, skippedRows);
    }

    private static readonly string _errorLogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "sync_errors.log");
    
    private void LogError(string tableName, long? rowId, string column, string error)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var rowIdStr = rowId?.ToString() ?? "?";
            var logLine = $"[{timestamp}] {tableName} | Row {rowIdStr} | {column} | {error}";
            
            File.AppendAllText(_errorLogFile, logLine + Environment.NewLine);
        }
        catch
        {
            // Don't fail sync because of logging issues
        }
    }

    private async Task<int> WriteChunkWithRetryAsync(
        TableSyncConfig config, 
        List<string> columns, 
        List<object?[]> rows,
        bool useSimpleInsert)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await WriteChunkAsync(config, columns, rows, useSimpleInsert);
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                attempt++;
                Console.WriteLine($"\nRetry {attempt}/{_maxRetries} after error: {ex.Message}");
                await Task.Delay(_retryDelayMs);
            }
        }
    }

    private async Task<int> WriteChunkAsync(
        TableSyncConfig config, 
        List<string> columns, 
        List<object?[]> rows,
        bool useSimpleInsert)
    {
        if (rows.Count == 0) return 0;
        
        await using var conn = new MySqlConnection(_targetConnectionString);
        await conn.OpenAsync();
        
        var columnList = string.Join(", ", columns.Select(c => $"`{c}`"));
        
        // Build VALUES part
        var valuesSb = new StringBuilder();
        var parameters = new List<MySqlParameter>();
        int paramIndex = 0;
        
        for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
        {
            if (rowIdx > 0) valuesSb.Append(", ");
            
            valuesSb.Append('(');
            var row = rows[rowIdx];
            for (int colIdx = 0; colIdx < columns.Count; colIdx++)
            {
                if (colIdx > 0) valuesSb.Append(", ");
                
                var paramName = $"@p{paramIndex++}";
                valuesSb.Append(paramName);
                parameters.Add(new MySqlParameter(paramName, row[colIdx] ?? DBNull.Value));
            }
            valuesSb.Append(')');
        }
        
        string sql;
        if (useSimpleInsert)
        {
            sql = $"INSERT IGNORE INTO `{config.TableName}` ({columnList}) VALUES {valuesSb}";
        }
        else
        {
            // Build ON DUPLICATE KEY UPDATE part (exclude primary key)
            var updateParts = columns
                .Where(c => c != config.PrimaryKeyColumn)
                .Select(c => $"`{c}` = VALUES(`{c}`)");
            var updateClause = string.Join(", ", updateParts);
            
            sql = $"INSERT INTO `{config.TableName}` ({columnList}) VALUES {valuesSb} " +
                  $"ON DUPLICATE KEY UPDATE {updateClause}";
        }
        
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = 300; // 5 minutes
        cmd.Parameters.AddRange(parameters.ToArray());
        
        int affected = await cmd.ExecuteNonQueryAsync();
        return rows.Count; // Return rows attempted, not affected (upsert can return 2 for updates)
    }

    /// <summary>
    /// Get a quick status report of all tables.
    /// </summary>
    public async Task<List<TableStatus>> GetTableStatusAsync()
    {
        var result = new List<TableStatus>();
        
        await using var sourceConn = new MySqlConnection(_sourceConnectionString);
        await sourceConn.OpenAsync();
        
        await using var targetConn = new MySqlConnection(_targetConnectionString);
        await targetConn.OpenAsync();
        
        foreach (var tableName in TableSyncConfig.KnownTables.Keys)
        {
            var status = new TableStatus { TableName = tableName };
            
            try
            {
                var srcCmd = new MySqlCommand($"SELECT COUNT(*) FROM `{tableName}`", sourceConn);
                status.SourceRows = Convert.ToInt64(await srcCmd.ExecuteScalarAsync());
            }
            catch { status.SourceRows = -1; }
            
            try
            {
                var tgtCmd = new MySqlCommand($"SELECT COUNT(*) FROM `{tableName}`", targetConn);
                status.TargetRows = Convert.ToInt64(await tgtCmd.ExecuteScalarAsync());
            }
            catch { status.TargetRows = -1; }
            
            var progress = _syncState.GetProgress(tableName);
            status.LastSyncedId = progress.LastSyncedId;
            status.Status = progress.Status;
            
            result.Add(status);
        }
        
        return result;
    }
}

public class TableStatus
{
    public string TableName { get; set; } = "";
    public long SourceRows { get; set; }
    public long TargetRows { get; set; }
    public long LastSyncedId { get; set; }
    public SyncStatus Status { get; set; }
    
    public double SyncPercentage => SourceRows > 0 ? (double)TargetRows / SourceRows * 100 : 0;
}
