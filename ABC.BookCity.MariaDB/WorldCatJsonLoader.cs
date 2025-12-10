using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using ZstdSharp;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// High-performance Span-based loader for WorldCat JSONL.ZST files.
/// Uses buffer pooling and Span-based line extraction like WorldCatLoader.
/// </summary>
public class WorldCatJsonLoader
{
    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly int _batchSize;

    private const int BUFFER_SIZE = 32 * 1024 * 1024; // 32MB buffer - JSON lines can be large

    public WorldCatJsonLoader(string connectionString, string filePath, int batchSize = 2000)
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
        long parseErrors = 0;

        // Buffer for building INSERT statements
        var insertBuilder = new StringBuilder(8 * 1024 * 1024); // 8MB - JSON records are big
        var batchCount = 0;

        const string insertPrefix = @"INSERT IGNORE INTO allthethings.worldcat_records 
            (aacid, oclc_number, record_type, title, creator, contributors, 
             date_text, date_year, language, publisher, publication_place,
             general_format, specific_format, subjects, summaries, 
             isbn, issn, merged_oclc_numbers, open_access_links, full_record) VALUES ";

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Optimize for bulk loading (max_allowed_packet is read-only at session level)
        await using (var cmd = new MySqlCommand(@"
            SET SESSION unique_checks = 0;
            SET SESSION foreign_key_checks = 0;
            SET SESSION sql_log_bin = 0;", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var fileInfo = new FileInfo(_filePath);
        Console.WriteLine($"[WorldCatJsonLoader] Loading from: {_filePath}");
        Console.WriteLine($"[WorldCatJsonLoader] File size: {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB (compressed)");
        Console.WriteLine($"[WorldCatJsonLoader] Skip rows: {skipRows:N0}, Batch size: {_batchSize:N0}");
        Console.WriteLine($"[WorldCatJsonLoader] Using Span-based parsing with {BUFFER_SIZE / 1024 / 1024}MB buffer");

        // Rent buffer from pool - keep array reference for Return()
        byte[] bufferArray = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        Memory<byte> buffer = bufferArray.AsMemory();
        
        // Track corruption status outside try block for completion message
        bool zstCorrupted = false;
        long currentRow = 0;

        try
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var zstdStream = new DecompressionStream(fs);

            int bufferStart = 0;
            int bufferEnd = 0;
            bool eof = false;

            var lastProgressTime = sw.Elapsed;
            long lastProgressRows = 0;

            // Helper to ensure buffer is well-filled
            void EnsureBufferFilled()
            {
                int available = bufferEnd - bufferStart;

                // Refill when less than 4MB available (JSON lines can be big)
                if (available < 4 * 1024 * 1024 && !eof && !zstCorrupted)
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
                    try
                    {
                        int bytesRead = zstdStream.Read(buffer.Span.Slice(bufferEnd, toRead));
                        bufferEnd += bytesRead;

                        if (bytesRead == 0)
                        {
                            eof = true;
                        }
                    }
                    catch (ZstdException ex)
                    {
                        // Handle ZST corruption (often from incomplete download)
                        zstCorrupted = true;
                        Console.WriteLine();
                        Console.WriteLine($"[WorldCatJsonLoader] ========== ZST CORRUPTION DETECTED ==========");
                        Console.WriteLine($"[WorldCatJsonLoader] Error: {ex.Message}");
                        Console.WriteLine($"[WorldCatJsonLoader] File position: {fs.Position:N0} bytes ({(double)fs.Position / fileInfo.Length * 100:F2}% of compressed file)");
                        Console.WriteLine($"[WorldCatJsonLoader] Rows processed so far: {rowsProcessed:N0}");
                        Console.WriteLine($"[WorldCatJsonLoader] Current row number: {currentRow:N0}");
                        Console.WriteLine($"[WorldCatJsonLoader] This usually means the file is INCOMPLETE (still downloading or interrupted).");
                        Console.WriteLine($"[WorldCatJsonLoader] Will flush pending batch and exit gracefully.");
                        Console.WriteLine($"[WorldCatJsonLoader] To RESUME later, use skipRows = {currentRow}");
                        Console.WriteLine();
                    }
                }
            }

            insertBuilder.Append(insertPrefix);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                EnsureBufferFilled();

                // Exit if ZST corruption detected or no more data
                if (zstCorrupted || bufferStart >= bufferEnd)
                {
                    break;
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
                            Console.WriteLine($"[WorldCatJsonLoader] WARNING: Line too long at row {currentRow + 1}, skipping");
                            // Find next newline and skip this line
                            bufferStart = bufferEnd;
                            rowsSkipped++;
                            continue;
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
                        Console.WriteLine($"[WorldCatJsonLoader] Skipping... {currentRow:N0} / {skipRows:N0}");
                    }
                    continue;
                }

