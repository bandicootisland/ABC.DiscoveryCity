#:package MySqlConnector@2.3.7
#:package System.Text.Json@8.0.0

// Extract structured data from ol_base JSON into normalized tables
// Populates: ol_authors, ol_works, ol_editions, ol_work_authors, ol_edition_authors
// Run: dotnet run

using MySqlConnector;
using System.Text.Json;
using System.Text;
using System.Diagnostics;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    OpenLibrary JSON Extractor                                ║");
Console.WriteLine("║    Extract data from ol_base to normalized tables           ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var connectionString = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
var batchSize = 2000;

// Menu
while (true)
{
    Console.WriteLine("\n=== Extraction Menu ===");
    Console.WriteLine("1. Extract Authors (/type/author → ol_authors)");
    Console.WriteLine("2. Extract Works (/type/work → ol_works + ol_work_authors)");
    Console.WriteLine("3. Extract Editions (/type/edition → ol_editions + ol_edition_authors)");
    Console.WriteLine("4. Extract All (sequential)");
    Console.WriteLine("5. Show Statistics");
    Console.WriteLine("Q. Quit");
    
    Console.Write("\nChoice: ");
    var choice = Console.ReadLine()?.Trim().ToLower();
    
    if (choice == "q") break;
    
    switch (choice)
    {
        case "1":
            await ExtractAuthorsAsync();
            break;
        case "2":
            await ExtractWorksAsync();
            break;
        case "3":
            await ExtractEditionsAsync();
            break;
        case "4":
            await ExtractAuthorsAsync();
            await ExtractWorksAsync();
            await ExtractEditionsAsync();
            break;
        case "5":
            await ShowStatsAsync();
            break;
        default:
            Console.WriteLine("Invalid choice.");
            break;
    }
}

Console.WriteLine("\nDone!");

// =============================================================================
// Extract Authors
// =============================================================================
async Task ExtractAuthorsAsync()
{
    Console.WriteLine("\n=== Extracting Authors ===");
    var sw = Stopwatch.StartNew();
    
    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();
    
    // Get count
    long total;
    await using (var countCmd = new MySqlCommand(
        "SELECT COUNT(*) FROM ol_base WHERE type = '/type/author'", conn))
    {
        total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
    }
    Console.WriteLine($"Total author records in ol_base: {total:N0}");
    
    // Check existing
    long existing;
    await using (var existCmd = new MySqlCommand("SELECT COUNT(*) FROM ol_authors", conn))
    {
        existing = Convert.ToInt64(await existCmd.ExecuteScalarAsync());
    }
    Console.WriteLine($"Existing in ol_authors: {existing:N0}");
    
    Console.Write("Skip existing keys? (y/n): ");
    var skipExisting = Console.ReadLine()?.ToLower() == "y";
    
    // Optimize session
    await using (var optCmd = new MySqlCommand(@"
        SET SESSION unique_checks = 0;
        SET SESSION foreign_key_checks = 0;", conn))
    {
        await optCmd.ExecuteNonQueryAsync();
    }
    
    long processed = 0;
    long inserted = 0;
    long errors = 0;
    string lastKey = "";
    
    var insertSql = @"INSERT INTO ol_authors 
        (ol_key, name, personal_name, alternate_names, birth_date, death_date, 
         bio, wikipedia, remote_ids, source_records, photos, links, 
         revision, created_at, last_modified, json)
        VALUES (@ol_key, @name, @personal_name, @alternate_names, @birth_date, @death_date,
                @bio, @wikipedia, @remote_ids, @source_records, @photos, @links,
                @revision, @created_at, @last_modified, @json)
        ON DUPLICATE KEY UPDATE 
            name = VALUES(name), revision = VALUES(revision), last_modified = VALUES(last_modified)";
    
    while (true)
    {
        var sql = skipExisting
            ? @"SELECT ol_key, json FROM ol_base 
                WHERE type = '/type/author' AND ol_key > @lastKey 
                AND ol_key NOT IN (SELECT ol_key FROM ol_authors)
                ORDER BY ol_key LIMIT @limit"
            : @"SELECT ol_key, json FROM ol_base 
                WHERE type = '/type/author' AND ol_key > @lastKey 
                ORDER BY ol_key LIMIT @limit";
        
        await using var selectCmd = new MySqlCommand(sql, conn);
        selectCmd.Parameters.AddWithValue("@lastKey", lastKey);
        selectCmd.Parameters.AddWithValue("@limit", batchSize);
        selectCmd.CommandTimeout = 120;
        
        var records = new List<(string key, string json)>();
        await using (var reader = await selectCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                records.Add((reader.GetString(0), reader.GetString(1)));
                lastKey = reader.GetString(0);
            }
        }
        
        if (records.Count == 0) break;
        
        foreach (var (key, jsonStr) in records)
        {
            processed++;
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                
                await using var insertCmd = new MySqlCommand(insertSql, conn);
                insertCmd.Parameters.AddWithValue("@ol_key", key);
                insertCmd.Parameters.AddWithValue("@name", GetJsonString(root, "name", 500));
                insertCmd.Parameters.AddWithValue("@personal_name", GetJsonString(root, "personal_name", 500));
                insertCmd.Parameters.AddWithValue("@alternate_names", GetJsonArray(root, "alternate_names"));
                insertCmd.Parameters.AddWithValue("@birth_date", GetJsonString(root, "birth_date", 100));
                insertCmd.Parameters.AddWithValue("@death_date", GetJsonString(root, "death_date", 100));
                insertCmd.Parameters.AddWithValue("@bio", GetJsonTextOrValue(root, "bio"));
                insertCmd.Parameters.AddWithValue("@wikipedia", GetJsonString(root, "wikipedia", 500));
                insertCmd.Parameters.AddWithValue("@remote_ids", GetJsonObject(root, "remote_ids"));
                insertCmd.Parameters.AddWithValue("@source_records", GetJsonArray(root, "source_records"));
                insertCmd.Parameters.AddWithValue("@photos", GetJsonArray(root, "photos"));
                insertCmd.Parameters.AddWithValue("@links", GetJsonArray(root, "links"));
                insertCmd.Parameters.AddWithValue("@revision", GetJsonInt(root, "revision", 1));
                insertCmd.Parameters.AddWithValue("@created_at", GetJsonDateTime(root, "created"));
                insertCmd.Parameters.AddWithValue("@last_modified", GetJsonDateTime(root, "last_modified"));
                insertCmd.Parameters.AddWithValue("@json", jsonStr.Length > 65000 ? jsonStr[..65000] : jsonStr);
                
                await insertCmd.ExecuteNonQueryAsync();
                inserted++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5)
                {
                    Console.WriteLine($"\nError on {key}: {ex.Message}");
                }
            }
        }
        
        PrintProgress(processed, total, sw.Elapsed, inserted, errors);
    }
    
    // Re-enable checks
    await using (var resetCmd = new MySqlCommand(@"
        SET SESSION unique_checks = 1;
        SET SESSION foreign_key_checks = 1;", conn))
    {
        await resetCmd.ExecuteNonQueryAsync();
    }
    
    Console.WriteLine($"\n\nAuthors extraction complete!");
    Console.WriteLine($"Processed: {processed:N0} | Inserted: {inserted:N0} | Errors: {errors:N0}");
    Console.WriteLine($"Time: {sw.Elapsed}");
}

// =============================================================================
// Extract Works
// =============================================================================
async Task ExtractWorksAsync()
{
    Console.WriteLine("\n=== Extracting Works ===");
    var sw = Stopwatch.StartNew();
    
    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();
    
    long total;
    await using (var countCmd = new MySqlCommand(
        "SELECT COUNT(*) FROM ol_base WHERE type = '/type/work'", conn))
    {
        total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
    }
    Console.WriteLine($"Total work records in ol_base: {total:N0}");
    
    long existing;
    await using (var existCmd = new MySqlCommand("SELECT COUNT(*) FROM ol_works", conn))
    {
        existing = Convert.ToInt64(await existCmd.ExecuteScalarAsync());
    }
    Console.WriteLine($"Existing in ol_works: {existing:N0}");
    
    Console.Write("Skip existing keys? (y/n): ");
    var skipExisting = Console.ReadLine()?.ToLower() == "y";
    
    await using (var optCmd = new MySqlCommand(@"
        SET SESSION unique_checks = 0;
        SET SESSION foreign_key_checks = 0;", conn))
    {
        await optCmd.ExecuteNonQueryAsync();
    }
    
    long processed = 0;
    long inserted = 0;
    long authorLinks = 0;
    long errors = 0;
    string lastKey = "";
    
    var insertWorkSql = @"INSERT INTO ol_works 
        (ol_key, title, subtitle, subjects, subject_places, subject_times, subject_people,
         description, first_sentence, covers, links, dewey_number, lc_classifications,
         first_publish_date, revision, created_at, last_modified, json)
        VALUES (@ol_key, @title, @subtitle, @subjects, @subject_places, @subject_times, @subject_people,
                @description, @first_sentence, @covers, @links, @dewey_number, @lc_classifications,
                @first_publish_date, @revision, @created_at, @last_modified, @json)
        ON DUPLICATE KEY UPDATE 
            title = VALUES(title), revision = VALUES(revision), last_modified = VALUES(last_modified)";
    
    var insertAuthorSql = @"INSERT IGNORE INTO ol_work_authors (work_key, author_key, author_role) 
                            VALUES (@work_key, @author_key, @author_role)";
    
    while (true)
    {
        var sql = skipExisting
            ? @"SELECT ol_key, json FROM ol_base 
                WHERE type = '/type/work' AND ol_key > @lastKey 
                AND ol_key NOT IN (SELECT ol_key FROM ol_works)
                ORDER BY ol_key LIMIT @limit"
            : @"SELECT ol_key, json FROM ol_base 
                WHERE type = '/type/work' AND ol_key > @lastKey 
                ORDER BY ol_key LIMIT @limit";
        
        await using var selectCmd = new MySqlCommand(sql, conn);
        selectCmd.Parameters.AddWithValue("@lastKey", lastKey);
        selectCmd.Parameters.AddWithValue("@limit", batchSize);
        selectCmd.CommandTimeout = 120;
        
        var records = new List<(string key, string json)>();
        await using (var reader = await selectCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                records.Add((reader.GetString(0), reader.GetString(1)));
                lastKey = reader.GetString(0);
            }
        }
        
        if (records.Count == 0) break;
        
        foreach (var (key, jsonStr) in records)
        {
            processed++;
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                
                await using var insertCmd = new MySqlCommand(insertWorkSql, conn);
                insertCmd.Parameters.AddWithValue("@ol_key", key);
                insertCmd.Parameters.AddWithValue("@title", GetJsonString(root, "title", 1000));
                insertCmd.Parameters.AddWithValue("@subtitle", GetJsonString(root, "subtitle", 500));
                insertCmd.Parameters.AddWithValue("@subjects", GetJsonArray(root, "subjects"));
                insertCmd.Parameters.AddWithValue("@subject_places", GetJsonArray(root, "subject_places"));
                insertCmd.Parameters.AddWithValue("@subject_times", GetJsonArray(root, "subject_times"));
                insertCmd.Parameters.AddWithValue("@subject_people", GetJsonArray(root, "subject_people"));
                insertCmd.Parameters.AddWithValue("@description", GetJsonTextOrValue(root, "description"));
                insertCmd.Parameters.AddWithValue("@first_sentence", GetJsonTextOrValue(root, "first_sentence"));
                insertCmd.Parameters.AddWithValue("@covers", GetJsonArray(root, "covers"));
                insertCmd.Parameters.AddWithValue("@links", GetJsonArray(root, "links"));
                insertCmd.Parameters.AddWithValue("@dewey_number", GetJsonArrayFirst(root, "dewey_number", 100));
                insertCmd.Parameters.AddWithValue("@lc_classifications", GetJsonArray(root, "lc_classifications"));
                insertCmd.Parameters.AddWithValue("@first_publish_date", GetJsonString(root, "first_publish_date", 100));
                insertCmd.Parameters.AddWithValue("@revision", GetJsonInt(root, "revision", 1));
                insertCmd.Parameters.AddWithValue("@created_at", GetJsonDateTime(root, "created"));
                insertCmd.Parameters.AddWithValue("@last_modified", GetJsonDateTime(root, "last_modified"));
                insertCmd.Parameters.AddWithValue("@json", jsonStr.Length > 65000 ? jsonStr[..65000] : jsonStr);
                
                await insertCmd.ExecuteNonQueryAsync();
                inserted++;
                
                // Extract authors
                if (root.TryGetProperty("authors", out var authorsElem) && authorsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var authorElem in authorsElem.EnumerateArray())
                    {
                        string? authorKey = null;
                        string? role = null;
                        
                        // Can be { "author": { "key": "/authors/OL123A" } } or { "key": "/authors/OL123A" }
                        if (authorElem.TryGetProperty("author", out var innerAuthor))
                        {
                            if (innerAuthor.TryGetProperty("key", out var keyElem))
                                authorKey = keyElem.GetString();
                        }
                        else if (authorElem.TryGetProperty("key", out var keyElem))
                        {
                            authorKey = keyElem.GetString();
                        }
                        
                        if (authorElem.TryGetProperty("type", out var typeElem))
                        {
                            if (typeElem.ValueKind == JsonValueKind.String)
                                role = typeElem.GetString();
                            else if (typeElem.TryGetProperty("key", out var roleKey))
                                role = roleKey.GetString()?.Replace("/type/", "");
                        }
                        
                        if (!string.IsNullOrEmpty(authorKey))
                        {
                            await using var linkCmd = new MySqlCommand(insertAuthorSql, conn);
                            linkCmd.Parameters.AddWithValue("@work_key", key);
                            linkCmd.Parameters.AddWithValue("@author_key", authorKey);
                            linkCmd.Parameters.AddWithValue("@author_role", role ?? "author");
                            await linkCmd.ExecuteNonQueryAsync();
                            authorLinks++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5)
                {
                    Console.WriteLine($"\nError on {key}: {ex.Message}");
                }
            }
        }
        
        PrintProgress(processed, total, sw.Elapsed, inserted, errors);
    }
    
    await using (var resetCmd = new MySqlCommand(@"
        SET SESSION unique_checks = 1;
        SET SESSION foreign_key_checks = 1;", conn))
    {
        await resetCmd.ExecuteNonQueryAsync();
    }
    
    Console.WriteLine($"\n\nWorks extraction complete!");
    Console.WriteLine($"Processed: {processed:N0} | Inserted: {inserted:N0} | Author Links: {authorLinks:N0} | Errors: {errors:N0}");
    Console.WriteLine($"Time: {sw.Elapsed}");
}

// =============================================================================
// Extract Editions
// =============================================================================
async Task ExtractEditionsAsync()
{
    Console.WriteLine("\n=== Extracting Editions ===");
    var sw = Stopwatch.StartNew();
    
    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();
    
    long total;
    await using (var countCmd = new MySqlCommand(
        "SELECT COUNT(*) FROM ol_base WHERE type = '/type/edition'", conn))
    {
        total = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
    }
    Console.WriteLine($"Total edition records in ol_base: {total:N0}");
    
    long existing;
    await using (var existCmd = new MySqlCommand("SELECT COUNT(*) FROM ol_editions", conn))
    {
        existing = Convert.ToInt64(await existCmd.ExecuteScalarAsync());
    }
    Console.WriteLine($"Existing in ol_editions: {existing:N0}");
    
    Console.Write("Skip existing keys? (y/n): ");
    var skipExisting = Console.ReadLine()?.ToLower() == "y";
    
    await using (var optCmd = new MySqlCommand(@"
        SET SESSION unique_checks = 0;
        SET SESSION foreign_key_checks = 0;", conn))
    {
        await optCmd.ExecuteNonQueryAsync();
    }
    
    long processed = 0;
    long inserted = 0;
    long authorLinks = 0;
    long errors = 0;
    string lastKey = "";
    
    var insertEditionSql = @"INSERT INTO ol_editions 
        (ol_key, title, subtitle, publishers, publish_date, publish_country, publish_places,
         number_of_pages, pagination, physical_format, physical_dimensions, weight,
         isbn_10, isbn_13, isbn_10_all, isbn_13_all, lccn, oclc_numbers, languages, language_code,
         work_key, covers, edition_name, series, volume_number, copyright_date, by_statement,
         description, notes, table_of_contents, contributions, source_records, identifiers,
         classifications, revision, created_at, last_modified, json)
        VALUES 
        (@ol_key, @title, @subtitle, @publishers, @publish_date, @publish_country, @publish_places,
         @number_of_pages, @pagination, @physical_format, @physical_dimensions, @weight,
         @isbn_10, @isbn_13, @isbn_10_all, @isbn_13_all, @lccn, @oclc_numbers, @languages, @language_code,
         @work_key, @covers, @edition_name, @series, @volume_number, @copyright_date, @by_statement,
         @description, @notes, @table_of_contents, @contributions, @source_records, @identifiers,
         @classifications, @revision, @created_at, @last_modified, @json)
        ON DUPLICATE KEY UPDATE 
            title = VALUES(title), revision = VALUES(revision), last_modified = VALUES(last_modified)";
    
    var insertAuthorSql = @"INSERT IGNORE INTO ol_edition_authors (edition_key, author_key) 
                            VALUES (@edition_key, @author_key)";
    
    while (true)
    {
        var sql = skipExisting
            ? @"SELECT ol_key, json FROM ol_base 
                WHERE type = '/type/edition' AND ol_key > @lastKey 
                AND ol_key NOT IN (SELECT ol_key FROM ol_editions)
                ORDER BY ol_key LIMIT @limit"
            : @"SELECT ol_key, json FROM ol_base 
                WHERE type = '/type/edition' AND ol_key > @lastKey 
                ORDER BY ol_key LIMIT @limit";
        
        await using var selectCmd = new MySqlCommand(sql, conn);
        selectCmd.Parameters.AddWithValue("@lastKey", lastKey);
        selectCmd.Parameters.AddWithValue("@limit", batchSize);
        selectCmd.CommandTimeout = 120;
        
        var records = new List<(string key, string json)>();
        await using (var reader = await selectCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                records.Add((reader.GetString(0), reader.GetString(1)));
                lastKey = reader.GetString(0);
            }
        }
        
        if (records.Count == 0) break;
        
        foreach (var (key, jsonStr) in records)
        {
            processed++;
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;
                
                await using var insertCmd = new MySqlCommand(insertEditionSql, conn);
                insertCmd.Parameters.AddWithValue("@ol_key", key);
                insertCmd.Parameters.AddWithValue("@title", GetJsonString(root, "title", 1000));
                insertCmd.Parameters.AddWithValue("@subtitle", GetJsonString(root, "subtitle", 500));
                insertCmd.Parameters.AddWithValue("@publishers", GetJsonArray(root, "publishers"));
                insertCmd.Parameters.AddWithValue("@publish_date", GetJsonString(root, "publish_date", 100));
                insertCmd.Parameters.AddWithValue("@publish_country", GetJsonString(root, "publish_country", 20));
                insertCmd.Parameters.AddWithValue("@publish_places", GetJsonArray(root, "publish_places"));
                insertCmd.Parameters.AddWithValue("@number_of_pages", GetJsonIntNullable(root, "number_of_pages"));
                insertCmd.Parameters.AddWithValue("@pagination", GetJsonString(root, "pagination", 200));
                insertCmd.Parameters.AddWithValue("@physical_format", GetJsonString(root, "physical_format", 100));
                insertCmd.Parameters.AddWithValue("@physical_dimensions", GetJsonString(root, "physical_dimensions", 100));
                insertCmd.Parameters.AddWithValue("@weight", GetJsonString(root, "weight", 50));
                insertCmd.Parameters.AddWithValue("@isbn_10", GetJsonArrayFirst(root, "isbn_10", 20));
                insertCmd.Parameters.AddWithValue("@isbn_13", GetJsonArrayFirst(root, "isbn_13", 20));
                insertCmd.Parameters.AddWithValue("@isbn_10_all", GetJsonArray(root, "isbn_10"));
                insertCmd.Parameters.AddWithValue("@isbn_13_all", GetJsonArray(root, "isbn_13"));
                insertCmd.Parameters.AddWithValue("@lccn", GetJsonArrayFirst(root, "lccn", 50));
                insertCmd.Parameters.AddWithValue("@oclc_numbers", GetJsonArray(root, "oclc_numbers"));
                insertCmd.Parameters.AddWithValue("@languages", GetJsonLanguages(root));
                insertCmd.Parameters.AddWithValue("@language_code", GetJsonLanguageCode(root));
                insertCmd.Parameters.AddWithValue("@work_key", GetJsonWorkKey(root));
                insertCmd.Parameters.AddWithValue("@covers", GetJsonArray(root, "covers"));
                insertCmd.Parameters.AddWithValue("@edition_name", GetJsonString(root, "edition_name", 200));
                insertCmd.Parameters.AddWithValue("@series", GetJsonArray(root, "series"));
                insertCmd.Parameters.AddWithValue("@volume_number", GetJsonString(root, "volume_number", 50));
                insertCmd.Parameters.AddWithValue("@copyright_date", GetJsonString(root, "copyright_date", 50));
                insertCmd.Parameters.AddWithValue("@by_statement", GetJsonString(root, "by_statement", 1000));
                insertCmd.Parameters.AddWithValue("@description", GetJsonTextOrValue(root, "description"));
                insertCmd.Parameters.AddWithValue("@notes", GetJsonTextOrValue(root, "notes"));
                insertCmd.Parameters.AddWithValue("@table_of_contents", GetJsonArray(root, "table_of_contents"));
                insertCmd.Parameters.AddWithValue("@contributions", GetJsonArray(root, "contributions"));
                insertCmd.Parameters.AddWithValue("@source_records", GetJsonArray(root, "source_records"));
                insertCmd.Parameters.AddWithValue("@identifiers", GetJsonObject(root, "identifiers"));
                insertCmd.Parameters.AddWithValue("@classifications", GetJsonObject(root, "classifications"));
                insertCmd.Parameters.AddWithValue("@revision", GetJsonInt(root, "revision", 1));
                insertCmd.Parameters.AddWithValue("@created_at", GetJsonDateTime(root, "created"));
                insertCmd.Parameters.AddWithValue("@last_modified", GetJsonDateTime(root, "last_modified"));
                insertCmd.Parameters.AddWithValue("@json", jsonStr.Length > 65000 ? jsonStr[..65000] : jsonStr);
                
                await insertCmd.ExecuteNonQueryAsync();
                inserted++;
                
                // Extract authors
                if (root.TryGetProperty("authors", out var authorsElem) && authorsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var authorElem in authorsElem.EnumerateArray())
                    {
                        string? authorKey = null;
                        
                        if (authorElem.TryGetProperty("key", out var keyElem))
                        {
                            authorKey = keyElem.GetString();
                        }
                        
                        if (!string.IsNullOrEmpty(authorKey))
                        {
                            await using var linkCmd = new MySqlCommand(insertAuthorSql, conn);
                            linkCmd.Parameters.AddWithValue("@edition_key", key);
                            linkCmd.Parameters.AddWithValue("@author_key", authorKey);
                            await linkCmd.ExecuteNonQueryAsync();
                            authorLinks++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                errors++;
                if (errors <= 5)
                {
                    Console.WriteLine($"\nError on {key}: {ex.Message}");
                }
            }
        }
        
        PrintProgress(processed, total, sw.Elapsed, inserted, errors);
    }
    
    await using (var resetCmd = new MySqlCommand(@"
        SET SESSION unique_checks = 1;
        SET SESSION foreign_key_checks = 1;", conn))
    {
        await resetCmd.ExecuteNonQueryAsync();
    }
    
    Console.WriteLine($"\n\nEditions extraction complete!");
    Console.WriteLine($"Processed: {processed:N0} | Inserted: {inserted:N0} | Author Links: {authorLinks:N0} | Errors: {errors:N0}");
    Console.WriteLine($"Time: {sw.Elapsed}");
}

// =============================================================================
// Show Statistics
// =============================================================================
async Task ShowStatsAsync()
{
    Console.WriteLine("\n=== OpenLibrary Table Statistics ===");
    
    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();
    
    var tables = new[] { "ol_base", "ol_authors", "ol_works", "ol_editions", "ol_work_authors", "ol_edition_authors" };
    
    foreach (var table in tables)
    {
        try
        {
            await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {table}", conn);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Console.WriteLine($"  {table,-25}: {count,15:N0}");
        }
        catch
        {
            Console.WriteLine($"  {table,-25}: (table not found)");
        }
    }
    
    // Type breakdown in ol_base
    Console.WriteLine("\nol_base type breakdown:");
    await using var typeCmd = new MySqlCommand(@"
        SELECT type, COUNT(*) as cnt 
        FROM ol_base 
        GROUP BY type 
        ORDER BY cnt DESC 
        LIMIT 10", conn);
    await using var reader = await typeCmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"  {reader.GetString(0),-30}: {reader.GetInt64(1),15:N0}");
    }
}

// =============================================================================
// JSON Helper Functions
// =============================================================================
void PrintProgress(long processed, long total, TimeSpan elapsed, long inserted, long errors)
{
    var rate = processed / Math.Max(1, elapsed.TotalSeconds);
    var remaining = total - processed;
    var eta = TimeSpan.FromSeconds(remaining / Math.Max(1, rate));
    var pct = (processed * 100.0 / Math.Max(1, total));
    
    Console.Write($"\r  Processed: {processed:N0} / {total:N0} ({pct:F1}%) | Inserted: {inserted:N0} | Err: {errors} | {rate:F0}/sec | ETA: {eta:hh\\:mm\\:ss}    ");
}

string? GetJsonString(JsonElement root, string property, int maxLength = int.MaxValue)
{
    if (!root.TryGetProperty(property, out var elem)) return null;
    if (elem.ValueKind != JsonValueKind.String) return null;
    var val = elem.GetString();
    if (val == null) return null;
    return val.Length > maxLength ? val[..maxLength] : val;
}

int GetJsonInt(JsonElement root, string property, int defaultValue = 0)
{
    if (!root.TryGetProperty(property, out var elem)) return defaultValue;
    if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var val)) return val;
    return defaultValue;
}

int? GetJsonIntNullable(JsonElement root, string property)
{
    if (!root.TryGetProperty(property, out var elem)) return null;
    if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var val)) return val;
    return null;
}

DateTime? GetJsonDateTime(JsonElement root, string property)
{
    if (!root.TryGetProperty(property, out var elem)) return null;
    
    // Can be { "type": "/type/datetime", "value": "2021-12-26T21:22:34.199846" }
    // Or just a string
    if (elem.ValueKind == JsonValueKind.Object)
    {
        if (elem.TryGetProperty("value", out var valueElem) && valueElem.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(valueElem.GetString(), out var dt)) return dt;
        }
    }
    else if (elem.ValueKind == JsonValueKind.String)
    {
        if (DateTime.TryParse(elem.GetString(), out var dt)) return dt;
    }
    return null;
}

string? GetJsonArray(JsonElement root, string property)
{
    if (!root.TryGetProperty(property, out var elem)) return null;
    if (elem.ValueKind != JsonValueKind.Array) return null;
    return elem.GetRawText();
}

string? GetJsonObject(JsonElement root, string property)
{
    if (!root.TryGetProperty(property, out var elem)) return null;
    if (elem.ValueKind != JsonValueKind.Object) return null;
    return elem.GetRawText();
}

string? GetJsonArrayFirst(JsonElement root, string property, int maxLength = int.MaxValue)
{
    if (!root.TryGetProperty(property, out var elem)) return null;
    if (elem.ValueKind == JsonValueKind.Array && elem.GetArrayLength() > 0)
    {
        var first = elem[0];
        if (first.ValueKind == JsonValueKind.String)
        {
            var val = first.GetString();
            if (val == null) return null;
            return val.Length > maxLength ? val[..maxLength] : val;
        }
    }
    else if (elem.ValueKind == JsonValueKind.String)
    {
        var val = elem.GetString();
        if (val == null) return null;
        return val.Length > maxLength ? val[..maxLength] : val;
    }
    return null;
}

string? GetJsonTextOrValue(JsonElement root, string property)
{
    if (!root.TryGetProperty(property, out var elem)) return null;
    
    // Can be a string or { "type": "/type/text", "value": "..." }
    if (elem.ValueKind == JsonValueKind.String)
    {
        return elem.GetString();
    }
    else if (elem.ValueKind == JsonValueKind.Object)
    {
        if (elem.TryGetProperty("value", out var valueElem) && valueElem.ValueKind == JsonValueKind.String)
        {
            return valueElem.GetString();
        }
    }
    return null;
}

string? GetJsonLanguages(JsonElement root)
{
    if (!root.TryGetProperty("languages", out var elem)) return null;
    if (elem.ValueKind != JsonValueKind.Array) return null;
    return elem.GetRawText();
}

string? GetJsonLanguageCode(JsonElement root)
{
    if (!root.TryGetProperty("languages", out var elem)) return null;
    if (elem.ValueKind != JsonValueKind.Array || elem.GetArrayLength() == 0) return null;
    
    var first = elem[0];
    if (first.TryGetProperty("key", out var keyElem) && keyElem.ValueKind == JsonValueKind.String)
    {
        // Format: "/languages/eng" -> "eng"
        var key = keyElem.GetString();
        if (key != null && key.StartsWith("/languages/"))
        {
            return key["/languages/".Length..];
        }
    }
    return null;
}

string? GetJsonWorkKey(JsonElement root)
{
    if (!root.TryGetProperty("works", out var elem)) return null;
    if (elem.ValueKind != JsonValueKind.Array || elem.GetArrayLength() == 0) return null;
    
    var first = elem[0];
    if (first.TryGetProperty("key", out var keyElem) && keyElem.ValueKind == JsonValueKind.String)
    {
        return keyElem.GetString();
    }
    return null;
}
