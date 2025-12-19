using System.Buffers;
using System.Diagnostics;
using System.Text;
using MySqlConnector;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// High-performance Span-based loader for OpenLibrary dump files.
/// Loads the ol_dump_latest.txt format (5 tab-separated columns, no header).
/// Format: type, ol_key, revision, last_modified, json
/// Uses buffer pooling and Span-based line extraction for optimal performance.
/// </summary>
public class OpenLibraryLoaderSpan
{
    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly int _batchSize;

    // Column indices in the TSV file
    private const int COL_TYPE = 0;
    private const int COL_OL_KEY = 1;
    private const int COL_REVISION = 2;
    private const int COL_LAST_MODIFIED = 3;
    private const int COL_JSON = 4;

    private const int EXPECTED_COLUMNS = 5;
    private const int BUFFER_SIZE = 32 * 1024 * 1024; // 32MB buffer (JSON can be large)

    public OpenLibraryLoaderSpan(string connectionString, string filePath, int batchSize = 3000)
    {
        _connectionString = connectionString;
        _filePath = filePath;
        _batchSize = batchSize;
    }

    public async Task<(long processed, long inserted, long skipped)> LoadAsync(
        long skipRows = 0,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        long rowsProcessed = 0;
        long rowsInserted = 0;
        long rowsSkipped = 0;
        long malformedRows = 0;

        // Buffer for building INSERT statements - JSON can be very large
        var insertBuilder = new StringBuilder(4 * 1024 * 1024); // 4MB initial
        var batchCount = 0;

        const string insertPrefix = @"INSERT IGNORE INTO allthethings.ol_base 
            (type, ol_key, revision, last_modified, json) VALUES ";

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Optimize for bulk loading (max_allowed_packet is read-only at SESSION level)
        await using (var cmd = new MySqlCommand(@"
            SET SESSION unique_checks = 0;
            SET SESSION foreign_key_checks = 0;
            SET SESSION sql_log_bin = 0;", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var fileInfo = new FileInfo(_filePath);
        Console.WriteLine($"[OpenLibraryLoaderSpan] Loading from: {_filePath}");
        Console.WriteLine($"[OpenLibraryLoaderSpan] File size: {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB");
        Console.WriteLine($"[OpenLibraryLoaderSpan] Skip rows: {skipRows:N0}, Batch size: {_batchSize:N0}");
        Console.WriteLine($"[OpenLibraryLoaderSpan] Using Span-based parsing with {BUFFER_SIZE / 1024 / 1024}MB buffer");

        // Rent buffer from pool
        byte[] bufferArray = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        Memory<byte> buffer = bufferArray.AsMemory();

        // Reusable array for field ranges
        Range[] fieldRanges = new Range[EXPECTED_COLUMNS + 1]; // +1 for safety with malformed lines

        try
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

            int bufferStart = 0;
            int bufferEnd = 0;
            bool eof = false;
            long currentRow = 0;

            var lastProgressTime = sw.Elapsed;
            long lastProgressRows = 0;
            long lastBytesRead = 0;

            // Helper to ensure buffer is well-filled
            void EnsureBufferFilled()
            {
                int available = bufferEnd - bufferStart;

                // Refill when less than 4MB available (JSON can be large)
                if (available < 4 * 1024 * 1024 && !eof)
                {
                    // Compact: move remaining data to front
                    if (bufferStart > 0 && available > 0)
                    {
                        buffer.Slice(bufferStart, available).CopyTo(buffer.Slice(0, available));
                    }
                    bufferStart = 0;
                    bufferEnd = available;

                    // Fill rest of buffer
                    int toRead = buffer.Length - bufferEnd;
                    int bytesRead = fs.Read(buffer.Span.Slice(bufferEnd, toRead));
                    bufferEnd += bytesRead;

                    if (bytesRead == 0)
                    {
                        eof = true;
                    }
                }
            }

            insertBuilder.Append(insertPrefix);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EnsureBufferFilled();

                if (bufferStart >= bufferEnd)
                {
                    break; // No more data
                }

                // Find newline in buffer using span
                var span = buffer.Slice(bufferStart, bufferEnd - bufferStart).Span;
                int newlineIndex = span.IndexOf((byte)'\n');

                if (newlineIndex < 0)
                {
                    if (eof)
                    {
                        // Process remaining data as last line
                        newlineIndex = span.Length;
                    }
                    else
                    {
                        // Need more data - buffer might be too small for this line
                        if (bufferStart == 0 && bufferEnd == buffer.Length)
                        {
                            // Line is larger than buffer - skip it
                            Console.WriteLine($"[OpenLibraryLoaderSpan] WARNING: Line {currentRow + 1} exceeds buffer size, skipping");
                            // Find next newline by reading more
                            int skipBytes = 0;
                            while (!eof)
                            {
                                int bytesRead = fs.Read(buffer.Span);
                                if (bytesRead == 0) { eof = true; break; }
                                int nlIdx = buffer.Span.Slice(0, bytesRead).IndexOf((byte)'\n');
                                if (nlIdx >= 0)
                                {
                                    // Found newline, reset buffer
                                    bufferStart = 0;
                                    bufferEnd = bytesRead - nlIdx - 1;
                                    buffer.Slice(nlIdx + 1, bufferEnd).CopyTo(buffer);
                                    currentRow++;
                                    rowsSkipped++;
                                    break;
                                }
                                skipBytes += bytesRead;
                            }
                        }
                        continue;
                    }
                }

                // Extract line (without newline) as ReadOnlySpan
                ReadOnlySpan<byte> lineSpan = span.Slice(0, newlineIndex);
                if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                {
                    lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);
                }

                bufferStart += newlineIndex + 1;
                currentRow++;

                // Skip rows if resuming
                if (currentRow <= skipRows)
                {
                    if (currentRow % 1_000_000 == 0)
                    {
                        var skipPercent = (double)fs.Position / fileInfo.Length * 100;
                        Console.WriteLine($"[OpenLibraryLoaderSpan] Skipping... {currentRow:N0} / {skipRows:N0} (file: {skipPercent:F1}%)");
                    }
                    continue;
                }

                // Skip empty lines
                if (lineSpan.Length == 0)
                {
                    rowsSkipped++;
                    continue;
                }

                rowsProcessed++;

                // Parse TSV line using Span
                int columnCount = ParseTsvLineSpan(lineSpan, fieldRanges);

                if (columnCount < EXPECTED_COLUMNS)
                {
                    malformedRows++;
                    if (malformedRows <= 10)
                    {
                        Console.WriteLine($"[OpenLibraryLoaderSpan] Malformed row {currentRow}: expected {EXPECTED_COLUMNS} columns, got {columnCount}");
                    }
                    rowsSkipped++;
                    continue;
                }

                // Validate required fields - ol_key must not be empty
                var olKeySpan = lineSpan[fieldRanges[COL_OL_KEY]];
                if (olKeySpan.Length == 0)
                {
                    rowsSkipped++;
                    continue;
                }

                // Build VALUES clause
                if (batchCount > 0)
                {
                    insertBuilder.Append(',');
                }

                insertBuilder.Append('(');
                AppendEscapedSpan(insertBuilder, lineSpan[fieldRanges[COL_TYPE]], 40); insertBuilder.Append(',');
                AppendEscapedSpan(insertBuilder, lineSpan[fieldRanges[COL_OL_KEY]], 250); insertBuilder.Append(',');
                AppendIntSpan(insertBuilder, lineSpan[fieldRanges[COL_REVISION]]); insertBuilder.Append(',');
                AppendDateTimeSpan(insertBuilder, lineSpan[fieldRanges[COL_LAST_MODIFIED]]); insertBuilder.Append(',');
                AppendEscapedSpan(insertBuilder, lineSpan[fieldRanges[COL_JSON]]); // JSON - no limit
                insertBuilder.Append(')');

                batchCount++;

                // Execute batch when full
                if (batchCount >= _batchSize)
                {
                    var inserted = await ExecuteBatchAsync(connection, insertBuilder.ToString(), cancellationToken);
                    rowsInserted += inserted;

                    // Reset for next batch
                    insertBuilder.Clear();
                    insertBuilder.Append(insertPrefix);
                    batchCount = 0;

                    // Progress report
                    var now = sw.Elapsed;
                    if (rowsProcessed % 50_000 == 0 || (now - lastProgressTime).TotalSeconds >= 30)
                    {
                        var elapsed = sw.Elapsed.TotalSeconds;
                        var recentRate = (rowsProcessed - lastProgressRows) / Math.Max(1, (now - lastProgressTime).TotalSeconds);
                        var filePercent = (double)fs.Position / fileInfo.Length * 100;
                        var bytesPerSec = (fs.Position - lastBytesRead) / Math.Max(1, (now - lastProgressTime).TotalSeconds);

                        // Estimate time remaining
                        var remainingBytes = fileInfo.Length - fs.Position;
                        var etaSeconds = bytesPerSec > 0 ? remainingBytes / bytesPerSec : 0;
                        var eta = TimeSpan.FromSeconds(etaSeconds);

                        Console.WriteLine($"[OpenLibraryLoaderSpan] Row: {currentRow:N0} | Processed: {rowsProcessed:N0} | Inserted: {rowsInserted:N0} | " +
                                          $"Rate: {recentRate:N0}/s | File: {filePercent:F1}% | ETA: {eta:hh\\:mm\\:ss} | Elapsed: {sw.Elapsed:hh\\:mm\\:ss}");

                        lastProgressTime = now;
                        lastProgressRows = rowsProcessed;
                        lastBytesRead = fs.Position;
                    }
                }
            }

            // Execute final batch
            if (batchCount > 0)
            {
                var inserted = await ExecuteBatchAsync(connection, insertBuilder.ToString(), cancellationToken);
                rowsInserted += inserted;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferArray);
        }

