using System.Buffers;
using System.Diagnostics;
using System.Text;
using MySqlConnector;

namespace ABC.DiscoveryCity.MariaDB;

/// <summary>
/// High-performance Span-based loader for HathiTrust TSV files.
/// Loads the hathi_full.txt format (26 tab-separated columns, no header).
/// Uses buffer pooling and Span-based line extraction for optimal performance.
/// </summary>
public class HathiTrustLoader
{
    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly int _batchSize;

    // Column indices in the TSV file
    private const int COL_HTID = 0;
    private const int COL_ACCESS = 1;
    private const int COL_RIGHTS = 2;
    private const int COL_HT_BIB_KEY = 3;
    private const int COL_DESCRIPTION = 4;
    private const int COL_SOURCE = 5;
    private const int COL_SOURCE_BIB_NUM = 6;
    private const int COL_OCLC_NUM = 7;
    private const int COL_ISBN = 8;
    private const int COL_ISSN = 9;
    private const int COL_LCCN = 10;
    private const int COL_TITLE = 11;
    private const int COL_IMPRINT = 12;
    private const int COL_RIGHTS_REASON_CODE = 13;
    private const int COL_RIGHTS_TIMESTAMP = 14;
    private const int COL_US_GOV_DOC_FLAG = 15;
    private const int COL_RIGHTS_DATE_USED = 16;
    private const int COL_PUB_PLACE = 17;
    private const int COL_LANG = 18;
    private const int COL_BIB_FMT = 19;
    private const int COL_COLLECTION_CODE = 20;
    private const int COL_CONTENT_PROVIDER_CODE = 21;
    private const int COL_RESPONSIBLE_ENTITY_CODE = 22;
    private const int COL_DIGITIZATION_AGENT_CODE = 23;
    private const int COL_ACCESS_PROFILE_CODE = 24;
    private const int COL_AUTHOR = 25;

    private const int EXPECTED_COLUMNS = 26;
    private const int BUFFER_SIZE = 16 * 1024 * 1024; // 16MB buffer

    public HathiTrustLoader(string connectionString, string filePath, int batchSize = 5000)
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

        // Buffer for building INSERT statements
        var insertBuilder = new StringBuilder(1024 * 1024); // 1MB initial
        var batchCount = 0;

        const string insertPrefix = @"INSERT IGNORE INTO allthethings.hathitrust_records 
            (htid, access, rights, ht_bib_key, description, source, source_bib_num, 
             oclc_num, isbn, issn, lccn, title, imprint, rights_reason_code, 
             rights_timestamp, us_gov_doc_flag, rights_date_used, pub_place, lang, 
             bib_fmt, collection_code, content_provider_code, responsible_entity_code, 
             digitization_agent_code, access_profile_code, author) VALUES ";

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

