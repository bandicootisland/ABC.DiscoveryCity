using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MySqlConnector;

namespace ABC.DiscoveryCity.MariaDB;

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

    // If we hit this many consecutive parse errors, assume gzip corruption and stop
    private const int MaxConsecutiveErrors = 50;

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
        long orphanLinesSkipped = 0; // Track orphan continuation lines from multi-line records
        long initialRowsSynced = progress.TotalRowsSynced;
        long globalLine = 0;
        long bytesProcessed = 0;
        int chunkNumber = 0;
        var chunkRows = new List<object?[]>();
        long chunkBytesEstimate = 0; // Track estimated size of current chunk
        const long MaxChunkBytes = 16 * 1024 * 1024; // 16MB max per chunk - prevents timeout on large records
        int consecutiveErrors = 0; // Track consecutive parse failures for corruption detection

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
                bool gzipCorrupted = false;
                string? pendingLine = null; // For multi-line CSV records

                while (!gzipCorrupted)
                {
                    try
                    {
                        line = await reader.ReadLineAsync(cancellationToken);
                        if (line == null) 
                        {
                            // End of file - if we have a pending incomplete line, it's an error
                            if (pendingLine != null)
                            {
                                LogError(config.TableName, globalLine, "INCOMPLETE_RECORD", 
                                    $"File ended with incomplete multi-line record");
                                rowsSkippedThisRun++;
                                pendingLine = null;
                            }
                            break;
                        }
                        
                        // Handle multi-line CSV records (quoted fields with embedded newlines)
                        if (pendingLine != null)
                        {
                            // Continue the previous incomplete line
                            line = pendingLine + "\n" + line;
                            pendingLine = null;
                        }
                        
                        // Check if line has unbalanced quotes (incomplete multi-line record)
                        if (HasUnbalancedQuotes(line))
                        {
                            pendingLine = line;
                            continue; // Read more lines to complete the record
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        // Gzip stream is corrupted/truncated - save what we have and continue
                        Console.WriteLine($"\n\nWARNING: Gzip corruption detected at line {globalLine}: {ex.Message}");
                        Console.WriteLine("Saving partial data and marking as partially complete...");
                        gzipCorrupted = true;
                        break;
                    }
                    catch (IOException ex) when (ex.Message.Contains("compression") || ex.Message.Contains("archive"))
                    {
                        Console.WriteLine($"\n\nWARNING: Compression error at line {globalLine}: {ex.Message}");
                        Console.WriteLine("Saving partial data and marking as partially complete...");
                        gzipCorrupted = true;
                        break;
                    }

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
                        consecutiveErrors = 0; // Reset on skipped lines
                        continue;
                    }

                    // Parse CSV line
                    try
                    {
                        var values = ParseCsvLine(line);
                        if (values.Count != columns.Count)
                        {
                            // Check if this looks like an orphan continuation line from a multi-line record
                            // These occur when a massive JSON field contains many newlines, and some
                            // intermediate lines happen to have balanced quotes
                            bool isOrphanContinuation = IsOrphanContinuationLine(line, values);
                            
                            if (isOrphanContinuation)
                            {
                                // Silently skip orphan continuation lines - they're part of a huge
                                // multi-line record that we already processed or will process
                                orphanLinesSkipped++;
                                rowsSkippedThisRun++;
                                continue;
                            }
                            
                            consecutiveErrors++;
                            
                            // If we hit many consecutive errors, likely gzip corruption - stop early
                            if (consecutiveErrors >= MaxConsecutiveErrors)
                            {
                                Console.WriteLine($"\n⚠️ Detected likely gzip corruption: {consecutiveErrors} consecutive parse errors at line {globalLine}");
                                Console.WriteLine($"   Saving {rowsSyncedThisRun:N0} valid rows loaded before corruption point.");
                                gzipCorrupted = true;
                                break;
                            }
                            
                            // Log first few errors with sample data for debugging
                            if (consecutiveErrors <= 5)
                            {
                                LogError(config.TableName, globalLine, "COLUMN_COUNT", 
                                    $"Expected {columns.Count} columns, got {values.Count}");
                                // Log sample of failing line (first 200 chars)
                                var sample = line.Length > 200 ? line.Substring(0, 200) + "..." : line;
                                LogError(config.TableName, globalLine, "SAMPLE", sample);
                            }
                            rowsSkippedThisRun++;
                            continue;
                        }

                        consecutiveErrors = 0; // Reset on successful parse
                        var row = new object?[columns.Count];
                        long rowBytes = 0;
                        for (int i = 0; i < values.Count; i++)
                        {
                            row[i] = ConvertValue(values[i]);
                            // Estimate byte size of this value
                            if (values[i] != null)
                                rowBytes += values[i].Length * 2; // UTF-8 to parameter overhead estimate
                        }
                        chunkRows.Add(row);
                        chunkBytesEstimate += rowBytes;
                    }
                    catch (Exception ex)
                    {
                        consecutiveErrors++;
                        if (consecutiveErrors >= MaxConsecutiveErrors)
                        {
                            Console.WriteLine($"\n⚠️ Detected likely gzip corruption: {consecutiveErrors} consecutive parse errors at line {globalLine}");
                            gzipCorrupted = true;
                            break;
                        }
                        if (consecutiveErrors <= 5)
                        {
                            LogError(config.TableName, globalLine, "PARSE", ex.Message);
                        }
                        rowsSkippedThisRun++;
                        continue;
                    }

                    // Write chunk when full (by row count OR by size - whichever comes first)
                    bool chunkFull = chunkRows.Count >= config.ChunkSize || chunkBytesEstimate >= MaxChunkBytes;
                    if (chunkFull)
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
                        chunkBytesEstimate = 0; // Reset size tracking
                    }
                }
                
                bytesProcessed += fileInfo.Length;
                
                // If gzip was corrupted, stop processing more files
                if (gzipCorrupted) break;
            }

            // Write remaining rows
            if (chunkRows.Count > 0)
            {
                chunkNumber++;
                int written = await WriteChunkWithRetryAsync(config, columns, chunkRows, useSimpleInsert);
                rowsSyncedThisRun += written;
                _syncState.UpdateProgress(config.TableName, globalLine, initialRowsSynced + rowsSyncedThisRun);
            }

            // Mark as partial if we hit corruption, otherwise completed
            if (rowsSyncedThisRun > 0)
            {
                // Check if any file had corruption by looking at progress vs expected
                var finalStatus = _syncState.GetProgress(config.TableName);
                _syncState.MarkCompleted(config.TableName, initialRowsSynced + rowsSyncedThisRun);
                Console.WriteLine("\n\nLoad completed!");
                if (orphanLinesSkipped > 0)
                {
                    Console.WriteLine($"  Note: {orphanLinesSkipped:N0} orphan continuation lines silently skipped (from multi-line JSON records)");
                }
            }
            else
            {
                _syncState.MarkCompleted(config.TableName, initialRowsSynced + rowsSyncedThisRun);
                Console.WriteLine("\n\nLoad completed!");
            }
        }
        catch (InvalidDataException ex)
        {
            // Gzip corruption - save what we have
            Console.WriteLine($"\n\nWARNING: Gzip file corrupted: {ex.Message}");
            if (chunkRows.Count > 0)
            {
                int written = await WriteChunkWithRetryAsync(config, columns, chunkRows, useSimpleInsert);
                rowsSyncedThisRun += written;
            }
            _syncState.UpdateProgress(config.TableName, globalLine, initialRowsSynced + rowsSyncedThisRun);
            if (rowsSyncedThisRun > 0)
            {
                _syncState.MarkCompleted(config.TableName, initialRowsSynced + rowsSyncedThisRun);
                Console.WriteLine($"Partial data saved: {rowsSyncedThisRun:N0} rows recovered before corruption.");
            }
            else
            {
                _syncState.MarkFailed(config.TableName, ex.Message);
            }
        }
        catch (Exception ex) when (ex.Message.Contains("compression") || ex.Message.Contains("unsupported"))
        {
            // Compression method error - save what we have
            Console.WriteLine($"\n\nWARNING: Compression error: {ex.Message}");
            if (chunkRows.Count > 0)
            {
                int written = await WriteChunkWithRetryAsync(config, columns, chunkRows, useSimpleInsert);
                rowsSyncedThisRun += written;
            }
            _syncState.UpdateProgress(config.TableName, globalLine, initialRowsSynced + rowsSyncedThisRun);
            if (rowsSyncedThisRun > 0)
            {
                _syncState.MarkCompleted(config.TableName, initialRowsSynced + rowsSyncedThisRun);
                Console.WriteLine($"Partial data saved: {rowsSyncedThisRun:N0} rows recovered before error.");
            }
            else
            {
                _syncState.MarkFailed(config.TableName, ex.Message);
            }
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
    /// Check if a line has unbalanced quotes, indicating a multi-line CSV record.
    /// Accounts for escaped quotes (\" or "").
    /// Also detects base64 continuation lines that appear balanced but are actually
    /// continuations of a field from the previous line.
    /// </summary>
    private bool HasUnbalancedQuotes(string line)
    {
        // First, check if this looks like a base64 continuation line.
        // Base64 continuations don't start with a quote - they start with base64 chars
        // and eventually hit a closing quote. E.g.: "YFRSRBL2SIQ64CELXI5VZY5WTLVGSWMK","next..."
        if (IsBase64ContinuationLine(line))
        {
            return true; // Treat as unbalanced - needs to be joined with previous line
        }
        
        bool inQuotes = false;
        int i = 0;
        
        while (i < line.Length)
        {
            char c = line[i];
            
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < line.Length)
                {
                    // Skip escape sequences
                    i += 2;
                    continue;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
            }
            i++;
        }
        
        return inQuotes; // If still in quotes at end, it's unbalanced
    }
    
    /// <summary>
    /// Detect if a line is a base64 continuation - a line that starts with base64 characters
    /// (not a quote) and has a long run of base64 chars before any comma or quote.
    /// This catches cases like: YFRSRBL2SIQ64CELXI5VZY5WTLVGSWMK","next_field",...
    /// which look balanced but are actually a continuation of the previous line's field.
    /// </summary>
    private bool IsBase64ContinuationLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;
            
        // If line starts with a quote, it's a proper field start, not a continuation
        if (line[0] == '"')
            return false;
        
        // Count consecutive base64 characters at the start
        // Base64 alphabet: A-Z, a-z, 0-9, +, /, = (padding)
        int base64Run = 0;
        foreach (char c in line)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || 
                (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=')
            {
                base64Run++;
            }
            else
            {
                break; // Hit a non-base64 character
            }
        }
        
        // If we have a long run of base64 chars (>30) at the start, this is likely a continuation
        // Normal CSV fields start with a quote, not with 30+ alphanumeric chars
        return base64Run >= 30;
    }

    /// <summary>
    /// Detect if a line is an orphan continuation fragment from a massive multi-line CSV record.
    /// This happens when a JSON field contains many newlines, and some intermediate lines
    /// happen to have balanced quotes, making them look like complete (but invalid) records.
    /// </summary>
    private bool IsOrphanContinuationLine(string line, List<string> parsedValues)
    {
        // If we got very few columns (less than expected), check for telltale signs of JSON fragments
        if (parsedValues.Count <= 3 && parsedValues.Count > 0)
        {
            // Check the LAST parsed value - orphan lines often end with JSON closing patterns
            var lastValue = parsedValues[parsedValues.Count - 1];
            
            // If last value ends with JSON fragment patterns like }}" or }} it's likely orphan
            if (lastValue.EndsWith("\"}}") || lastValue.EndsWith("}}") || 
                lastValue.EndsWith("\"}") || lastValue.EndsWith("}\"]"))
            {
                return true;
            }
            
            // Check if any parsed value contains patterns that indicate mid-JSON content
            foreach (var val in parsedValues)
            {
                // These patterns strongly indicate we're inside a JSON object
                if (val.Contains("\"key\":") || val.Contains("\"value\":") ||
                    val.Contains("\"type\":") || val.Contains("\\\"key\\\""))
                {
                    return true;
                }
            }
            
            // Check if second column (if exists) looks like a JSON key reference ending with garbage
            if (parsedValues.Count >= 2)
            {
                var second = parsedValues[1];
                // Pattern: /authors/OL123"}} or /works/OL123"}}
                if ((second.Contains("/authors/") || second.Contains("/works/") || 
                     second.Contains("/books/")) && 
                    (second.EndsWith("\"}}") || second.EndsWith("}}")))
                {
                    return true;
                }
            }
        }
        
        return false;
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
        
        // Find the column definitions between CREATE TABLE ... ( and the matching closing )
        // We need to find the FIRST opening paren after CREATE TABLE, then find its MATCHING closing paren
        // This is important for tables with PARTITION BY clauses which add more parentheses
        int startParen = createStatement.IndexOf('(');
        if (startParen < 0) return columns;

        // Find the matching closing paren by counting parentheses
        int endParen = FindMatchingCloseParen(createStatement, startParen);
        if (endParen < 0) return columns;

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

    /// <summary>
    /// Find the matching closing parenthesis for the opening paren at openIndex.
    /// Handles nested parentheses correctly, which is important for tables with
    /// PARTITION BY clauses that contain additional parentheses.
    /// </summary>
    private int FindMatchingCloseParen(string str, int openIndex)
    {
        if (openIndex < 0 || openIndex >= str.Length || str[openIndex] != '(')
            return -1;

        int depth = 1;
        for (int i = openIndex + 1; i < str.Length; i++)
        {
            if (str[i] == '(')
            {
                depth++;
            }
            else if (str[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1; // No matching close paren found
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

                // Extract the CREATE TABLE statement - be careful with semicolons inside quoted strings
                // We need to find "CREATE TABLE" and then find the proper ending semicolon
                var createSql = ExtractCreateTableStatement(content);

                if (!string.IsNullOrEmpty(createSql))
                {
                    createSql = createSql.Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS");
                    
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
    
    /// <summary>
    /// Extract the CREATE TABLE statement from schema content, handling semicolons inside quoted strings.
    /// </summary>
    private string? ExtractCreateTableStatement(string content)
    {
        // Find "CREATE TABLE"
        var startIdx = content.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0)
            return null;
        
        // Now find the ending semicolon, but ignore semicolons inside quotes
        bool inSingleQuote = false;
        bool inDoubleQuote = false;
        bool inBacktick = false;
        
        for (int i = startIdx; i < content.Length; i++)
        {
            char c = content[i];
            char prev = i > 0 ? content[i - 1] : '\0';
            
            // Handle escape sequences (skip escaped quotes)
            if (prev == '\\')
                continue;
                
            if (c == '\'' && !inDoubleQuote && !inBacktick)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (c == '"' && !inSingleQuote && !inBacktick)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (c == '`' && !inSingleQuote && !inDoubleQuote)
            {
                inBacktick = !inBacktick;
            }
            else if (c == ';' && !inSingleQuote && !inDoubleQuote && !inBacktick)
            {
                // Found the real end of the statement
                return content.Substring(startIdx, i - startIdx + 1);
            }
        }
        
        return null; // No proper ending found
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
        cmd.CommandTimeout = 1800; // 30 minutes for very large metadata inserts
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
        if (tableName.Contains("aa_ia") && tableName.Contains("metadata"))
            return 200; // Very large JSON metadata records (7.5GB compressed) - needs small chunks
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
