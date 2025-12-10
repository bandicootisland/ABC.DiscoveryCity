using System.Buffers;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// Span-based CSV loader using LastIndexOf and slice-based approach.
/// Keeps buffer well-filled, processes all complete rows at once.
/// </summary>
public class FastCsvLoader
{
    private readonly string _targetConnectionString;
    private readonly SyncState _syncState;
    private readonly string _basePath;
    private readonly int _maxRetries;
    private readonly int _retryDelayMs;

    private const int MaxChunkRows = 2000;
    private const long MaxChunkBytes = 4 * 1024 * 1024;
    private const int BufferSize = 4 * 1024 * 1024;
    private const int CommandTimeout = 1800;

    public FastCsvLoader(
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

    public async Task LoadTableAsync(CsvFileConfig config, CancellationToken ct = default)
    {
        var dataFiles = config.DataFiles.Count > 0
            ? config.DataFiles
            : new List<string> { Path.Combine(_basePath, config.FileName) };

        foreach (var f in dataFiles)
        {
            if (!File.Exists(f))
            {
                Console.WriteLine($"ERROR: File not found: {f}");
                return;
            }
        }

        var progress = _syncState.GetProgress(config.TableName);
        long startRow = progress.LastSyncedId;
        long totalSize = dataFiles.Sum(f => new FileInfo(f).Length);

        Console.WriteLine($"\n{'=',-60}");
        Console.WriteLine($"Loading: {config.TableName} (FastCsvLoader/Slice)");
        Console.WriteLine($"Files:   {dataFiles.Count} part(s)");
        Console.WriteLine($"Resume:  Starting from row {startRow:N0}");
        Console.WriteLine($"{'=',-60}\n");
        Console.WriteLine($"Total size: {totalSize / 1024.0 / 1024.0:F2} MB (compressed)");

        var columns = await GetColumnsFromSchemaAsync(config);
        if (columns.Length == 0)
        {
            Console.WriteLine("ERROR: Could not determine columns from schema.");
            return;
        }
        int columnCount = columns.Length;
        Console.WriteLine($"Columns: {columnCount} ({string.Join(", ", columns.Take(5))}...)");

        await EnsureTargetTableExistsAsync(config);

        long targetRows = await GetTargetCountAsync(config);
        bool useInsertIgnore = config.UseSimpleInsert || targetRows == 0;
        Console.WriteLine($"Target has {targetRows:N0} rows. Using {(useInsertIgnore ? "INSERT IGNORE" : "UPSERT")} mode.");

        var sw = Stopwatch.StartNew();
        long totalRowsLoaded = 0;
        long globalRow = 0;
        long bytesRead = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        int[] colStart = new int[columnCount];
        int[] colEnd = new int[columnCount];
        var values = new string?[columnCount];
        
        try
        {
            foreach (var dataFile in dataFiles)
            {
                if (ct.IsCancellationRequested) break;

                Console.WriteLine($"\nProcessing: {Path.GetFileName(dataFile)}");

                using var fs = new FileStream(dataFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);

                int bufferStart = 0;
                int bufferEnd = 0;
                bool eof = false;

                // Helper to ensure buffer is well-filled
                void EnsureBufferFilled()
                {
                    int available = bufferEnd - bufferStart;
                    
                    // Refill when less than 1MB available
                    if (available < 1024 * 1024 && !eof)
                    {
                        // Compact: move remaining data to front
                        if (bufferStart > 0 && available > 0)
                        {
                            Buffer.BlockCopy(buffer, bufferStart, buffer, 0, available);
                        }
                        bufferEnd = available;
                        bufferStart = 0;

                        // Fill the rest
                        int space = buffer.Length - bufferEnd;
                        if (space > 0)
                        {
                     
                            try
                            {
                                int read = gz.Read(buffer, bufferEnd, space);
                                bytesRead += read;
                                bufferEnd += read;
                                if (read == 0) eof = true;
                            }
                            catch (InvalidDataException) { eof = true; }
                        }
                    }
                }

                // Initial fill
                EnsureBufferFilled();

                // Skip header
                if (config.HasHeader && bufferEnd > bufferStart)
                {
                    var span = buffer.AsSpan(bufferStart, bufferEnd - bufferStart);
                    int nl = span.IndexOf((byte)'\n');
                    if (nl >= 0)
                    {
                        int comma = span.Slice(0, nl).IndexOf((byte)',');
                        var firstField = comma > 0
                            ? Encoding.UTF8.GetString(span.Slice(0, comma))
                            : Encoding.UTF8.GetString(span.Slice(0, nl));
                        if (firstField.StartsWith("\"")) firstField = firstField[1..^1];
                        if (firstField == columns[0])
                        {
                            bufferStart += nl + 1;
                            Console.WriteLine("  Skipped header row.");
                        }
                    }
                }

                // Main processing loop
                bool skipping = startRow > globalRow;
                if (skipping)
                    Console.WriteLine($"  Skipping {startRow - globalRow:N0} rows...");

                var chunk = new List<object?[]>(MaxChunkRows);
                long chunkBytes = 0;
                int chunkNum = 0;
                long lastReport = 0;

                while (!ct.IsCancellationRequested)
                {
                    EnsureBufferFilled();
                    
                    int spanLen = bufferEnd - bufferStart;
                    if (spanLen == 0) break;
                    var rowinfo = new Row(globalRow);
                    // Use LastIndexOf to find the last complete row boundary
                    var span = buffer.AsSpan(bufferStart, spanLen);

                    rowinfo.RowEndIndex = span.LastIndexOf((byte)'\n');
                    
                    if (rowinfo.RowEndIndex < 0)
                    {
                        if (eof) break; // No more data
                        continue; // Need more data
                    }

                    // Process all complete rows in span[0..lastNl]
                    int consumed = 0;
                    int searchPos = 0;

                    while (searchPos <= rowinfo.RowEndIndex)
                    {
                        // Find next newline from searchPos
                        //, lastNl - searchPos + 1
                        int nlOffset = span.Slice(searchPos).IndexOf((byte)'\n');
                        if (nlOffset < 0) break;

                        int nlPos = searchPos + nlOffset;

                        // Check if this is a legitimate row end (even quote count)
                        var alignlinebreak = (nlPos, searchPos) switch
                        {
                            (> 0, >= 0) => nlPos - searchPos,
                            _ => -1
                        };
                        
                        var rowCandidate = span.Slice(searchPos, nlOffset);
                        int quoteCount = 0;
                        int qPos = 0;
                        while (qPos < rowCandidate.Length)
                        {                            
                            int q = rowCandidate.Slice(qPos).IndexOf((byte)'"');
                            if (q < 0) break;
                            quoteCount++;
                            if (quoteCount %2==1)
                                rowinfo.Columns.Add(new ColumnInfo() { ColumnNumber= rowinfo.Columns.Count+1});
                            var ci = rowinfo.Columns[rowinfo.Columns.Count - 1];
                            if (quoteCount % 2 == 1)
                                ci.StartIndex = qPos + q + 1;
                            else
                                ci.EndIndex = qPos + q;
                            
                            qPos += q + 1;
                        }

                        if (quoteCount % 2 == 1)
                        {
                            // Inside quotes - this newline is embedded, skip it
                            searchPos = nlPos + 1;
                            continue;
                        }

                        // Store row indices - don't hold span
                        int rowOffset = searchPos;
                        int rowLen = nlOffset;
                        consumed = nlPos + 1;
                        searchPos = nlPos + 1;

                        // Progress report during skip
                        if (skipping && globalRow - lastReport >= 100000)
                        {
                            Console.Write($"\r  Skipped {globalRow:N0} / {startRow:N0}...");
                            lastReport = globalRow;
                        }

                        // If skipping, just count
                        if (skipping)
                        {
                            globalRow++;
                            if (globalRow >= startRow)
                            {
                                skipping = false;
                                Console.WriteLine($"\r  Skipped {globalRow:N0} rows.                    ");
                            }
                            continue;
                        }

                        // Parse columns - get fresh span for this row
                        var row = buffer.AsSpan(bufferStart + rowOffset, rowLen);
                        bool inQuotes = false;
                        int col = 0;
                        colStart[0] = 0;

                        for (int i = 0; i < row.Length && col < columnCount; i++)
                        {
                            byte b = row[i];
                            if (b == '"')
                            {
                                if (inQuotes && i + 1 < row.Length && row[i + 1] == '"')
                                    i++;
                                else
                                    inQuotes = !inQuotes;
                            }
                            else if (b == ',' && !inQuotes)
                            {
                                colEnd[col] = i;
                                col++;
                                if (col < columnCount)
                                    colStart[col] = i + 1;
                            }
                        }

                        if (col != columnCount - 1)
                        {
                            globalRow++;
                            continue; // Wrong column count
                        }
                        colEnd[col] = row.Length;

                        // Extract values - row is fresh span
                        for (int c = 0; c < columnCount; c++)
                        {
                            int start = colStart[c];
                            int end = colEnd[c];
                            if (end <= start)
                            {
                                values[c] = "";
                                continue;
                            }

                            var field = row.Slice(start, end - start);

                            if (field.Length == 2 && field[0] == '\\' && field[1] == 'N')
                            {
                                values[c] = "\\N";
                                continue;
                            }

                            if (field.Length >= 2 && field[0] == '"' && field[^1] == '"')
                            {
                                var inner = field.Slice(1, field.Length - 2);
                                if (inner.IndexOf((byte)'"') >= 0)
                                    values[c] = Encoding.UTF8.GetString(inner).Replace("\"\"", "\"");
                                else
                                    values[c] = Encoding.UTF8.GetString(inner);
                                continue;
                            }

                            values[c] = Encoding.UTF8.GetString(field);
                        }

                        // Build DB row
                        var dbRow = new object?[columnCount];
                        long rowBytes = 0;
                        for (int i = 0; i < columnCount; i++)
                        {
                            var v = values[i];
                            dbRow[i] = (v == null || v == "\\N") ? DBNull.Value : v;
                            rowBytes += v?.Length ?? 0;
                        }
                        chunk.Add(dbRow);
                        chunkBytes += rowBytes * 2;
                        globalRow++;

                        // Write chunk if full
                        if (chunk.Count >= MaxChunkRows || chunkBytes >= MaxChunkBytes)
                        {
                            chunkNum++;
                            int written = WriteChunk(config, columns, chunk, useInsertIgnore);
                            totalRowsLoaded += written;
                            _syncState.UpdateProgress(config.TableName, globalRow, totalRowsLoaded);

                            double pct = totalSize > 0 ? bytesRead * 100.0 / totalSize : 0;
                            double rate = totalRowsLoaded / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
                            Console.Write($"\rChunk {chunkNum}: {globalRow:N0} rows | {pct:F1}% | {rate:N0}/s    ");

                            chunk.Clear();
                            chunkBytes = 0;
                        }
                    }

                    // Consume all processed rows
                    if (consumed > 0)
                        bufferStart += consumed;
                }

                // Final chunk
                if (chunk.Count > 0)
                {
                    chunkNum++;
                    int written = WriteChunk(config, columns, chunk, useInsertIgnore);
                    totalRowsLoaded += written;
                    _syncState.UpdateProgress(config.TableName, globalRow, totalRowsLoaded);
                }

                Console.WriteLine($"\n  File complete. Rows: {globalRow:N0}");
                startRow = 0;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            Console.WriteLine("Progress has been saved. You can resume later.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _syncState.MarkCompleted(config.TableName, totalRowsLoaded);
        Console.WriteLine($"\n\nCompleted in {sw.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Total rows loaded: {totalRowsLoaded:N0}");
    }
    private int WriteChunk(CsvFileConfig config, string[] columns, List<object?[]> rows, bool useInsertIgnore)
    {
        if (rows.Count == 0) return 0;

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var conn = new MySqlConnection(_targetConnectionString);
                conn.Open();

                var colList = string.Join(",", columns.Select(c => $"`{c}`"));
                var insertType = useInsertIgnore ? "INSERT IGNORE" : "REPLACE";
                var sb = new StringBuilder();
                sb.Append($"{insertType} INTO `{config.TableName}` ({colList}) VALUES ");

                var parameters = new List<MySqlParameter>();
                for (int r = 0; r < rows.Count; r++)
                {
                    if (r > 0) sb.Append(',');
                    sb.Append('(');
                    for (int c = 0; c < columns.Length; c++)
                    {
                        if (c > 0) sb.Append(',');
                        var pname = $"@p{r}_{c}";
                        sb.Append(pname);
                        parameters.Add(new MySqlParameter(pname, rows[r][c] ?? DBNull.Value));
                    }
                    sb.Append(')');
                }

                using var cmd = new MySqlCommand(sb.ToString(), conn);
                cmd.CommandTimeout = CommandTimeout;
                cmd.Parameters.AddRange(parameters.ToArray());
                return cmd.ExecuteNonQuery();
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                Console.WriteLine($"\nRetry {attempt + 1}/{_maxRetries}: {ex.Message}");
                Task.Delay(_retryDelayMs);
            }
        }
        return 0;
    }
    private async Task<int> WriteChunkAsync(CsvFileConfig config, string[] columns, List<object?[]> rows, bool useInsertIgnore)
    {
        if (rows.Count == 0) return 0;

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                await using var conn = new MySqlConnection(_targetConnectionString);
                await conn.OpenAsync();

                var colList = string.Join(",", columns.Select(c => $"`{c}`"));
                var insertType = useInsertIgnore ? "INSERT IGNORE" : "REPLACE";
                var sb = new StringBuilder();
                sb.Append($"{insertType} INTO `{config.TableName}` ({colList}) VALUES ");

                var parameters = new List<MySqlParameter>();
                for (int r = 0; r < rows.Count; r++)
                {
                    if (r > 0) sb.Append(',');
                    sb.Append('(');
                    for (int c = 0; c < columns.Length; c++)
                    {
                        if (c > 0) sb.Append(',');
                        var pname = $"@p{r}_{c}";
                        sb.Append(pname);
                        parameters.Add(new MySqlParameter(pname, rows[r][c] ?? DBNull.Value));
                    }
                    sb.Append(')');
                }

                await using var cmd = new MySqlCommand(sb.ToString(), conn);
                cmd.CommandTimeout = CommandTimeout;
                cmd.Parameters.AddRange(parameters.ToArray());
                return await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                Console.WriteLine($"\nRetry {attempt + 1}/{_maxRetries}: {ex.Message}");
                await Task.Delay(_retryDelayMs);
            }
        }
        return 0;
    }

