using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using MySqlConnector;

namespace ABC.DiscoveryCity.MariaDB;

/// <summary>
/// High-performance loader for WorldCat .dat.gz files using Span-based parsing.
/// Format: CSV with 5 columns (aacid, primary_id, md5, byte_offset, byte_length)
/// Uses INSERT IGNORE to handle duplicates when resuming.
/// </summary>
public class WorldCatLoader
{
    private readonly string _connectionString;
    private readonly string _filePath;
    private readonly int _batchSize;
    
    // Column indices in the CSV file (after header)
    private const int COL_AACID = 0;
    private const int COL_PRIMARY_ID = 1;
    private const int COL_MD5 = 2;
    private const int COL_BYTE_OFFSET = 3;
    private const int COL_BYTE_LENGTH = 4;
    
    private const int EXPECTED_COLUMNS = 5;
    private const int BUFFER_SIZE = 8 * 1024 * 1024; // 8MB buffer for large file
    
    public WorldCatLoader(string connectionString, string filePath, int batchSize = 5000)
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
        var insertBuilder = new StringBuilder(2 * 1024 * 1024); // 2MB initial
        var batchCount = 0;
        
        const string insertPrefix = @"INSERT IGNORE INTO allthethings.annas_archive_meta__aacid__worldcat 
            (aacid, primary_id, md5, byte_offset, byte_length) VALUES ";
        
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
        Console.WriteLine($"[WorldCatLoader] Loading from: {_filePath}");
        Console.WriteLine($"[WorldCatLoader] File size: {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB (compressed)");
        Console.WriteLine($"[WorldCatLoader] Skip rows: {skipRows:N0}, Batch size: {_batchSize:N0}");
        
        // Use Span-based parsing for better performance with large files
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        
        try
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            
            int bufferStart = 0;
            int bufferEnd = 0;
            bool eof = false;
            bool headerSkipped = false;
            long currentRow = 0;
            
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
                    bufferStart = 0;
                    bufferEnd = available;
                    
                    // Fill rest of buffer
                    int toRead = buffer.Length - bufferEnd;
                    int bytesRead = gz.Read(buffer, bufferEnd, toRead);
                    bufferEnd += bytesRead;
                    
