using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MySqlConnector;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// Loads data from MyDumper .dat.gz files (CSV format) into MariaDB.
/// Uses streaming to handle large files without loading into memory.
/// Supports resume via SyncState and uses chunked inserts.
/// </summary>
public class GzipCsvLoader
{
    private readonly string _targetConnectionString;
    private readonly SyncState _syncState;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;
    private readonly string _basePath;

    private static readonly string _errorLogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "csv_loader_errors.log");

    public GzipCsvLoader(
        string targetConnectionString,
        SyncState syncState,
        string basePath,
        int maxRetries = 3,
        int retryDelayMs = 5000)
    {
        _targetConnectionString = targetConnectionString;
        _syncState = syncState;
        _basePath = basePath;
        _maxRetries = maxRetries;
        _retryDelayMs = retryDelayMs;
    }

    /// <summary>
    /// Load a .dat.gz file (or multiple parts) into the target database.
    /// </summary>
    public async Task LoadFileAsync(CsvFileConfig config, CancellationToken cancellationToken = default)
    {
        // Get list of data files to process
        var dataFiles = config.DataFiles.Count > 0 
            ? config.DataFiles 
            : new List<string> { Path.Combine(_basePath, config.FileName) };
        
        // Verify files exist
        foreach (var f in dataFiles)
        {
            if (!File.Exists(f))
            {
                Console.WriteLine($"ERROR: File not found: {f}");
                return;
            }
        }

        var progress = _syncState.GetProgress(config.TableName);
        long startLine = progress.LastSyncedId; // We use LastSyncedId to track global line number
        
        // Calculate total size for progress
        long totalSize = dataFiles.Sum(f => new FileInfo(f).Length);
        
        Console.WriteLine($"\n{'=',-60}");
        Console.WriteLine($"Loading: {config.TableName}");
        Console.WriteLine($"Files:   {dataFiles.Count} part(s)");
        Console.WriteLine($"Target:  {config.TargetDatabase}.{config.TableName}");
        Console.WriteLine($"Chunk:   {config.ChunkSize:N0} rows");
        Console.WriteLine($"Resume:  Starting from line {startLine:N0}");
        Console.WriteLine($"{'=',-60}\n");
        Console.WriteLine($"Total size: {totalSize / 1024.0 / 1024.0:F2} MB (compressed)");

        // Read schema file to get columns
        var columns = await GetColumnsFromSchemaOrHeaderAsync(config, dataFiles[0]);
        if (columns.Count == 0)
        {
            Console.WriteLine("ERROR: Could not determine columns.");
            return;
        }
        Console.WriteLine($"Columns: {columns.Count} ({string.Join(", ", columns.Take(5))}...)");

        // Ensure target table exists
        await EnsureTargetTableExistsAsync(config);

        // Check target row count
        long targetRows = await GetTargetCountAsync(config);
        bool useSimpleInsert = config.UseSimpleInsert || targetRows == 0;
        Console.WriteLine($"Target has {targetRows:N0} rows. Using {(useSimpleInsert ? "INSERT IGNORE" : "UPSERT")} mode.");

        var stopwatch = Stopwatch.StartNew();
        long rowsSyncedThisRun = 0;
        long rowsSkippedThisRun = 0;
        long initialRowsSynced = progress.TotalRowsSynced;
        long globalLine = 0;
        long bytesProcessed = 0;
        int chunkNumber = 0;
        var chunkRows = new List<object?[]>();

        try
        {
            foreach (var filePath in dataFiles)
            {
                if (cancellationToken.IsCancellationRequested) break;
                
                var fileInfo = new FileInfo(filePath);
                Console.WriteLine($"\nProcessing: {Path.GetFileName(filePath)} ({fileInfo.Length / 1024.0 / 1024.0:F2} MB)");
                
                await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 
                    bufferSize: 65536);
                await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzipStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                    bufferSize: 65536);

                string? line;
                bool isFirstLine = true;

                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    globalLine++;

                    // Skip header if first line of first file contains column names
                    if (isFirstLine)
                    {
                        isFirstLine = false;
                        if (config.HasHeader)
                        {
                            var headerCols = ParseCsvLine(line);
                            if (headerCols.Count > 0 && headerCols[0] == columns[0])
                            {
                                continue; // Skip header
                            }
                        }
                    }

                    // Skip lines we've already processed
                    if (globalLine <= startLine)
                    {
                        continue;
                    }

                    // Parse CSV line
                    try
                    {
                        var values = ParseCsvLine(line);
                        if (values.Count != columns.Count)
                        {
                            LogError(config.TableName, globalLine, "COLUMN_COUNT", 
                                $"Expected {columns.Count} columns, got {values.Count}");
                            rowsSkippedThisRun++;
                            continue;
                        }

                        var row = new object?[columns.Count];
                        for (int i = 0; i < values.Count; i++)
                        {
                            row[i] = ConvertValue(values[i]);
                        }
                        chunkRows.Add(row);
                    }
                    catch (Exception ex)
                    {
                        LogError(config.TableName, globalLine, "PARSE", ex.Message);
                        rowsSkippedThisRun++;
                        continue;
                    }

                    // Write chunk when full
                    if (chunkRows.Count >= config.ChunkSize)
                    {
                        chunkNumber++;
                        int written = await WriteChunkWithRetryAsync(config, columns, chunkRows, useSimpleInsert);
                        rowsSyncedThisRun += written;
                        
                        _syncState.UpdateProgress(config.TableName, globalLine, initialRowsSynced + rowsSyncedThisRun);
                        
                        // Progress display
                        double pct = (bytesProcessed + fileStream.Position) * 100.0 / totalSize;
                        double rowsPerSec = rowsSyncedThisRun / Math.Max(0.1, stopwatch.Elapsed.TotalSeconds);
                        long remaining = (long)((totalSize - bytesProcessed - fileStream.Position) / 
                            Math.Max(1, (bytesProcessed + fileStream.Position) / stopwatch.Elapsed.TotalSeconds));
                        var eta = TimeSpan.FromSeconds(remaining);
                        
                        Console.Write($"\rChunk {chunkNumber}: {rowsSyncedThisRun:N0} loaded | {pct:F1}% | {rowsPerSec:N0}/s | ETA: {eta:hh\\:mm\\:ss}   ");
                        
                        chunkRows.Clear();
                    }
                }
                
                bytesProcessed += fileInfo.Length;
            }

            // Write remaining rows
            if (chunkRows.Count > 0)
            {
                chunkNumber++;
                int written = await WriteChunkWithRetryAsync(config, columns, chunkRows, useSimpleInsert);
                rowsSyncedThisRun += written;
                _syncState.UpdateProgress(config.TableName, globalLine, initialRowsSynced + rowsSyncedThisRun);
            }

            _syncState.MarkCompleted(config.TableName, initialRowsSynced + rowsSyncedThisRun);
            Console.WriteLine("\n\nLoad completed!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n\nERROR at line {globalLine}: {ex.Message}");
            _syncState.MarkFailed(config.TableName, ex.Message);
            throw;
        }

        stopwatch.Stop();
        Console.WriteLine($"Completed in {stopwatch.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Total rows loaded: {rowsSyncedThisRun:N0}");
        if (rowsSkippedThisRun > 0)
        {
            Console.WriteLine($"Rows skipped due to errors: {rowsSkippedThisRun:N0}");
            Console.WriteLine($"See error log: {_errorLogFile}");
        }
    }

    /// <summary>
    /// Parse a CSV line handling quoted fields and escaped quotes.
    /// MyDumper format: values are quoted with ", NULL is \N, quotes escaped as \"
    /// </summary>
    private List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    // Escape sequence
                    char next = line[i + 1];
                    switch (next)
                    {
                        case '"':
                            current.Append('"');
                            i += 2;
                            continue;
                        case '\\':
                            current.Append('\\');
                            i += 2;
                            continue;
                        case 'n':
                            current.Append('\n');
                            i += 2;
                            continue;
                        case 'r':
                            current.Append('\r');
                            i += 2;
                            continue;
                        case 't':
                            current.Append('\t');
                            i += 2;
                            continue;
                        case 'N':
                            // \N inside quotes is literal \N
                            current.Append("\\N");
                            i += 2;
                            continue;
                        default:
                            current.Append(c);
                            i++;
                            continue;
                    }
                }
                else if (c == '"')
                {
                    // End of quoted field
                    inQuotes = false;
                    i++;
                    continue;
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                    i++;
                }
                else if (c == ',')
                {
                    result.Add(current.ToString());
                    current.Clear();
                    i++;
                }
                else if (c == '\\' && i + 1 < line.Length && line[i + 1] == 'N')
                {
                    // \N outside quotes means NULL
                    current.Append("\\N");
                    i += 2;
                }
                else
                {
                    current.Append(c);
                    i++;
                }
            }
        }

        // Add last field
        result.Add(current.ToString());

        return result;
    }

    /// <summary>
    /// Convert string value to proper type for database.
    /// </summary>
    private object? ConvertValue(string value)
    {
        // NULL value
        if (value == "\\N")
            return null;

        // Return as string - let MySqlConnector handle type conversion
        return value;
    }

    private async Task<List<string>> GetColumnsFromSchemaOrHeaderAsync(CsvFileConfig config, string datFilePath)
    {
        // Try to read from accompanying -schema.sql.gz file
        // Handle multi-part files: allthethings.table.00001.dat.gz -> allthethings.table-schema.sql.gz
        var dir = Path.GetDirectoryName(datFilePath)!;
        var schemaFile = Path.Combine(dir, $"allthethings.{config.TableName}-schema.sql.gz");
        
        if (File.Exists(schemaFile))
        {
            try
            {
                await using var fs = new FileStream(schemaFile, FileMode.Open, FileAccess.Read);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new StreamReader(gz);
                var content = await sr.ReadToEndAsync();

                var columns = ParseColumnsFromCreateTable(content);
                if (columns.Count > 0)
                    return columns;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not read schema file: {ex.Message}");
            }
        }

        // Fall back to reading header from data file
        if (config.HasHeader)
        {
            await using var fs = new FileStream(datFilePath, FileMode.Open, FileAccess.Read);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            var firstLine = await sr.ReadLineAsync();
            if (!string.IsNullOrEmpty(firstLine))
            {
                return ParseCsvLine(firstLine);
            }
        }

        // Use provided columns if available
        if (config.Columns != null && config.Columns.Count > 0)
            return config.Columns;

        return new List<string>();
    }

    private List<string> ParseColumnsFromCreateTable(string createStatement)
    {
        var columns = new List<string>();
        
        // Find the column definitions between CREATE TABLE ... ( and the closing )
        int startParen = createStatement.IndexOf('(');
        if (startParen < 0) return columns;

        int endParen = createStatement.LastIndexOf(')');
        if (endParen < startParen) return columns;

        var columnSection = createStatement.Substring(startParen + 1, endParen - startParen - 1);
        var lines = columnSection.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            
            // Skip constraints (PRIMARY KEY, UNIQUE, INDEX, KEY, CONSTRAINT)
            if (trimmed.StartsWith("PRIMARY", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("INDEX", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("KEY", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("FULLTEXT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Extract column name (first backtick-quoted identifier or first word)
            string colName;
            if (trimmed.StartsWith('`'))
            {
                int endBacktick = trimmed.IndexOf('`', 1);
                if (endBacktick > 1)
                {
                    colName = trimmed.Substring(1, endBacktick - 1);
                    columns.Add(colName);
                }
            }
            else
            {
                // No backticks - take first word
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && !parts[0].StartsWith("--"))
                {
                    colName = parts[0].TrimEnd(',');
                    columns.Add(colName);
                }
            }
        }

        return columns;
    }

    private async Task EnsureTargetTableExistsAsync(CsvFileConfig config)
    {
        // Try to read and execute the schema file
        var schemaFile = Path.Combine(_basePath, $"allthethings.{config.TableName}-schema.sql.gz");
        
        if (File.Exists(schemaFile))
        {
            try
            {
                await using var fs = new FileStream(schemaFile, FileMode.Open, FileAccess.Read);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var sr = new StreamReader(gz);
                var content = await sr.ReadToEndAsync();

                // Extract just the CREATE TABLE statement
                var createMatch = System.Text.RegularExpressions.Regex.Match(
                    content, 
                    @"CREATE TABLE[^;]+;", 
                    System.Text.RegularExpressions.RegexOptions.Singleline | 
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (createMatch.Success)
                {
                    var createSql = createMatch.Value.Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS");
                    
                    await using var conn = new MySqlConnection(_targetConnectionString);
                    await conn.OpenAsync();
                    
                    await using var cmd = new MySqlCommand(createSql, conn);
                    await cmd.ExecuteNonQueryAsync();
                    Console.WriteLine("Target table verified/created from schema file.");
                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not execute schema: {ex.Message}");
            }
        }

        Console.WriteLine("WARNING: No schema file found. Table must already exist.");
    }

    private async Task<long> GetTargetCountAsync(CsvFileConfig config)
    {
        try
        {
            await using var conn = new MySqlConnection(_targetConnectionString);
            await conn.OpenAsync();

            await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{config.TableName}`", conn);
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<int> WriteChunkWithRetryAsync(
        CsvFileConfig config,
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
        CsvFileConfig config,
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
            // Build ON DUPLICATE KEY UPDATE part
            var updateParts = columns
                .Where(c => c != config.PrimaryKeyColumn)
                .Select(c => $"`{c}` = VALUES(`{c}`)");
            var updateClause = string.Join(", ", updateParts);

            sql = $"INSERT INTO `{config.TableName}` ({columnList}) VALUES {valuesSb} " +
                  $"ON DUPLICATE KEY UPDATE {updateClause}";
        }

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = 300;
        cmd.Parameters.AddRange(parameters.ToArray());

        await cmd.ExecuteNonQueryAsync();
        return rows.Count;
    }

    private void LogError(string tableName, long lineNumber, string column, string error)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var logLine = $"[{timestamp}] {tableName} | Line {lineNumber} | {column} | {error}";
            File.AppendAllText(_errorLogFile, logLine + Environment.NewLine);
        }
        catch
        {
            // Don't fail load because of logging issues
        }
    }

    /// <summary>
    /// Get status of all CSV files in the base path.
    /// </summary>
    public async Task<List<CsvFileStatus>> GetFileStatusAsync()
    {
        var result = new List<CsvFileStatus>();
        var configs = CsvFileConfig.ScanDirectory(_basePath);

        foreach (var config in configs)
        {
            var status = new CsvFileStatus
            {
                FileName = config.FileName,
                TableName = config.TableName,
                FileCount = config.DataFiles.Count
            };

            // Calculate total size
            status.FileSizeMB = config.DataFiles.Sum(f => 
                File.Exists(f) ? new FileInfo(f).Length / 1024.0 / 1024.0 : 0);
            status.Exists = config.DataFiles.All(File.Exists);

            var progress = _syncState.GetProgress(config.TableName);
            status.LastLine = progress.LastSyncedId;
            status.RowsLoaded = progress.TotalRowsSynced;
            status.Status = progress.Status;

            // Get target count
            try
            {
                await using var conn = new MySqlConnection(_targetConnectionString);
                await conn.OpenAsync();
                await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{config.TableName}`", conn);
                status.TargetRows = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            catch
            {
                status.TargetRows = -1;
            }

            result.Add(status);
        }

        return result;
    }
}

/// <summary>
/// Configuration for a CSV file to load.
/// </summary>
public class CsvFileConfig
{
    public string FileName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string TargetDatabase { get; set; } = "allthethings";
    public string PrimaryKeyColumn { get; set; } = "aacid"; // Default for most tables
    public int ChunkSize { get; set; } = 5000;
    public bool HasHeader { get; set; } = true;
    public bool UseSimpleInsert { get; set; } = true;
    public List<string>? Columns { get; set; }
    
    /// <summary>
    /// For multi-part files, list of all .dat.gz files in order.
    /// </summary>
    public List<string> DataFiles { get; set; } = new();
    
    /// <summary>
    /// Scan a directory for all MyDumper .dat.gz files and return configs.
    /// Handles multi-part files (e.g., table.00001.dat.gz, table.00002.dat.gz)
    /// </summary>
    public static List<CsvFileConfig> ScanDirectory(string directory)
    {
        var configs = new Dictionary<string, CsvFileConfig>();
        
        var datFiles = Directory.GetFiles(directory, "*.dat.gz")
            .Where(f => !f.EndsWith("-schema.sql.gz"))
            .OrderBy(f => f)
            .ToList();
        
        foreach (var filePath in datFiles)
        {
            var fileName = Path.GetFileName(filePath);
            
            // Parse: allthethings.TABLE_NAME.NNNNN.dat.gz or allthethings.TABLE_NAME.dat.gz
            // Example: allthethings.libgenli_files.00001.dat.gz
            var parts = fileName.Split('.');
            if (parts.Length < 3) continue;
            
            // Extract table name (everything between first and last two parts)
            // allthethings.table_name.00001.dat.gz -> table_name
            // allthethings.table_name.dat.gz -> table_name (but .dat.gz counts as 2)
            string tableName;
            
            if (parts.Length >= 5 && parts[^2] == "dat" && parts[^1] == "gz")
            {
                // Check if second-to-last before .dat.gz is a number (multi-part)
                if (int.TryParse(parts[^3], out _))
                {
                    // Multi-part: allthethings.table_name.00001.dat.gz
                    tableName = string.Join(".", parts.Skip(1).Take(parts.Length - 4));
                }
                else
                {
                    // Single part: allthethings.table_name.dat.gz
                    tableName = string.Join(".", parts.Skip(1).Take(parts.Length - 3));
                }
            }
            else
            {
                continue; // Unknown format
            }
            
            if (!configs.TryGetValue(tableName, out var config))
            {
                config = new CsvFileConfig
                {
                    TableName = tableName,
                    FileName = fileName, // Will be overwritten for multi-part
                    PrimaryKeyColumn = GetDefaultPrimaryKey(tableName),
                    ChunkSize = GetDefaultChunkSize(tableName)
                };
                configs[tableName] = config;
            }
            
            config.DataFiles.Add(filePath);
        }
        
        // Sort data files for each config (important for multi-part)
        foreach (var config in configs.Values)
        {
            config.DataFiles.Sort();
            config.FileName = Path.GetFileName(config.DataFiles[0]);
        }
        
        return configs.Values.OrderBy(c => c.TableName).ToList();
    }
    
    private static string GetDefaultPrimaryKey(string tableName)
    {
        // Most annas_archive tables use aacid
        if (tableName.Contains("annas_archive_meta__aacid"))
            return "aacid";
        if (tableName.Contains("aarecords_"))
            return "md5"; // or depends on specific table
        
        // libgenli tables
        return tableName switch
        {
            "libgenli_publishers" => "p_id",
            "libgenli_series" => "s_id",
            "libgenli_series_add_descr" => "s_add_id",
            "libgenli_elem_descr" => "id",
            "libgenli_editions" => "e_id",
            "libgenli_editions_add_descr" => "ea_id",
            "libgenli_editions_to_files" => "ef_id",
            "libgenli_files" => "f_id",
            "libgenli_files_add_descr" => "fa_id",
            // libgenrs tables
            "libgenrs_fiction" => "ID",
            "libgenrs_fiction_description" => "MD5",
            "libgenrs_fiction_hashes" => "md5",
            "libgenrs_updated" => "ID",
            "libgenrs_description" => "md5",
            "libgenrs_hashes" => "md5",
            "libgenrs_topics" => "id",
            // Other tables
            "isbndb_isbns" => "isbn13",
            "ol_base" => "ol_key",
            "zlib_book" => "zlibrary_id",
            "scihub_dois" => "doi",
            _ => "id"
        };
    }
    
    private static int GetDefaultChunkSize(string tableName)
    {
        // Larger chunks for simpler tables, smaller for tables with large text columns
        if (tableName.Contains("description") || tableName.Contains("records"))
            return 2000;
        if (tableName.Contains("worldcat"))
            return 1000; // Very large records
        return 5000;
    }
}

public class CsvFileStatus
{
    public string FileName { get; set; } = "";
    public string TableName { get; set; } = "";
    public bool Exists { get; set; }
    public double FileSizeMB { get; set; }
    public int FileCount { get; set; } = 1;
    public long LastLine { get; set; }
    public long RowsLoaded { get; set; }
    public long TargetRows { get; set; }
    public SyncStatus Status { get; set; }
}
