using MySqlConnector;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace ABC.DiscoveryCity.MariaDB;

/// <summary>
/// High-performance Span-based loader for aarecords_codes .dat.gz files.
/// This is a massive file (377GB+) so we need robust resume support.
/// Format: CSV with 7 columns (header row):
///   row_number_order_by_code, dense_rank_order_by_code, 
///   row_number_partition_by_aarecord_id_prefix_order_by_code,
///   dense_rank_partition_by_aarecord_id_prefix_order_by_code,
///   code, aarecord_id, aarecord_id_prefix
/// </summary>
public class AarecordsCodesLoader
{
    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly int _batchSize;

    // Column indices in the CSV file (after header)
    private const int COL_ROW_NUMBER_ORDER_BY_CODE = 0;
    private const int COL_DENSE_RANK_ORDER_BY_CODE = 1;
    private const int COL_ROW_NUMBER_PARTITION = 2;
    private const int COL_DENSE_RANK_PARTITION = 3;
    private const int COL_CODE = 4;
    private const int COL_AARECORD_ID = 5;
    private const int COL_AARECORD_ID_PREFIX = 6;

    private const int EXPECTED_COLUMNS = 7;  // File may have 6 or 7 columns
    private const int MIN_COLUMNS = 6;       // Minimum required (aarecord_id_prefix can be calculated)
    private const int BUFFER_SIZE = 16 * 1024 * 1024; // 16MB buffer