                    if (bytesRead == 0)
                    {
                        eof = true;
                    }
                }
            }
            
            insertBuilder.Append(insertPrefix);
            
            var lastProgressTime = sw.Elapsed;
            long lastProgressRows = 0;
            
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                EnsureBufferFilled();
                
                if (bufferStart >= bufferEnd)
                {
                    break; // No more data
                }
                
                // Find newline in buffer
                var span = buffer.AsSpan(bufferStart, bufferEnd - bufferStart);
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
                
                // Extract line (without newline)
                var lineSpan = span.Slice(0, newlineIndex);
                if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                {
                    lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);
                }
                
                bufferStart += newlineIndex + 1;
                
                // Skip header row
                if (!headerSkipped)
                {
                    headerSkipped = true;
                    continue;
                }
                
                currentRow++;
                
                // Skip rows if resuming
                if (currentRow <= skipRows)
                {
                    if (currentRow % 1_000_000 == 0)
                    {
                        Console.WriteLine($"[WorldCatLoader] Skipping... {currentRow:N0} / {skipRows:N0}");
                    }
                    continue;
                }
                
                rowsProcessed++;
                
                // Parse CSV line using Span
                var line = Encoding.UTF8.GetString(lineSpan);
                var columns = ParseCsvLine(line);
                
                if (columns.Length != EXPECTED_COLUMNS)
                {
                    malformedRows++;
                    if (malformedRows <= 10)
                    {
                        Console.WriteLine($"[WorldCatLoader] Malformed row {currentRow}: expected {EXPECTED_COLUMNS} columns, got {columns.Length}");
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
                AppendEscaped(insertBuilder, columns[COL_AACID]); insertBuilder.Append(',');
                AppendEscapedOrNull(insertBuilder, columns[COL_PRIMARY_ID]); insertBuilder.Append(',');
                AppendMd5OrNull(insertBuilder, columns[COL_MD5]); insertBuilder.Append(',');
                AppendBigInt(insertBuilder, columns[COL_BYTE_OFFSET]); insertBuilder.Append(',');
                AppendBigInt(insertBuilder, columns[COL_BYTE_LENGTH]);
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
                    
                    // Progress report every 100k rows or 30 seconds
                    var now = sw.Elapsed;
                    if (rowsProcessed % 100_000 == 0 || (now - lastProgressTime).TotalSeconds >= 30)
                    {
                        var elapsed = sw.Elapsed.TotalSeconds;
                        var rate = rowsProcessed / elapsed;
                        var recentRate = (rowsProcessed - lastProgressRows) / (now - lastProgressTime).TotalSeconds;
                        var compressedPercent = (double)fs.Position / fileInfo.Length * 100;
                        
                        Console.WriteLine($"[WorldCatLoader] Row: {currentRow:N0} | Processed: {rowsProcessed:N0} | Inserted: {rowsInserted:N0} | " +
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
            ArrayPool<byte>.Shared.Return(buffer);
        }
        
        // Re-enable checks
        await using (var cmd = new MySqlCommand(@"
            SET SESSION unique_checks = 1;
            SET SESSION foreign_key_checks = 1;", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        
        Console.WriteLine($"\n[WorldCatLoader] ========== COMPLETE ==========");
        Console.WriteLine($"[WorldCatLoader] Processed: {rowsProcessed:N0}");
        Console.WriteLine($"[WorldCatLoader] Inserted: {rowsInserted:N0}");
        Console.WriteLine($"[WorldCatLoader] Skipped: {rowsSkipped:N0}");
        Console.WriteLine($"[WorldCatLoader] Malformed: {malformedRows:N0}");
        Console.WriteLine($"[WorldCatLoader] Time: {sw.Elapsed}");
        Console.WriteLine($"[WorldCatLoader] Avg Rate: {rowsProcessed / sw.Elapsed.TotalSeconds:N0} rows/sec");
        
        return (rowsProcessed, rowsInserted, rowsSkipped);
    }
    
    /// <summary>
    /// Parse a CSV line handling quoted fields and escaped quotes.
    /// Falls back to simple split if quoted parsing fails.
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        // First try: quoted CSV parsing
        var fields = ParseCsvQuoted(line);
        
        // If we got exactly 5, we're good
        if (fields.Length == EXPECTED_COLUMNS)
        {
            return fields;
        }
        
        // Fallback: simple comma split (works for most WorldCat rows since they don't have embedded commas)
        // Strip quotes from each field
        var simpleSplit = line.Split(',');
        if (simpleSplit.Length == EXPECTED_COLUMNS)
        {
            for (int i = 0; i < simpleSplit.Length; i++)
            {
                var f = simpleSplit[i];
                if (f.StartsWith('"') && f.EndsWith('"') && f.Length >= 2)
                {
                    simpleSplit[i] = f.Substring(1, f.Length - 2);
                }
            }
            return simpleSplit;
        }
        
        // Last resort for corrupted rows: try to extract the 5 fields we need
        // Format: "aacid","primary_id",\N or md5,byte_offset,byte_length
        // Find first and second quoted fields, then parse the rest
        if (simpleSplit.Length > EXPECTED_COLUMNS)
        {
            // Take first two (aacid, primary_id), skip middle garbage, take last two numbers
            var recovered = new string[EXPECTED_COLUMNS];
            
            // Field 0: aacid (first quoted field)
            recovered[0] = StripQuotes(simpleSplit[0]);
            
            // Field 1: primary_id (second quoted field) 
            recovered[1] = StripQuotes(simpleSplit[1]);
            
            // Field 2: md5 - should be \N or a 32-char hex
            recovered[2] = simpleSplit[2];
            
            // Fields 3,4: byte_offset, byte_length - take last two numeric fields
            recovered[3] = simpleSplit[simpleSplit.Length - 2];
            recovered[4] = simpleSplit[simpleSplit.Length - 1];
            
            // Validate: last two should be numeric
            if (long.TryParse(recovered[3], out _) && long.TryParse(recovered[4], out _))
            {
                return recovered;
            }
        }
        
        // Return original parsed result (will be flagged as malformed)
        return fields;
    }
    
    private static string StripQuotes(string s)
    {
        if (s.StartsWith('"') && s.EndsWith('"') && s.Length >= 2)
            return s.Substring(1, s.Length - 2);
        if (s.StartsWith('"'))
            return s.Substring(1);
        if (s.EndsWith('"'))
            return s.Substring(0, s.Length - 1);
        return s;
    }
    
    private static string[] ParseCsvQuoted(string line)
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
            Console.WriteLine($"[WorldCatLoader] Batch error: {ex.Message}");
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
    
    private static void AppendEscapedOrNull(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value) || value == "\\N")
        {
            sb.Append("NULL");
            return;
        }
        AppendEscaped(sb, value);
    }
    
    private static void AppendMd5OrNull(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value) || value == "\\N")
        {
            sb.Append("NULL");
            return;
        }
        // MD5 is exactly 32 hex chars
        if (value.Length == 32)
        {
            sb.Append('\'');
            sb.Append(value);
            sb.Append('\'');
        }
        else
        {
            sb.Append("NULL");
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
}