                rowsProcessed++;

                // Skip empty lines
                if (lineSpan.Length == 0)
                {
                    rowsSkipped++;
                    continue;
                }

                // Parse JSON from the line span
                try
                {
                    var record = ParseWorldCatRecord(lineSpan);
                    if (record == null)
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
                    AppendEscaped(insertBuilder, record.Aacid); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.OclcNumber); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.RecordType); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.Title, 60000); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.Creator, 500); insertBuilder.Append(',');
                    AppendJsonOrNull(insertBuilder, record.Contributors); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.DateText, 100); insertBuilder.Append(',');
                    AppendIntOrNull(insertBuilder, record.DateYear); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.Language, 10); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.Publisher, 500); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.PublicationPlace, 255); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.GeneralFormat, 50); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.SpecificFormat, 50); insertBuilder.Append(',');
                    AppendJsonOrNull(insertBuilder, record.Subjects); insertBuilder.Append(',');
                    AppendJsonOrNull(insertBuilder, record.Summaries); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.Isbn, 20); insertBuilder.Append(',');
                    AppendEscapedOrNull(insertBuilder, record.Issn, 20); insertBuilder.Append(',');
                    AppendJsonOrNull(insertBuilder, record.MergedOclcNumbers); insertBuilder.Append(',');
                    AppendJsonOrNull(insertBuilder, record.OpenAccessLinks); insertBuilder.Append(',');
                    AppendJsonOrNull(insertBuilder, record.FullRecord);
                    insertBuilder.Append(')');

                    batchCount++;
                }
                catch (Exception ex)
                {
                    parseErrors++;
                    if (parseErrors <= 10)
                    {
                        Console.WriteLine($"[WorldCatJsonLoader] Parse error row {currentRow}: {ex.Message}");
                    }
                    rowsSkipped++;
                    continue;
                }

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
                        var compressedPercent = (double)fs.Position / fileInfo.Length * 100;

                        Console.WriteLine($"[WorldCatJsonLoader] Row: {currentRow:N0} | Processed: {rowsProcessed:N0} | Inserted: {rowsInserted:N0} | " +
                                          $"Rate: {recentRate:N0}/s | File: {compressedPercent:F1}% | Elapsed: {sw.Elapsed:hh\\:mm\\:ss}");

                        lastProgressTime = now;
                        lastProgressRows = rowsProcessed;
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

        Console.WriteLine($"\n[WorldCatJsonLoader] ========== {(zstCorrupted ? "PARTIAL (ZST CORRUPTION)" : "COMPLETE")} ==========");
        Console.WriteLine($"[WorldCatJsonLoader] Processed: {rowsProcessed:N0}");
        Console.WriteLine($"[WorldCatJsonLoader] Inserted: {rowsInserted:N0}");
        Console.WriteLine($"[WorldCatJsonLoader] Skipped: {rowsSkipped:N0}");
        Console.WriteLine($"[WorldCatJsonLoader] Parse Errors: {parseErrors:N0}");
        Console.WriteLine($"[WorldCatJsonLoader] Time: {sw.Elapsed}");
        if (sw.Elapsed.TotalSeconds > 0)
        {
            Console.WriteLine($"[WorldCatJsonLoader] Avg Rate: {rowsProcessed / sw.Elapsed.TotalSeconds:N0} rows/sec");
        }
        if (zstCorrupted)
        {
            Console.WriteLine($"[WorldCatJsonLoader] *** File appears INCOMPLETE - stopped at row {currentRow:N0} ***");
            Console.WriteLine($"[WorldCatJsonLoader] *** To RESUME after fixing the file: skipRows = {currentRow} ***");
        }

        return (rowsProcessed, rowsInserted, rowsSkipped);
    }

    private WorldCatRecordDto? ParseWorldCatRecord(ReadOnlySpan<byte> jsonBytes)
    {
        var reader = new Utf8JsonReader(jsonBytes);
        var dto = new WorldCatRecordDto();

        try
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            // aacid is at root level
            if (root.TryGetProperty("aacid", out var aacidElem))
            {
                dto.Aacid = aacidElem.GetString() ?? "";
            }
            else
            {
                return null; // Skip records without aacid
            }

            // metadata contains oclc_number, type, and record
            if (root.TryGetProperty("metadata", out var metadata))
            {
                if (metadata.TryGetProperty("oclc_number", out var oclcElem))
                    dto.OclcNumber = oclcElem.GetString();

                if (metadata.TryGetProperty("type", out var typeElem))
                    dto.RecordType = typeElem.GetString();

                // The actual bibliographic data is in metadata.record
                if (metadata.TryGetProperty("record", out var record))
                {
                    // Check for not_found records
                    if (record.TryGetProperty("not_found", out _))
                    {
                        dto.RecordType = "not_found";
                        return dto; // Still insert, but minimal data
                    }

                    // Title
                    if (record.TryGetProperty("title", out var titleElem))
                        dto.Title = titleElem.GetString();
                    else if (record.TryGetProperty("titleInfo", out var titleInfo) &&
                             titleInfo.TryGetProperty("text", out var titleText))
                        dto.Title = titleText.GetString();

                    // Creator
                    if (record.TryGetProperty("creator", out var creatorElem))
                        dto.Creator = creatorElem.GetString();

                    // Contributors (store as JSON)
                    if (record.TryGetProperty("contributors", out var contribElem))
                        dto.Contributors = contribElem.GetRawText();

                    // Date
                    if (record.TryGetProperty("date", out var dateElem))
                        dto.DateText = dateElem.GetString();

                    if (record.TryGetProperty("machineReadableDate", out var mrdElem))
                    {
                        var mrd = mrdElem.GetString();
                        if (!string.IsNullOrEmpty(mrd) && mrd.Length >= 4 && int.TryParse(mrd.AsSpan(0, 4), out var year))
                            dto.DateYear = year;
                    }

                    // Language
                    if (record.TryGetProperty("language", out var langElem))
                        dto.Language = langElem.GetString();

                    // Publisher
                    if (record.TryGetProperty("publisher", out var pubElem))
                        dto.Publisher = pubElem.GetString();

                    // Publication Place
                    if (record.TryGetProperty("publicationPlace", out var placeElem))
                        dto.PublicationPlace = placeElem.GetString();

                    // Format
                    if (record.TryGetProperty("generalFormat", out var gfElem))
                        dto.GeneralFormat = gfElem.GetString();
                    if (record.TryGetProperty("specificFormat", out var sfElem))
                        dto.SpecificFormat = sfElem.GetString();

                    // Subjects (store as JSON array)
                    if (record.TryGetProperty("subjectsText", out var subjElem))
                        dto.Subjects = subjElem.GetRawText();

                    // Summaries (store as JSON array)
                    if (record.TryGetProperty("summariesText", out var summElem))
                        dto.Summaries = summElem.GetRawText();

                    // Merged OCLC numbers
                    if (record.TryGetProperty("mergedOclcNumbers", out var mergedElem))
                        dto.MergedOclcNumbers = mergedElem.GetRawText();

                    // Open Access Links
                    if (record.TryGetProperty("openAccessLinks", out var linksElem))
                        dto.OpenAccessLinks = linksElem.GetRawText();

                    // Store the full record JSON for future use
                    dto.FullRecord = record.GetRawText();
                }
            }

            return dto;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> ExecuteBatchAsync(MySqlConnection connection, string sql, CancellationToken ct)
    {
        try
        {
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.CommandTimeout = 900; // 15 minutes for large batches with JSON
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WorldCatJsonLoader] Batch error: {ex.Message}");
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
                case '\0': break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('\'');
    }

    private static void AppendEscapedOrNull(StringBuilder sb, string? value, int? maxLength = null)
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

    private static void AppendJsonOrNull(StringBuilder sb, string? jsonValue)
    {
        if (string.IsNullOrEmpty(jsonValue))
        {
            sb.Append("NULL");
            return;
        }
        AppendEscaped(sb, jsonValue);
    }

    private static void AppendIntOrNull(StringBuilder sb, int? value)
    {
        if (value.HasValue)
        {
            sb.Append(value.Value);
        }
        else
        {
            sb.Append("NULL");
        }
    }

    private class WorldCatRecordDto
    {
        public string Aacid { get; set; } = "";
        public string? OclcNumber { get; set; }
        public string? RecordType { get; set; }
        public string? Title { get; set; }
        public string? Creator { get; set; }
        public string? Contributors { get; set; }
        public string? DateText { get; set; }
        public int? DateYear { get; set; }
        public string? Language { get; set; }
        public string? Publisher { get; set; }
        public string? PublicationPlace { get; set; }
        public string? GeneralFormat { get; set; }
        public string? SpecificFormat { get; set; }
        public string? Subjects { get; set; }
        public string? Summaries { get; set; }
        public string? Isbn { get; set; }
        public string? Issn { get; set; }
        public string? MergedOclcNumbers { get; set; }
        public string? OpenAccessLinks { get; set; }
        public string? FullRecord { get; set; }
    }
}