        var fileInfo = new FileInfo(_filePath);
        Console.WriteLine($"[HathiTrustLoader] Loading from: {_filePath}");
        Console.WriteLine($"[HathiTrustLoader] File size: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"[HathiTrustLoader] Skip rows: {skipRows:N0}, Batch size: {_batchSize:N0}");
        Console.WriteLine($"[HathiTrustLoader] Using Span-based parsing with {BUFFER_SIZE / 1024 / 1024}MB buffer");

        // Rent buffer from pool - keep array reference for Return()
        byte[] bufferArray = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        Memory<byte> buffer = bufferArray.AsMemory();

        // Reusable array for field ranges - allocated once
        Range[] fieldRanges = new Range[EXPECTED_COLUMNS];

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

                // Refill when less than 2MB available
                if (available < 2 * 1024 * 1024 && !eof)
                {
                    // Compact: move remaining data to front using span-based copy
                    if (bufferStart > 0 && available > 0)
                    {
                        buffer.Slice(bufferStart, available).CopyTo(buffer.Slice(0, available));
                    }
                    bufferStart = 0;
                    bufferEnd = available;

                    // Fill rest of buffer using span-based read
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
                        // Need more data
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
                        Console.WriteLine($"[HathiTrustLoader] Skipping... {currentRow:N0} / {skipRows:N0} (file: {skipPercent:F1}%)");
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

                // Parse TSV line using Span (reuses pre-allocated fieldRanges array)
                int columnCount = ParseTsvLineSpan(lineSpan, fieldRanges);

                if (columnCount != EXPECTED_COLUMNS)
                {
                    malformedRows++;
                    if (malformedRows <= 10)
                    {
                        Console.WriteLine($"[HathiTrustLoader] Malformed row {currentRow}: expected {EXPECTED_COLUMNS} columns, got {columnCount}");
                    }
                    rowsSkipped++;
                    continue;
                }

                // Build VALUES clause
                if (batchCount > 0)
                {
                    insertBuilder.Append(',');
                }

                insertBuilder.Append('(');
                AppendEscaped(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_HTID])); insertBuilder.Append(',');
                AppendEscaped(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_ACCESS])); insertBuilder.Append(',');
                AppendEscaped(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_RIGHTS])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_HT_BIB_KEY])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_DESCRIPTION]), 500); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_SOURCE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_SOURCE_BIB_NUM])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_OCLC_NUM])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_ISBN])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_ISSN])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_LCCN])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_TITLE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_IMPRINT])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_RIGHTS_REASON_CODE])); insertBuilder.Append(',');
                AppendTimestampOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_RIGHTS_TIMESTAMP])); insertBuilder.Append(',');
                AppendIntOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_US_GOV_DOC_FLAG])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_RIGHTS_DATE_USED])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_PUB_PLACE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_LANG])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_BIB_FMT])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_COLLECTION_CODE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_CONTENT_PROVIDER_CODE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_RESPONSIBLE_ENTITY_CODE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_DIGITIZATION_AGENT_CODE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_ACCESS_PROFILE_CODE])); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, ExtractField(lineSpan, fieldRanges[COL_AUTHOR]));
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
                    if (rowsProcessed % 100_000 == 0 || (now - lastProgressTime).TotalSeconds >= 30)
                    {
                        var elapsed = sw.Elapsed.TotalSeconds;
                        var recentRate = (rowsProcessed - lastProgressRows) / Math.Max(1, (now - lastProgressTime).TotalSeconds);
                        var filePercent = (double)fs.Position / fileInfo.Length * 100;
                        var bytesPerSec = (fs.Position - lastBytesRead) / Math.Max(1, (now - lastProgressTime).TotalSeconds);

                        // Estimate time remaining
                        var remainingBytes = fileInfo.Length - fs.Position;
                        var etaSeconds = bytesPerSec > 0 ? remainingBytes / bytesPerSec : 0;
                        var eta = TimeSpan.FromSeconds(etaSeconds);

                        Console.WriteLine($"[HathiTrustLoader] Row: {currentRow:N0} | Processed: {rowsProcessed:N0} | Inserted: {rowsInserted:N0} | " +
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
            // Return the original array, not the Memory<byte>
            ArrayPool<byte>.Shared.Return(bufferArray);
        }

        // Re-enable checks
        await using (var cmd = new MySqlCommand(@"
            SET SESSION unique_checks = 1;
            SET SESSION foreign_key_checks = 1;", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        Console.WriteLine($"\n[HathiTrustLoader] ========== COMPLETE ==========");
        Console.WriteLine($"[HathiTrustLoader] Processed: {rowsProcessed:N0}");
        Console.WriteLine($"[HathiTrustLoader] Inserted: {rowsInserted:N0}");
        Console.WriteLine($"[HathiTrustLoader] Skipped: {rowsSkipped:N0}");
        Console.WriteLine($"[HathiTrustLoader] Malformed: {malformedRows:N0}");
        Console.WriteLine($"[HathiTrustLoader] Time: {sw.Elapsed}");
        if (sw.Elapsed.TotalSeconds > 0)
        {
            Console.WriteLine($"[HathiTrustLoader] Avg Rate: {rowsProcessed / sw.Elapsed.TotalSeconds:N0} rows/sec");
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

    /// <summary>
    /// Extract field value from span
    /// </summary>
    private static string ExtractField(ReadOnlySpan<byte> lineSpan, Range range)
    {
        var field = lineSpan[range];

        if (field.Length == 0)
            return string.Empty;

        return Encoding.UTF8.GetString(field);
    }

    private async Task<int> ExecuteBatchAsync(MySqlConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.CommandTimeout = 300; // 5 minutes for large batches
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HathiTrustLoader] Batch error: {ex.Message}");
            throw;
        }
    }

    private static void AppendEscaped(StringBuilder sb, string value)
    {
        sb.Append('\'');
        foreach (char c in value)
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

    private static void AppendEscapedOrNull(StringBuilder sb, string value, int? maxLength = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            sb.Append("NULL");
            return;
        }

        var val = maxLength.HasValue && value.Length > maxLength.Value
            ? value[..maxLength.Value]
            : value;
        AppendEscaped(sb, val);
    }

    private static void AppendTimestampOrNull(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            sb.Append("NULL");
            return;
        }

        // Format: "2011-09-15 04:30:52"
        if (DateTime.TryParse(value, out var dt))
        {
            sb.Append('\'');
            sb.Append(dt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.Append('\'');
        }
        else
        {
            sb.Append("NULL");
        }
    }

    private static void AppendIntOrNull(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            sb.Append("NULL");
            return;
        }

        if (int.TryParse(value, out var i))
        {
            sb.Append(i);
        }
        else
        {
            sb.Append("NULL");
        }
    }
}