        // Re-enable checks
        await using (var cmd = new MySqlCommand(@"
            SET SESSION unique_checks = 1;
            SET SESSION foreign_key_checks = 1;", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        Console.WriteLine($"\n[OpenLibraryLoaderSpan] ========== COMPLETE ==========");
        Console.WriteLine($"[OpenLibraryLoaderSpan] Processed: {rowsProcessed:N0}");
        Console.WriteLine($"[OpenLibraryLoaderSpan] Inserted: {rowsInserted:N0}");
        Console.WriteLine($"[OpenLibraryLoaderSpan] Skipped: {rowsSkipped:N0}");
        Console.WriteLine($"[OpenLibraryLoaderSpan] Malformed: {malformedRows:N0}");
        Console.WriteLine($"[OpenLibraryLoaderSpan] Time: {sw.Elapsed}");
        if (sw.Elapsed.TotalSeconds > 0)
        {
            Console.WriteLine($"[OpenLibraryLoaderSpan] Avg Rate: {rowsProcessed / sw.Elapsed.TotalSeconds:N0} rows/sec");
        }

        return (rowsProcessed, rowsInserted, rowsSkipped);
    }

    /// <summary>
    /// Parse a TSV line - Span-based version (uses tab delimiter)
    /// </summary>
    private static int ParseTsvLineSpan(ReadOnlySpan<byte> lineSpan, Span<Range> fieldRanges)
    {
        int fieldCount = 0;
        int fieldStart = 0;

        for (int i = 0; i < lineSpan.Length; i++)
        {
            if (lineSpan[i] == (byte)'\t')
            {
                if (fieldCount < fieldRanges.Length)
                {
                    fieldRanges[fieldCount] = fieldStart..i;
                    fieldCount++;
                }
                fieldStart = i + 1;
            }
        }

        // Add last field
        if (fieldCount < fieldRanges.Length)
        {
            fieldRanges[fieldCount] = fieldStart..lineSpan.Length;
            fieldCount++;
        }

        return fieldCount;
    }

    private async Task<int> ExecuteBatchAsync(MySqlConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.CommandTimeout = 600; // 10 minutes for large batches with big JSON
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OpenLibraryLoaderSpan] Batch error: {ex.Message}");
            // Try to log the problematic SQL length
            Console.WriteLine($"[OpenLibraryLoaderSpan] SQL length: {sql.Length:N0} chars");
            throw;
        }
    }

    private static void AppendEscapedSpan(StringBuilder sb, ReadOnlySpan<byte> value, int? maxLength = null)
    {
        if (value.Length == 0)
        {
            sb.Append("''");
            return;
        }

        // Convert to string - this is the only allocation per field
        string str = Encoding.UTF8.GetString(value);
        
        if (maxLength.HasValue && str.Length > maxLength.Value)
        {
            str = str[..maxLength.Value];
        }

        sb.Append('\'');
        foreach (char c in str)
        {
            switch (c)
            {
                case '\'': sb.Append("''"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': break; // Skip null chars
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
    }

    private static void AppendIntSpan(StringBuilder sb, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            sb.Append('0');
            return;
        }

        // Try to parse directly from span
        if (int.TryParse(Encoding.UTF8.GetString(value), out var i))
        {
            sb.Append(i);
        }
        else
        {
            sb.Append('0');
        }
    }

    private static void AppendDateTimeSpan(StringBuilder sb, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
        {
            sb.Append("'1970-01-01 00:00:00'");
            return;
        }

        string str = Encoding.UTF8.GetString(value);
        
        // Format: "2021-12-26T21:22:34.199846"
        // Need to convert to MySQL format: "2021-12-26 21:22:34"
        if (DateTime.TryParse(str, out var dt))
        {
            sb.Append('\'');
            sb.Append(dt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append('\'');
        }
        else
        {
            sb.Append("'1970-01-01 00:00:00'");
        }
    }
}
