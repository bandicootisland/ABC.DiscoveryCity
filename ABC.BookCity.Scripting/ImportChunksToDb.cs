#:package MySqlConnector@2.3.7

// Import HathiTrust TSV chunks into MariaDB
// Copy this script to the folder containing ./chunks and run: dotnet run
// Uses file renaming as a queue system - can run multiple instances in parallel
// Files: chunk.txt -> .processing.txt -> .done.txt (keeps .txt for Excel)

using MySqlConnector;
using System.Text.Json;
using System.Text.Json.Serialization;

// Configuration
string chunksFolder = Path.Combine(Directory.GetCurrentDirectory(), "chunks");
string connectionString = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
string progressFile = Path.Combine(Directory.GetCurrentDirectory(), $"import_progress_{Environment.ProcessId}.json");
int batchSize = 1000; // Rows per INSERT statement
int expectedColumnCount = 26; // HathiTrust catalog has 26 columns
string instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";

Console.WriteLine($"╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine($"║         HathiTrust Catalog Import                            ║");
Console.WriteLine($"╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"Instance: {instanceId}");
Console.WriteLine($"Working directory: {Directory.GetCurrentDirectory()}");
Console.WriteLine($"Chunks folder: {chunksFolder}");
Console.WriteLine();

await ImportChunksAsync(chunksFolder, connectionString, progressFile, batchSize, expectedColumnCount, instanceId, Console.WriteLine);

async Task ImportChunksAsync(string chunksFolder, string connStr, string progressFile, int batchSize, int expectedColumns, string instanceId, Action<string> log)
{
    var progress = LoadProgress(progressFile);
    progress.LastStartTime = DateTime.Now;
    progress.InstanceId = instanceId;
    SaveProgress(progressFile, progress);

    using var connection = new MySqlConnection(connStr);
    await connection.OpenAsync();

    // Check if we should disable indexes (only if table is empty or mostly empty)
    var currentCount = await GetTableCountAsync(connection);
    bool indexesDisabled = false;
    
    if (currentCount < 100000)
    {
        log("Disabling indexes for faster import (table has < 100k rows)...");
        await ExecuteNonQueryAsync(connection, "ALTER TABLE hathi_catalog DISABLE KEYS;");
        indexesDisabled = true;
    }

    try
    {
        while (true)
        {
            // Get next available file (re-read directory each time for parallel support)
            var nextFile = GetNextAvailableFile(chunksFolder);
            
            if (nextFile == null)
            {
                // Check if any files are still processing
                var processingFiles = Directory.GetFiles(chunksFolder, "*.processing.txt");
                if (processingFiles.Any())
                {
                    log($"No pending files. {processingFiles.Length} file(s) still processing by other instances.");
                    log("Waiting 10 seconds before checking again...");
                    await Task.Delay(10000);
                    continue;
                }
                
                log("No more files to process. All done!");
                break;
            }

            var originalName = Path.GetFileNameWithoutExtension(nextFile).Replace(".processing", "") + ".txt";
            var fileStats = GetFileStats(chunksFolder);
            
            log($"Processing {originalName} ({fileStats.done + 1}/{fileStats.total})...");

            var fileStartTime = DateTime.Now;
            bool success = false;
            long imported = 0;
            long skipped = 0;
            
            try
            {
                (imported, skipped) = await ImportFileAsync(connection, nextFile, batchSize, expectedColumns, log);
                success = true;
            }
            catch (Exception ex)
            {
                log($"  FATAL ERROR: {ex.Message}");
                log("  Renaming file back to pending and stopping...");
                
                // Rename back to pending (remove .processing)
                var dir = Path.GetDirectoryName(nextFile)!;
                var name = Path.GetFileNameWithoutExtension(nextFile).Replace(".processing", "");
                var pendingFile = Path.Combine(dir, name + ".txt");
                try { File.Move(nextFile, pendingFile); } catch { }
                
                throw; // Re-throw to exit the loop
            }

            // Only mark file as done if successful
            var doneDir = Path.GetDirectoryName(nextFile)!;
            var doneName = Path.GetFileNameWithoutExtension(nextFile).Replace(".processing", "");
            var doneFile = Path.Combine(doneDir, doneName + ".done.txt");
            File.Move(nextFile, doneFile);

            // Update progress
            progress.FilesProcessed++;
            progress.TotalRowsImported += imported;
            progress.TotalRowsSkipped += skipped;
            progress.LastFileProcessed = originalName;
            progress.LastFileTime = DateTime.Now;
            progress.LastFileDuration = DateTime.Now - fileStartTime;
            SaveProgress(progressFile, progress);

            // Progress update
            var elapsed = DateTime.Now - progress.LastStartTime!.Value;
            var stats = GetFileStats(chunksFolder);
            var avgSecondsPerFile = elapsed.TotalSeconds / progress.FilesProcessed;
            var etaSeconds = stats.pending * avgSecondsPerFile;
            var eta = TimeSpan.FromSeconds(etaSeconds);

            log($"  Imported: {imported:N0}, Skipped: {skipped:N0}, Duration: {progress.LastFileDuration:mm\\:ss}");
            log($"  Progress: {stats.done}/{stats.total} done, {stats.processing} processing, {stats.pending} pending - ETA: {eta:hh\\:mm\\:ss}");
        }
    }
    finally
    {
        if (indexesDisabled)
        {
            log("");
            log("Re-enabling indexes (this may take a while)...");
            await ExecuteNonQueryAsync(connection, "ALTER TABLE hathi_catalog ENABLE KEYS;");
        }
    }

    var totalElapsed = DateTime.Now - progress.LastStartTime;
    log("");
    log("=== Import Complete ===");
    log($"Instance: {instanceId}");
    log($"Files processed this run: {progress.FilesProcessed}");
    log($"Rows imported this run: {progress.TotalRowsImported:N0}");
    log($"Rows skipped this run: {progress.TotalRowsSkipped:N0}");
    log($"Time elapsed: {totalElapsed:hh\\:mm\\:ss}");

    // Get final count
    var count = await GetTableCountAsync(connection);
    log($"Total records in table: {count:N0}");
    
    progress.FinalTableCount = count;
    progress.CompletedTime = DateTime.Now;
    SaveProgress(progressFile, progress);
}

string? GetNextAvailableFile(string folder)
{
    // Get all .txt files that are not .processing.txt or .done.txt
    var pendingFiles = Directory.GetFiles(folder, "*.txt")
        .Where(f => !f.EndsWith(".processing.txt") && !f.EndsWith(".done.txt"))
        .OrderBy(f => f)
        .ToList();

    foreach (var file in pendingFiles)
    {
        // chunk_0001.txt -> chunk_0001.processing.txt (only change extension, not folder name!)
        var dir = Path.GetDirectoryName(file)!;
        var name = Path.GetFileNameWithoutExtension(file);
        var processingFile = Path.Combine(dir, name + ".processing.txt");
        
        try
        {
            // Atomically rename to .processing.txt
            File.Move(file, processingFile);
            return processingFile;
        }
        catch (IOException ex)
        {
            // Another instance grabbed this file, or permission issue - try next
            Console.WriteLine($"  Could not acquire {Path.GetFileName(file)}: {ex.Message}");
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Unexpected error on {Path.GetFileName(file)}: {ex.Message}");
            continue;
        }
    }

    return null;
}

(int total, int done, int processing, int pending) GetFileStats(string folder)
{
    var allFiles = Directory.GetFiles(folder, "*.txt");
    int done = allFiles.Count(f => f.EndsWith(".done.txt"));
    int processing = allFiles.Count(f => f.EndsWith(".processing.txt"));
    int pending = allFiles.Count(f => !f.EndsWith(".processing.txt") && !f.EndsWith(".done.txt"));
    int total = done + processing + pending;
    return (total, done, processing, pending);
}

async Task<long> GetTableCountAsync(MySqlConnection connection)
{
    using var cmd = new MySqlCommand("SELECT COUNT(*) FROM hathi_catalog", connection);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt64(result);
}

async Task<(long imported, long skipped)> ImportFileAsync(MySqlConnection connection, string filePath, int batchSize, int expectedColumns, Action<string> log)
{
    long imported = 0;
    long skipped = 0;
    var batch = new List<string[]>();

    using var reader = new StreamReader(filePath);
    string? line;
    int lineNumber = 0;

    while ((line = await reader.ReadLineAsync()) != null)
    {
        lineNumber++;
        var columns = line.Split('\t');

        // Validate column count - skip bad rows
        if (columns.Length != expectedColumns)
        {
            skipped++;
            continue;
        }

        // Clean and validate the data
        var cleanedColumns = CleanRow(columns);
        if (cleanedColumns == null)
        {
            skipped++;
            continue;
        }

        batch.Add(cleanedColumns);

        if (batch.Count >= batchSize)
        {
            imported += await InsertBatchAsync(connection, batch);
            batch.Clear();
        }
    }

    // Insert remaining rows
    if (batch.Any())
    {
        imported += await InsertBatchAsync(connection, batch);
    }

    return (imported, skipped);
}

string[]? CleanRow(string[] columns)
{
    // Column indices (0-based):
    // 0: htid, 1: access, 2: rights, 3: ht_bib_key (BIGINT), 4: description
    // 5: source, 6: source_bib_num, 7: oclc_num, 8: isbn, 9: issn
    // 10: lccn, 11: title, 12: imprint, 13: rights_reason_code, 14: rights_timestamp (DATETIME)
    // 15: us_gov_doc_flag (TINYINT), 16: rights_date_used, 17: pub_place, 18: lang, 19: bib_fmt
    // 20: collection_code, 21: content_provider_code, 22: responsible_entity_code
    // 23: digitization_agent_code, 24: access_profile_code, 25: author

    // htid is required (primary key)
    if (string.IsNullOrWhiteSpace(columns[0]))
        return null;

    // Clean integer fields - extract only digits
    columns[3] = ExtractLong(columns[3]);           // ht_bib_key
    columns[15] = ExtractTinyInt(columns[15]);      // us_gov_doc_flag

    // Clean datetime field
    columns[14] = CleanDateTime(columns[14]);       // rights_timestamp

    // Escape all string fields for MySQL
    for (int i = 0; i < columns.Length; i++)
    {
        columns[i] = columns[i].Replace("\\", "\\\\").Replace("'", "\\'");
    }

    return columns;
}

string ExtractLong(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        return "";
    
    // Extract only digits
    var digits = new string(value.Where(char.IsDigit).ToArray());
    
    // Validate it's a valid long
    if (string.IsNullOrEmpty(digits))
        return "";
    
    if (long.TryParse(digits, out _))
        return digits;
    
    return "";
}

string ExtractTinyInt(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        return "";
    
    // Extract only digits
    var digits = new string(value.Where(char.IsDigit).ToArray());
    
    if (string.IsNullOrEmpty(digits))
        return "";
    
    // Must be 0 or 1 for boolean flag
    if (digits == "0" || digits == "1")
        return digits;
    
    // If it's any other number, treat non-zero as 1
    if (int.TryParse(digits, out int val))
        return val == 0 ? "0" : "1";
    
    return "";
}

string CleanDateTime(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        return "";
    
    // Try to parse as datetime
    if (DateTime.TryParse(value, out DateTime dt))
        return dt.ToString("yyyy-MM-dd HH:mm:ss");
    
    // Try common formats
    string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy" };
    foreach (var fmt in formats)
    {
        if (DateTime.TryParseExact(value, fmt, null, System.Globalization.DateTimeStyles.None, out dt))
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
    }
    
    return "";
}

async Task<int> InsertBatchAsync(MySqlConnection connection, List<string[]> batch)
{
    if (!batch.Any()) return 0;

    var sb = new System.Text.StringBuilder();
    sb.AppendLine(@"INSERT IGNORE INTO hathi_catalog (
        htid, access, rights, ht_bib_key, description, source, source_bib_num,
        oclc_num, isbn, issn, lccn, title, imprint, rights_reason_code,
        rights_timestamp, us_gov_doc_flag, rights_date_used, pub_place, lang,
        bib_fmt, collection_code, content_provider_code, responsible_entity_code,
        digitization_agent_code, access_profile_code, author
    ) VALUES ");

    var values = new List<string>();
    foreach (var row in batch)
    {
        var vals = new List<string>();
        for (int i = 0; i < row.Length; i++)
        {
            if (string.IsNullOrEmpty(row[i]))
            {
                vals.Add("NULL");
            }
            else if (i == 3 || i == 15) // ht_bib_key, us_gov_doc_flag (integers)
            {
                vals.Add(row[i]);
            }
            else if (i == 14) // rights_timestamp (datetime)
            {
                vals.Add($"'{row[i]}'");
            }
            else
            {
                vals.Add($"'{row[i]}'");
            }
        }
        values.Add($"({string.Join(",", vals)})");
    }

    sb.Append(string.Join(",\n", values));
    sb.Append(";");

    using var cmd = new MySqlCommand(sb.ToString(), connection);
    cmd.CommandTimeout = 300;
    
    try
    {
        return await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception ex) when (ex.Message.Contains("Connection") || ex.Message.Contains("Closed") || ex.Message.Contains("socket"))
    {
        // Fatal connection error - rethrow to stop processing
        Console.WriteLine($"  FATAL: Connection error: {ex.Message}");
        throw;
    }
    catch (Exception ex)
    {
        // Non-fatal error - log and continue
        Console.WriteLine($"  Warning: Batch insert error: {ex.Message}");
        return 0;
    }
}

async Task ExecuteNonQueryAsync(MySqlConnection connection, string sql)
{
    using var cmd = new MySqlCommand(sql, connection);
    cmd.CommandTimeout = 3600; // 1 hour for index operations
    await cmd.ExecuteNonQueryAsync();
}

// Progress tracking
ImportProgress LoadProgress(string path)
{
    try
    {
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ImportProgressContext.Default.ImportProgress) ?? new ImportProgress();
        }
    }
    catch { }
    return new ImportProgress();
}

void SaveProgress(string path, ImportProgress progress)
{
    try
    {
        var json = JsonSerializer.Serialize(progress, ImportProgressContext.Default.ImportProgress);
        File.WriteAllText(path, json);
    }
    catch { }
}

class ImportProgress
{
    public string? InstanceId { get; set; }
    public DateTime? LastStartTime { get; set; }
    public DateTime? CompletedTime { get; set; }
    public int FilesProcessed { get; set; }
    public long TotalRowsImported { get; set; }
    public long TotalRowsSkipped { get; set; }
    public string? LastFileProcessed { get; set; }
    public DateTime? LastFileTime { get; set; }
    public TimeSpan? LastFileDuration { get; set; }
    public long FinalTableCount { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ImportProgress))]
partial class ImportProgressContext : JsonSerializerContext { }