    private async Task<string[]> GetColumnsFromSchemaAsync(CsvFileConfig config)
    {
        var schemaFile = Path.Combine(_basePath, $"allthethings.{config.TableName}-schema.sql.gz");
        if (!File.Exists(schemaFile)) return Array.Empty<string>();

        try
        {
            await using var fs = new FileStream(schemaFile, FileMode.Open, FileAccess.Read);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            var content = await sr.ReadToEndAsync();

            var columns = new List<string>();
            var match = Regex.Match(content, @"CREATE TABLE[^(]+\((.+)\)",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (match.Success)
            {
                foreach (var line in match.Groups[1].Value.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("`"))
                    {
                        int end = trimmed.IndexOf('`', 1);
                        if (end > 1) columns.Add(trimmed.Substring(1, end - 1));
                    }
                }
            }
            return columns.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private async Task EnsureTargetTableExistsAsync(CsvFileConfig config)
    {
        var schemaFile = Path.Combine(_basePath, $"allthethings.{config.TableName}-schema.sql.gz");
        if (!File.Exists(schemaFile)) return;

        try
        {
            await using var fs = new FileStream(schemaFile, FileMode.Open, FileAccess.Read);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            var content = await sr.ReadToEndAsync();

            var sql = ExtractCreateTable(content);
            if (string.IsNullOrEmpty(sql)) return;

            sql = sql.Replace("CREATE TABLE", "CREATE TABLE IF NOT EXISTS");

            await using var conn = new MySqlConnection(_targetConnectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine("Target table verified/created.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Schema error: {ex.Message}");
        }
    }

    private static string? ExtractCreateTable(string content)
    {
        int start = content.IndexOf("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;

        bool inQ1 = false, inQ2 = false, inBt = false;
        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            char prev = i > 0 ? content[i - 1] : '\0';
            if (prev == '\\') continue;
            if (c == '\'' && !inQ2 && !inBt) inQ1 = !inQ1;
            else if (c == '"' && !inQ1 && !inBt) inQ2 = !inQ2;
            else if (c == '`' && !inQ1 && !inQ2) inBt = !inBt;
            else if (c == ';' && !inQ1 && !inQ2 && !inBt)
                return content.Substring(start, i - start + 1);
        }
        return null;
    }

    private async Task<long> GetTargetCountAsync(CsvFileConfig config)
    {
        try
        {
            await using var conn = new MySqlConnection(_targetConnectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM `{config.TableName}`", conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        catch { return 0; }
    }
}
public struct Row
{
    public Row(long rownumber)
    {
        RowNumber = rownumber;
        RowStartIndex = -1;
        RowEndIndex = -1;        
    }

    public long RowNumber { get; set; }
    public int RowStartIndex { get; set; }
    public List<ColumnInfo> Columns { get; set; } = new();
    public int RowEndIndex { get; set; }
    
}
public struct ColumnInfo
{
    public ColumnInfo(int start, int end)
    {
        StartIndex = start;
        EndIndex = end;
    }
    public int ColumnNumber { get; set; }
    public int StartIndex { get; set; }
    public int EndIndex { get; set; }
}