    public AarecordsCodesLoader(string connectionString, string filePath, int batchSize = 10000)
    {
        _connectionString = connectionString;
        _filePath = filePath;
        _batchSize = batchSize; // Higher batch size since rows are simpler than JSON
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
        long duplicateRows = 0;

        // Buffer for building INSERT statements
        var insertBuilder = new StringBuilder(4 * 1024 * 1024); // 4MB initial
        var batchCount = 0;

        // INSERT IGNORE silently skips rows that would cause duplicate key errors
        // This is the fastest method for bulk loading with potential duplicates
        const string insertPrefix = @"INSERT IGNORE INTO allthethings.aarecords_codes 
            (row_number_order_by_code, dense_rank_order_by_code, 
             row_number_partition_by_aarecord_id_prefix_order_by_code,
             dense_rank_partition_by_aarecord_id_prefix_order_by_code,
             code, aarecord_id, aarecord_id_prefix) VALUES ";

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Optimize for bulk loading
        await using (var cmd = new MySqlCommand(@"
            SET SESSION unique_checks = 0;
            SET SESSION foreign_key_checks = 0;
            SET SESSION sql_log_bin = 0;", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        Console.WriteLine("[AarecordsCodesLoader] Session optimizations applied.");

        var fileInfo = new FileInfo(_filePath);
        Console.WriteLine($"[AarecordsCodesLoader] Loading from: {_filePath}");
        Console.WriteLine($"[AarecordsCodesLoader] File size: {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB (compressed)");
        Console.WriteLine($"[AarecordsCodesLoader] Skip rows: {skipRows:N0}, Batch size: {_batchSize:N0}");
        Console.WriteLine($"[AarecordsCodesLoader] Using Span-based parsing with {BUFFER_SIZE / 1024 / 1024}MB buffer");
        Console.Out.Flush();

        // Rent buffer from pool
        byte[] bufferArray = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        Memory<byte> buffer = bufferArray.AsMemory();
        try
        {
            Console.WriteLine("[AarecordsCodesLoader] Opening file stream...");
            Console.Out.Flush();
            
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            Console.WriteLine("[AarecordsCodesLoader] Creating GZip decompression stream...");
            Console.Out.Flush();
            
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            Console.WriteLine("[AarecordsCodesLoader] Streams ready, starting processing...");
            Console.Out.Flush();

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

                // Refill when less than 2MB available
                if (available < 2 * 1024 * 1024 && !eof)
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
                    try
                    {
                        int bytesRead = gz.Read(buffer.Span.Slice(bufferEnd, toRead));
                        bufferEnd += bytesRead;

                        if (bytesRead == 0)
                        {
                            eof = true;
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        // End of compressed stream - this is normal for some GZ files
                        // when they reach the end with trailing data or unusual compression
                        // Also handles "unsupported compression method" at end of multi-part archives
                        var filePercent = (double)fs.Position / fileInfo.Length * 100;
                        if (filePercent > 99.0)
                        {
                            Console.WriteLine($"[AarecordsCodesLoader] Reached end of compressed data at {filePercent:F2}% - this is normal.");
                        }
                        else
                        {
                            Console.WriteLine($"[AarecordsCodesLoader] Compression error at {filePercent:F2}%: {ex.Message}");
                        }
                        eof = true;
                    }
                }
            }

            insertBuilder.Append(insertPrefix);

            // Reusable array for field ranges - allocated once
            Range[] fieldRanges = new Range[EXPECTED_COLUMNS];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EnsureBufferFilled();

                if (bufferStart >= bufferEnd)
                {
                    break; // No more data
                }

                // Find a VALID line ending (not one inside a quoted field)
                var span = buffer.Slice(bufferStart, bufferEnd - bufferStart).Span;
                int newlineIndex = FindValidLineEnd(span, MIN_COLUMNS);
                
                if (newlineIndex < 0)
                {
                    if (eof)
                    {
                        // Process remaining data as last line
                        newlineIndex = span.Length;
                    }
                    else
                    {
                        // Need more data - buffer may not contain complete line
                        continue;
                    }
                }

                // Extract line - keep the full span including any \r before \n
                // We'll handle \r\n vs \n when we slice for parsing
                ReadOnlySpan<byte> lineSpan = span.Slice(0, newlineIndex);
                //Console.WriteLine(Encoding.UTF8.GetString(lineSpan));
                // Remove trailing \r if present (CRLF line ending)
                if (lineSpan.Length > 0 && lineSpan[lineSpan.Length - 1] == '\r')
                {
                    lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);
                }

                bufferStart += newlineIndex + 1;
                
                // Skip Header - only on first row when not resuming
                if (currentRow == 0 && skipRows == 0)
                {
                    currentRow++; // Increment so we don't stay at 0 forever!
                    continue;
                }

                currentRow++;

                // Skip rows if resuming
                if (currentRow <= skipRows)
                {
                    if (currentRow % 10_000_000 == 0)
                    {
                        var skipPercent = (double)fs.Position / fileInfo.Length * 100;
                        Console.WriteLine($"[AarecordsCodesLoader] Skipping... {currentRow:N0} / {skipRows:N0} (file: {skipPercent:F1}%)");
                    }
                    continue;
                }

                rowsProcessed++;

                // Quick sanity check: count fields first to detect concatenated rows
                int fieldCount = CountFields(lineSpan);
                if (fieldCount > EXPECTED_COLUMNS)
                {
                    // This row has too many fields - likely two rows concatenated
                    malformedRows++;
                    if (malformedRows <= 10)
                    {
                        Console.WriteLine($"[AarecordsCodesLoader] Row {currentRow}: Too many fields ({fieldCount} > {EXPECTED_COLUMNS}) - likely concatenated rows, skipping");
                    }
                    rowsSkipped++;
                    continue;
                }

                // Parse CSV line using Span (reuses pre-allocated fieldRanges array)
                int columnCount = ParseCsvLineSpan(lineSpan, fieldRanges);

                if (columnCount < MIN_COLUMNS)
                {
                    malformedRows++;
                    if (malformedRows <= 10)
                    {
                        Console.WriteLine($"[AarecordsCodesLoader] Malformed row {currentRow}: expected {MIN_COLUMNS}+ columns, got {columnCount}");
                    }
                    rowsSkipped++;
                    continue;
                }

                // Extract key fields
                var aarecordId = ExtractField(lineSpan, fieldRanges[COL_AARECORD_ID]);

                // Build VALUES clause
                if (batchCount > 0)
                {
                    insertBuilder.Append(',');
                }

                insertBuilder.Append('(');
                AppendBigInt(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_ROW_NUMBER_ORDER_BY_CODE])); insertBuilder.Append(',');
                AppendBigInt(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_DENSE_RANK_ORDER_BY_CODE])); insertBuilder.Append(',');
                AppendBigInt(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_ROW_NUMBER_PARTITION])); insertBuilder.Append(',');
                AppendBigInt(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_DENSE_RANK_PARTITION])); insertBuilder.Append(',');
                AppendVarbinary(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_CODE])); insertBuilder.Append(',');
                
                AppendVarbinary(insertBuilder, aarecordId); insertBuilder.Append(',');
                
                // aarecord_id_prefix: use from file if available, otherwise extract from aarecord_id (part before ':')
                var prefixRange = fieldRanges[COL_AARECORD_ID_PREFIX];
                if (columnCount >= EXPECTED_COLUMNS && prefixRange.End.Value > prefixRange.Start.Value)
                {
                    AppendVarbinary(insertBuilder, ExtractField(lineSpan, prefixRange));
                }
                else
                {
                    // Calculate: everything before first ':' in aarecord_id
                    var colonIdx = aarecordId.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        AppendVarbinary(insertBuilder, aarecordId.Substring(0, colonIdx));
                    }
                    else
                    {
                        AppendVarbinary(insertBuilder, aarecordId); // fallback to full value
                    }
                }
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

                    // Progress report every 500k rows or 60 seconds
                    var now = sw.Elapsed;
                    if (rowsProcessed % 500_000 == 0 || (now - lastProgressTime).TotalSeconds >= 60)
                    {
                        var elapsed = sw.Elapsed.TotalSeconds;
                        var recentRate = (rowsProcessed - lastProgressRows) / Math.Max(1, (now - lastProgressTime).TotalSeconds);
                        var compressedPercent = (double)fs.Position / fileInfo.Length * 100;
                        var bytesPerSec = (fs.Position - lastBytesRead) / Math.Max(1, (now - lastProgressTime).TotalSeconds);

                        // Estimate time remaining
                        var remainingBytes = fileInfo.Length - fs.Position;
                        var etaSeconds = bytesPerSec > 0 ? remainingBytes / bytesPerSec : 0;
                        var eta = TimeSpan.FromSeconds(etaSeconds);

                        Console.WriteLine($"[AarecordsCodesLoader] Row: {currentRow:N0} | Processed: {rowsProcessed:N0} | Inserted: {rowsInserted:N0} | " +
                                          $"Rate: {recentRate:N0}/s | File: {compressedPercent:F2}% | ETA: {eta:hh\\:mm\\:ss} | Elapsed: {sw.Elapsed:hh\\:mm\\:ss}");

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

        Console.WriteLine($"\n[AarecordsCodesLoader] ========== COMPLETE ==========");
        Console.WriteLine($"[AarecordsCodesLoader] Processed: {rowsProcessed:N0}");
        Console.WriteLine($"[AarecordsCodesLoader] Inserted: {rowsInserted:N0}");
        Console.WriteLine($"[AarecordsCodesLoader] Skipped: {rowsSkipped:N0}");
        Console.WriteLine($"[AarecordsCodesLoader] Malformed: {malformedRows:N0}");
        Console.WriteLine($"[AarecordsCodesLoader] Time: {sw.Elapsed}");
        if (sw.Elapsed.TotalSeconds > 0)
        {
            Console.WriteLine($"[AarecordsCodesLoader] Avg Rate: {rowsProcessed / sw.Elapsed.TotalSeconds:N0} rows/sec");
        }

        return (rowsProcessed, rowsInserted, rowsSkipped);
    }

    /// <summary>
    /// Validates field structure by checking for proper field boundaries.
    /// A valid CSV row has fields ending with:
    ///   - Unquoted: digits followed by comma
    ///   - Quoted: closing quote followed by comma or end-of-line
    /// 
    /// Valid patterns after a closing quote:
    ///   - "," followed by " (next quoted field)
    ///   - "," followed by digit (next numeric field)  
    ///   - " at end of line (last field)
    /// 
    /// Invalid pattern (indicates corruption):
    ///   - "," followed by letter (like "aacid") - missing closing quote on previous field
    /// 
    /// Returns the number of VALID field separators found, plus 1.
    /// If corruption detected, returns a high number to trigger skip.
    /// </summary>
    private static int CountFields(ReadOnlySpan<byte> lineSpan)
    {
        int fieldCount = 1; // Start with 1 (there's always at least one field)
        
        for (int i = 0; i < lineSpan.Length - 1; i++)
        {
            byte b = lineSpan[i];
            byte next = lineSpan[i + 1];
            
            // Look for "," pattern - end of a quoted field followed by comma
            if (b == (byte)'"' && next == (byte)',')
            {
                // Valid quoted field separator - check what comes after the comma
                if (i + 2 < lineSpan.Length)
                {
                    byte afterComma = lineSpan[i + 2];
                    
                    // Valid: next field starts with " (quoted) or digit (numeric)
                    if (afterComma == (byte)'"' || (afterComma >= (byte)'0' && afterComma <= (byte)'9'))
                    {
                        fieldCount++;
                        i++; // Skip the comma we just processed
                    }
                    else
                    {
                        // CORRUPTION: "," followed by letter like 'a' in "aacid"
                        // This means the previous quoted field was never closed properly
                        // Return high number to trigger skip
                        return 100;
                    }
                }
                else
                {
                    // "," at end of line - unusual but count it
                    fieldCount++;
                }
            }
            // Look for digit followed by comma - unquoted numeric field separator
            else if (b >= (byte)'0' && b <= (byte)'9' && next == (byte)',')
            {
                // Valid numeric field separator
                fieldCount++;
            }
        }
        
        return fieldCount;
    }

    /// <summary>
    /// Parse a CSV line handling quoted fields - Span-based version
    /// </summary>
    private static int ParseCsvLineSpan(ReadOnlySpan<byte> lineSpan, Span<Range> fieldRanges)
    {
        int fieldCount = 0;
        bool inQuotes = false;
        int fieldStart = 0;
        
        //Console.WriteLine(Encoding.UTF8.GetString(lineSpan));
        for (int i = 0; i < lineSpan.Length; i++)
        {
            byte b = lineSpan[i];
//            Console.WriteLine((char)b);
            if (inQuotes)
            {
                if (b == (byte)'"')
                {
                    // Check for escaped quote
                    if (i + 1 < lineSpan.Length && lineSpan[i + 1] == (byte)'"')
                    {
                        i++; // Skip next quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
            }
            else
            {
                if (b == (byte)'"')
                {
                    inQuotes = true;
                }
                else if (b == (byte)',')
                {
                    if (fieldCount < fieldRanges.Length)
                    {
                        fieldRanges[fieldCount] = fieldStart..i;
                        fieldCount++;
                    }
                    fieldStart = i + 1;
                }
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

    /// <summary>
    /// Find a valid line ending by locating the START of the NEXT row.
    /// Delegates to FindStartOfNextRow, then returns the position just before it.
    /// </summary>
    private static int FindValidLineEnd(ReadOnlySpan<byte> span, int minFields)
    {
        int nextRowStart = FindStartOfNextRow(span);
        
        if (nextRowStart < 0)
        {
            return -1; // No valid next row found
        }
        
        // The line end is the \n (or \r\n) just before the next row start
        // nextRowStart points to the first digit of the next row
        // So we need to go back to find the \n
        int lineEnd = nextRowStart - 1;
        
        // Skip back over \r if present (\r\n case)
        if (lineEnd > 0 && span[lineEnd] == '\r')
        {
            lineEnd--;
        }
        
        // lineEnd should now be at \n
        if (lineEnd >= 0 && span[lineEnd] == '\n')
        {
            return lineEnd;
        }
        
        // Edge case: nextRowStart is at position 0 (first row)
        if (nextRowStart == 0)
        {
            return -1; // No line before the first row
        }
        
        return -1;
    }
    
    /// <summary>
    /// Find the START of the next valid CSV row.
    /// Pattern: \n (or \r\n) + digits + comma + digits + comma + digits + comma + digits + comma
    /// This matches the first 4 numeric columns: row_number, dense_rank, row_number_partition, dense_rank_partition
    /// 
    /// Example: \n1234567,994627,220110,164033,
    /// 
    /// We rely on the 4 numeric fields pattern plus data validation after parsing
    /// to handle corrupted rows.
    /// 
    /// Returns the position of the first digit of the next row, or -1 if not found.
    /// </summary>
    private static int FindStartOfNextRow(ReadOnlySpan<byte> span)
    {
        int searchStart = 0;
        
        while (searchStart < span.Length)
        {
            // Find next newline
            var remaining = span.Slice(searchStart);
            int relativeNewline = remaining.IndexOf((byte)'\n');
            
            if (relativeNewline < 0)
            {
                return -1; // No more newlines
            }
            
            int absoluteNewline = searchStart + relativeNewline;
            int pos = absoluteNewline + 1;
            
            // Skip any \r after \n
            while (pos < span.Length && span[pos] == '\r')
            {
                pos++;
            }
            
            if (pos >= span.Length)
            {
                return -1; // Need more data
            }
            
            int digitStart = pos;
            
            // We need to match: digits,digits,digits,digits,
            // (4 numeric fields followed by commas)
            bool valid = true;
            for (int field = 0; field < 4 && valid; field++)
            {
                // Must have at least one digit
                if (pos >= span.Length || span[pos] < (byte)'0' || span[pos] > (byte)'9')
                {
                    valid = false;
                    break;
                }
                
                // Skip all digits
                while (pos < span.Length && span[pos] >= (byte)'0' && span[pos] <= (byte)'9')
                {
                    pos++;
                }
                
                // Skip optional spaces
                while (pos < span.Length && span[pos] == ' ')
                {
                    pos++;
                }
                
                // Must have comma after digits
                if (pos >= span.Length || span[pos] != (byte)',')
                {
                    valid = false;
                    break;
                }
                
                pos++; // Skip the comma
            }
            
            if (valid)
            {
                // Found valid row start with 4 numeric fields!
                return digitStart;
            }
            
            // Not a valid row start - continue searching
            searchStart = absoluteNewline + 1;
        }
        
        return -1;
    }
    
    /// <summary>
    /// Check if a span contains only ASCII digits (0-9).
    /// Used to validate that first field is row_number_order_by_code.
    /// </summary>
    private static bool IsNumeric(ReadOnlySpan<byte> span)
    {
        if (span.Length == 0)
            return false;
            
        foreach (byte b in span)
        {
            if (b < (byte)'0' || b > (byte)'9')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Extract field value from span, handling quotes and escaping
    /// </summary>
    private static string ExtractField(ReadOnlySpan<byte> lineSpan, Range range)
    {
        var field = lineSpan[range];

        if (field.Length == 0)
            return string.Empty;

        // Check for quoted field
        if (field.Length >= 2 && field[0] == (byte)'"' && field[^1] == (byte)'"')
        {
            var inner = field.Slice(1, field.Length - 2);

            // Check if contains escaped quotes
            if (inner.IndexOf((byte)'"') >= 0)
            {
                return Encoding.UTF8.GetString(inner).Replace("\"\"", "\"");
            }

            return Encoding.UTF8.GetString(inner);
        }

        return Encoding.UTF8.GetString(field);
    }

    /// <summary>
    /// Parse a CSV line handling quoted fields
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check for escaped quote
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++; // Skip next quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private async Task<int> ExecuteBatchAsync(MySqlConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.CommandTimeout = 600; // 10 minutes for large batches
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AarecordsCodesLoader] Batch error: {ex.Message}");
            throw;
        }
    }

    private static void AppendBigInt(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value) || value == "\\N")
        {
            sb.Append('0');
            return;
        }

        if (long.TryParse(value, out var num))
        {
            sb.Append(num);
        }
        else
        {
            sb.Append('0');
        }
    }

    private static void AppendVarbinary(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value) || value == "\\N")
        {
            sb.Append("''");
            return;
        }

        sb.Append('\'');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\'': sb.Append("''"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
    }
}