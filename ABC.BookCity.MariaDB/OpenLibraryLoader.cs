using System.Diagnostics;
using System.Text;
using MySqlConnector;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// High-performance loader for OpenLibrary dump files.
/// Loads the ol_dump_latest.txt format (5 tab-separated columns, no header).
/// Format: type, ol_key, revision, last_modified, json
/// </summary>
public class OpenLibraryLoader
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
    
    public OpenLibraryLoader(string connectionString, string filePath, int batchSize = 5000)
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
        var insertBuilder = new StringBuilder(2 * 1024 * 1024); // 2MB initial (JSON can be large)
        var batchCount = 0;
        
        const string insertPrefix = @"INSERT IGNORE INTO allthethings.ol_base 
            (type, ol_key, revision, last_modified, json) VALUES ";
        
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
        
        Console.WriteLine($"[OpenLibraryLoader] Loading from: {_filePath}");
        Console.WriteLine($"[OpenLibraryLoader] Skip rows: {skipRows:N0}, Batch size: {_batchSize:N0}");
        
        using var reader = new StreamReader(_filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 1024);
        
        // Skip rows if needed
        if (skipRows > 0)
        {
            Console.WriteLine($"[OpenLibraryLoader] Skipping {skipRows:N0} rows...");
            var skipSw = Stopwatch.StartNew();
            for (long i = 0; i < skipRows; i++)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                
                if (i > 0 && i % 1_000_000 == 0)
                {
                    Console.WriteLine($"[OpenLibraryLoader] Skipped {i:N0} rows ({skipSw.Elapsed.TotalSeconds:F1}s)");
                }
            }
            Console.WriteLine($"[OpenLibraryLoader] Skip complete in {skipSw.Elapsed.TotalSeconds:F1}s");
        }
        
        insertBuilder.Append(insertPrefix);
        
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break;
            if (string.IsNullOrEmpty(line)) continue;
            
            rowsProcessed++;
            
            // Parse TSV line - find tab positions
            var columns = line.Split('\t');
            if (columns.Length < EXPECTED_COLUMNS)
            {
                malformedRows++;
                if (malformedRows <= 10)
                {
                    Console.WriteLine($"[OpenLibraryLoader] Malformed row {rowsProcessed}: expected {EXPECTED_COLUMNS} columns, got {columns.Length}");
                }
                rowsSkipped++;
                continue;
            }
            
            // Validate required fields
            if (string.IsNullOrEmpty(columns[COL_OL_KEY]))
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
            AppendEscaped(insertBuilder, columns[COL_TYPE], 40); insertBuilder.Append(',');
            AppendEscaped(insertBuilder, columns[COL_OL_KEY], 250); insertBuilder.Append(',');
            AppendInt(insertBuilder, columns[COL_REVISION]); insertBuilder.Append(',');
            AppendDateTime(insertBuilder, columns[COL_LAST_MODIFIED]); insertBuilder.Append(',');
            AppendEscaped(insertBuilder, columns[COL_JSON]); // JSON can be very long
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
                if (rowsProcessed % 100_000 == 0)
                {
                    var elapsed = sw.Elapsed.TotalSeconds;
                    var rate = rowsProcessed / elapsed;
                    Console.WriteLine($"[OpenLibraryLoader] Processed: {rowsProcessed:N0} | Inserted: {rowsInserted:N0} | " +
                                      $"Rate: {rate:N0}/s | Elapsed: {elapsed:F1}s");
                }
            }
        }
        
        // Execute final batch
        if (batchCount > 0)
        {
            var inserted = await ExecuteBatchAsync(connection, insertBuilder.ToString(), cancellationToken);
            rowsInserted += inserted;
        }
        
        // Re-enable checks
        await using (var cmd = new MySqlCommand(@"
            SET SESSION unique_checks = 1;
            SET SESSION foreign_key_checks = 1;", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        
        Console.WriteLine($"[OpenLibraryLoader] Complete!");
        Console.WriteLine($"[OpenLibraryLoader] Processed: {rowsProcessed:N0}");
        Console.WriteLine($"[OpenLibraryLoader] Inserted: {rowsInserted:N0}");
        Console.WriteLine($"[OpenLibraryLoader] Skipped: {rowsSkipped:N0}");
        Console.WriteLine($"[OpenLibraryLoader] Malformed: {malformedRows:N0}");
        Console.WriteLine($"[OpenLibraryLoader] Time: {sw.Elapsed}");
        Console.WriteLine($"[OpenLibraryLoader] Rate: {rowsProcessed / sw.Elapsed.TotalSeconds:N0} rows/sec");
        
        return (rowsProcessed, rowsInserted, rowsSkipped);
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
            Console.WriteLine($"[OpenLibraryLoader] Batch error: {ex.Message}");
            throw;
        }
    }
    
    private static void AppendEscaped(StringBuilder sb, string value, int? maxLength = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            sb.Append("''");
            return;
        }
        
        var val = maxLength.HasValue && value.Length > maxLength.Value 
            ? value[..maxLength.Value] 
            : value;
            
        sb.Append('\'');
        foreach (char c in val)
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
    
    private static void AppendInt(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value) || !int.TryParse(value, out var i))
        {
            sb.Append('0');
            return;
        }
        sb.Append(i);
    }
    
    private static void AppendDateTime(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            sb.Append("'1970-01-01 00:00:00'");
            return;
        }
        
        // Format: "2021-12-26T21:22:34.199846"
        // Need to convert to MySQL format: "2021-12-26 21:22:34"
        if (DateTime.TryParse(value, out var dt))
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
