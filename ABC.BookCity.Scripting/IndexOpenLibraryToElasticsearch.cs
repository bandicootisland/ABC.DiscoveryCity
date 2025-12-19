#:package MySqlConnector@2.3.7
#:package Elastic.Clients.Elasticsearch@8.11.0

// Index OpenLibrary catalog from MariaDB to Elasticsearch
// Works, Editions, and Authors are indexed separately
// Run: dotnet run

using MySqlConnector;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    OpenLibrary Elasticsearch Indexer                         ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var mariaDbConn = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
var elasticUrl = "http://localhost:9200";
var batchSize = 3000; // Smaller batches due to JSON size

// Setup Elasticsearch client
var settings = new ElasticsearchClientSettings(new Uri(elasticUrl))
    .DisableDirectStreaming()
    .RequestTimeout(TimeSpan.FromMinutes(10));
var elastic = new ElasticsearchClient(settings);

// Check connection
var pingResponse = await elastic.PingAsync();
if (!pingResponse.IsValidResponse)
{
    Console.WriteLine($"ERROR: Cannot connect to Elasticsearch at {elasticUrl}");
    return;
}
Console.WriteLine($"Connected to Elasticsearch at {elasticUrl}");

// Connect to MariaDB
await using var connection = new MySqlConnection(mariaDbConn);
await connection.OpenAsync();
Console.WriteLine("Connected to MariaDB");

// Menu
while (true)
{
    Console.WriteLine("\n=== OpenLibrary Indexer Menu ===");
    Console.WriteLine("1. Index Editions (ol_editions → ol_editions index)");
    Console.WriteLine("2. Index Works (ol_works → ol_works index)");
    Console.WriteLine("3. Index Authors (ol_authors → ol_authors index)");
    Console.WriteLine("4. Index All from ol_base (raw JSON parsing)");
    Console.WriteLine("5. Check Index Statistics");
    Console.WriteLine("Q. Quit");
    Console.Write("\nChoice: ");
    
    var choice = Console.ReadLine()?.Trim().ToLower();
    
    if (choice == "q") break;
    
    switch (choice)
    {
        case "1":
            await IndexEditions();
            break;
        case "2":
            await IndexWorks();
            break;
        case "3":
            await IndexAuthors();
            break;
        case "4":
            await IndexFromOlBase();
            break;
        case "5":
            await ShowStats();
            break;
    }
}

// ============================================================================
// INDEX EDITIONS
// ============================================================================
async Task IndexEditions()
{
    var indexName = "ol_editions";
    Console.WriteLine($"\n=== Indexing Editions to {indexName} ===");
    
    // Get count
    long totalRecords;
    await using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM ol_editions", connection))
    {
        totalRecords = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    
    if (totalRecords == 0)
    {
        Console.WriteLine("No editions found in ol_editions table. Run extraction first.");
        return;
    }
    
    Console.WriteLine($"Total editions in MariaDB: {totalRecords:N0}");
    
    // Check/create index
    if (!await CreateIndexIfNeeded(indexName)) return;
    
    // Index
    await IndexTable(
        "ol_editions",
        indexName,
        @"SELECT ol_key, title, subtitle, publishers, publish_date, publish_country,
                 number_of_pages, physical_format, isbn_10, isbn_13, lccn, 
                 oclc_numbers, language_code, work_key, edition_name, 
                 by_statement, source_records, revision, last_modified
          FROM ol_editions
          WHERE ol_key > @lastKey
          ORDER BY ol_key
          LIMIT @limit",
        reader => new OpenLibraryEdition
        {
            OlKey = reader.GetString(0),
            Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            Subtitle = reader.IsDBNull(2) ? null : reader.GetString(2),
            Publishers = reader.IsDBNull(3) ? null : reader.GetString(3),
            PublishDate = reader.IsDBNull(4) ? null : reader.GetString(4),
            PublishCountry = reader.IsDBNull(5) ? null : reader.GetString(5),
            NumberOfPages = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            PhysicalFormat = reader.IsDBNull(7) ? null : reader.GetString(7),
            Isbn10 = reader.IsDBNull(8) ? null : reader.GetString(8),
            Isbn13 = reader.IsDBNull(9) ? null : reader.GetString(9),
            Lccn = reader.IsDBNull(10) ? null : reader.GetString(10),
            OclcNumbers = reader.IsDBNull(11) ? null : reader.GetString(11),
            LanguageCode = reader.IsDBNull(12) ? null : reader.GetString(12),
            WorkKey = reader.IsDBNull(13) ? null : reader.GetString(13),
            EditionName = reader.IsDBNull(14) ? null : reader.GetString(14),
            ByStatement = reader.IsDBNull(15) ? null : reader.GetString(15),
            SourceRecords = reader.IsDBNull(16) ? null : reader.GetString(16),
            Revision = reader.GetInt32(17),
            LastModified = reader.GetDateTime(18)
        },
        totalRecords
    );
}

// ============================================================================
// INDEX WORKS
// ============================================================================
async Task IndexWorks()
{
    var indexName = "ol_works";
    Console.WriteLine($"\n=== Indexing Works to {indexName} ===");
    
    // Get count
    long totalRecords;
    await using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM ol_works", connection))
    {
        totalRecords = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    
    if (totalRecords == 0)
    {
        Console.WriteLine("No works found in ol_works table. Run extraction first.");
        return;
    }
    
    Console.WriteLine($"Total works in MariaDB: {totalRecords:N0}");
    
    // Check/create index
    if (!await CreateIndexIfNeeded(indexName)) return;
    
    // Index
    await IndexTable(
        "ol_works",
        indexName,
        @"SELECT ol_key, title, subtitle, subjects, subject_places, subject_times,
                 subject_people, description, first_sentence, dewey_number, 
                 lc_classifications, first_publish_date, revision, last_modified
          FROM ol_works
          WHERE ol_key > @lastKey
          ORDER BY ol_key
          LIMIT @limit",
        reader => new OpenLibraryWork
        {
            OlKey = reader.GetString(0),
            Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            Subtitle = reader.IsDBNull(2) ? null : reader.GetString(2),
            Subjects = reader.IsDBNull(3) ? null : reader.GetString(3),
            SubjectPlaces = reader.IsDBNull(4) ? null : reader.GetString(4),
            SubjectTimes = reader.IsDBNull(5) ? null : reader.GetString(5),
            SubjectPeople = reader.IsDBNull(6) ? null : reader.GetString(6),
            Description = reader.IsDBNull(7) ? null : reader.GetString(7),
            FirstSentence = reader.IsDBNull(8) ? null : reader.GetString(8),
            DeweyNumber = reader.IsDBNull(9) ? null : reader.GetString(9),
            LcClassifications = reader.IsDBNull(10) ? null : reader.GetString(10),
            FirstPublishDate = reader.IsDBNull(11) ? null : reader.GetString(11),
            Revision = reader.GetInt32(12),
            LastModified = reader.GetDateTime(13)
        },
        totalRecords
    );
}

// ============================================================================
// INDEX AUTHORS
// ============================================================================
async Task IndexAuthors()
{
    var indexName = "ol_authors";
    Console.WriteLine($"\n=== Indexing Authors to {indexName} ===");
    
    // Get count
    long totalRecords;
    await using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM ol_authors", connection))
    {
        totalRecords = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    
    if (totalRecords == 0)
    {
        Console.WriteLine("No authors found in ol_authors table. Run extraction first.");
        return;
    }
    
    Console.WriteLine($"Total authors in MariaDB: {totalRecords:N0}");
    
    // Check/create index
    if (!await CreateIndexIfNeeded(indexName)) return;
    
    // Index
    await IndexTable(
        "ol_authors",
        indexName,
        @"SELECT ol_key, name, alternate_names, personal_name, birth_date, death_date,
                 bio, wikipedia, remote_ids, source_records, revision, last_modified
          FROM ol_authors
          WHERE ol_key > @lastKey
          ORDER BY ol_key
          LIMIT @limit",
        reader => new OpenLibraryAuthor
        {
            OlKey = reader.GetString(0),
            Name = reader.IsDBNull(1) ? null : reader.GetString(1),
            AlternateNames = reader.IsDBNull(2) ? null : reader.GetString(2),
            PersonalName = reader.IsDBNull(3) ? null : reader.GetString(3),
            BirthDate = reader.IsDBNull(4) ? null : reader.GetString(4),
            DeathDate = reader.IsDBNull(5) ? null : reader.GetString(5),
            Bio = reader.IsDBNull(6) ? null : reader.GetString(6),
            Wikipedia = reader.IsDBNull(7) ? null : reader.GetString(7),
            RemoteIds = reader.IsDBNull(8) ? null : reader.GetString(8),
            SourceRecords = reader.IsDBNull(9) ? null : reader.GetString(9),
            Revision = reader.GetInt32(10),
            LastModified = reader.GetDateTime(11)
        },
        totalRecords
    );
}

// ============================================================================
// INDEX FROM OL_BASE (Raw JSON parsing for when extraction tables aren't ready)
// ============================================================================
async Task IndexFromOlBase()
{
    Console.WriteLine("\n=== Indexing directly from ol_base ===");
    Console.WriteLine("This parses JSON on the fly - slower but works without extraction.");
    
    // Skip the slow GROUP BY - go straight to menu
    Console.WriteLine("\nApprox counts: Editions ~14M, Works ~10M, Authors ~4M");
    
    Console.WriteLine("\nWhich type to index?");
    Console.WriteLine("1. /type/edition → ol_editions_raw (~14M)");
    Console.WriteLine("2. /type/work → ol_works_raw (~10M)");
    Console.WriteLine("3. /type/author → ol_authors_raw (~4M)");
    Console.Write("Choice: ");
    
    var typeChoice = Console.ReadLine()?.Trim();
    
    string recordType, indexName;
    switch (typeChoice)
    {
        case "1":
            recordType = "/type/edition";
            indexName = "ol_editions_raw";
            break;
        case "2":
            recordType = "/type/work";
            indexName = "ol_works_raw";
            break;
        case "3":
            recordType = "/type/author";
            indexName = "ol_authors_raw";
            break;
        default:
            Console.WriteLine("Invalid choice");
            return;
    }
    
    // Use hardcoded counts to avoid slow query (idx_type index helps but still slow on 30M rows)
    long totalRecords = recordType switch
    {
        "/type/edition" => 14245208,
        "/type/work" => 10429226,
        "/type/author" => 3855814,
        _ => 1000000
    };
    
    Console.WriteLine($"Total {recordType} records: ~{totalRecords:N0}");
    
    // Delete and recreate index
    Console.Write($"Delete and recreate {indexName}? (y/n): ");
    if (Console.ReadLine()?.ToLower() == "y")
    {
        try { await elastic.Indices.DeleteAsync(indexName); } catch { }
        
        var createResp = await elastic.Indices.CreateAsync(indexName, c => c
            .Settings(s => s
                .NumberOfShards(1)
                .NumberOfReplicas(0)
                .RefreshInterval(TimeSpan.FromSeconds(60))
            )
        );
        
        if (!createResp.IsValidResponse)
        {
            Console.WriteLine($"ERROR: {createResp.DebugInformation}");
            return;
        }
    }
    
    // Index with JSON parsing - use raw HTTP to avoid .NET 10 reflection issues
    var startTime = DateTime.Now;
    long indexed = 0;
    string lastKey = "";
    var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    
    while (true)
    {
        var bulkBody = new StringBuilder();
        int batchCount = 0;
        
        await using var cmd = new MySqlCommand(
            @"SELECT ol_key, json FROM ol_base 
              WHERE type = @type AND ol_key > @lastKey
              ORDER BY ol_key LIMIT @limit", connection);
        cmd.Parameters.AddWithValue("@type", recordType);
        cmd.Parameters.AddWithValue("@lastKey", lastKey);
        cmd.Parameters.AddWithValue("@limit", batchSize);
        cmd.CommandTimeout = 300;
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var olKey = reader.GetString(0);
            var jsonStr = reader.GetString(1);
            lastKey = olKey;
            
            try
            {
                using var jsonDoc = JsonDocument.Parse(jsonStr);
                var root = jsonDoc.RootElement;
                
                // Build index doc with key fields extracted
                var indexDoc = new StringBuilder();
                indexDoc.Append("{");
                indexDoc.Append($"\"ol_key\":{JsonSerializer.Serialize(olKey)},");
                
                if (root.TryGetProperty("title", out var title))
                    indexDoc.Append($"\"title\":{title.GetRawText()},");
                if (root.TryGetProperty("name", out var name))
                    indexDoc.Append($"\"name\":{name.GetRawText()},");
                if (root.TryGetProperty("isbn_13", out var isbn13))
                    indexDoc.Append($"\"isbn_13\":{isbn13.GetRawText()},");
                if (root.TryGetProperty("isbn_10", out var isbn10))
                    indexDoc.Append($"\"isbn_10\":{isbn10.GetRawText()},");
                if (root.TryGetProperty("publishers", out var pubs))
                    indexDoc.Append($"\"publishers\":{pubs.GetRawText()},");
                if (root.TryGetProperty("publish_date", out var pd))
                    indexDoc.Append($"\"publish_date\":{pd.GetRawText()},");
                if (root.TryGetProperty("subjects", out var subj))
                    indexDoc.Append($"\"subjects\":{subj.GetRawText()},");
                if (root.TryGetProperty("authors", out var auth))
                    indexDoc.Append($"\"authors\":{auth.GetRawText()},");
                if (root.TryGetProperty("works", out var works))
                    indexDoc.Append($"\"works\":{works.GetRawText()},");
                if (root.TryGetProperty("covers", out var covers))
                    indexDoc.Append($"\"covers\":{covers.GetRawText()},");
                if (root.TryGetProperty("ocaid", out var ocaid))
                    indexDoc.Append($"\"ocaid\":{ocaid.GetRawText()},");
                    
                // Remove trailing comma and close
                if (indexDoc[indexDoc.Length - 1] == ',')
                    indexDoc.Length--;
                indexDoc.Append("}");
                
                // Build bulk action line  
                bulkBody.AppendLine($"{{\"index\":{{\"_id\":{JsonSerializer.Serialize(olKey)}}}}}");
                bulkBody.AppendLine(indexDoc.ToString());
                batchCount++;
            }
            catch
            {
                // Skip malformed JSON
            }
        }
        
        if (batchCount == 0) break;
        
        // Send bulk request via HTTP
        var content = new StringContent(bulkBody.ToString(), Encoding.UTF8, "application/x-ndjson");
        var response = await httpClient.PostAsync($"{elasticUrl}/{indexName}/_bulk", content);
        
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"\nBulk error: {response.StatusCode}");
            break;
        }
        
        indexed += batchCount;
        
        // Progress
        var elapsed = DateTime.Now - startTime;
        var rate = indexed / Math.Max(1, elapsed.TotalSeconds);
        var eta = TimeSpan.FromSeconds((totalRecords - indexed) / Math.Max(1, rate));
        var pct = indexed * 100.0 / totalRecords;
        
        Console.Write($"\r  Indexed: {indexed:N0} / {totalRecords:N0} ({pct:F1}%) | {rate:F0}/sec | ETA: {eta:hh\\:mm\\:ss}    ");
    }
    
    await elastic.Indices.RefreshAsync(indexName);
    Console.WriteLine($"\n\nComplete! Indexed {indexed:N0} records in {DateTime.Now - startTime:hh\\:mm\\:ss}");
}

// ============================================================================
// SHOW STATS
// ============================================================================
async Task ShowStats()
{
    Console.WriteLine("\n=== Elasticsearch Index Statistics ===");
    
    var indices = new[] { "ol_editions", "ol_works", "ol_authors", "ol_editions_raw", "ol_works_raw", "ol_authors_raw" };
    
    foreach (var idx in indices)
    {
        try
        {
            var countResp = await elastic.CountAsync<object>(c => c.Indices(idx));
            if (countResp.IsValidResponse)
            {
                Console.WriteLine($"  {idx}: {countResp.Count:N0} documents");
            }
        }
        catch
        {
            // Index doesn't exist
        }
    }
    
    Console.WriteLine("\n=== MariaDB Table Statistics ===");
    var tables = new[] { "ol_base", "ol_editions", "ol_works", "ol_authors" };
    foreach (var tbl in tables)
    {
        try
        {
            await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {tbl}", connection);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Console.WriteLine($"  {tbl}: {count:N0} rows");
        }
        catch
        {
            Console.WriteLine($"  {tbl}: (not found)");
        }
    }
}

// ============================================================================
// HELPER: Create index with settings (no mappings - dynamic)
// ============================================================================
async Task<bool> CreateIndexIfNeeded(string indexName)
{
    var existsResp = await elastic.Indices.ExistsAsync(indexName);
    
    if (existsResp.Exists)
    {
        var countResp = await elastic.CountAsync<object>(c => c.Indices(indexName));
        Console.WriteLine($"Index {indexName} exists with {countResp.Count:N0} documents");
        
        Console.Write("Delete and recreate? (y/n): ");
        if (Console.ReadLine()?.ToLower() != "y")
        {
            return false;
        }
        
        await elastic.Indices.DeleteAsync(indexName);
    }
    
    Console.WriteLine($"Creating index {indexName}...");
    var createResp = await elastic.Indices.CreateAsync(indexName, c => c
        .Settings(s => s
            .NumberOfShards(1)
            .NumberOfReplicas(0)
            .RefreshInterval(TimeSpan.FromSeconds(60))
        )
    );
    
    if (!createResp.IsValidResponse)
    {
        Console.WriteLine($"Failed to create index: {createResp.DebugInformation}");
        return false;
    }
    return true;
}

// ============================================================================
// HELPER: Generic Table Indexer
// ============================================================================
async Task IndexTable<T>(string tableName, string indexName, string sql, 
    Func<MySqlDataReader, T> mapper, long totalRecords) where T : class, IHasOlKey
{
    var startTime = DateTime.Now;
    long indexed = 0;
    long errors = 0;
    string lastKey = "";
    
    while (true)
    {
        var batch = new List<T>();
        
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@lastKey", lastKey);
        cmd.Parameters.AddWithValue("@limit", batchSize);
        cmd.CommandTimeout = 300;
        
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var doc = mapper(reader);
            batch.Add(doc);
            lastKey = doc.OlKey;
        }
        
        if (batch.Count == 0) break;
        
        // Bulk index
        var bulkResponse = await elastic.BulkAsync(b => b
            .Index(indexName)
            .IndexMany(batch, (op, doc) => op.Id(doc.OlKey))
        );
        
        if (bulkResponse.Errors)
        {
            errors += bulkResponse.ItemsWithErrors.Count();
        }
        
        indexed += batch.Count;
        
        // Progress
        var elapsed = DateTime.Now - startTime;
        var rate = indexed / Math.Max(1, elapsed.TotalSeconds);
        var eta = TimeSpan.FromSeconds((totalRecords - indexed) / Math.Max(1, rate));
        var pct = indexed * 100.0 / totalRecords;
        
        Console.Write($"\r  Indexed: {indexed:N0} / {totalRecords:N0} ({pct:F1}%) | {rate:F0}/sec | ETA: {eta:hh\\:mm\\:ss}    ");
    }
    
    await elastic.Indices.RefreshAsync(indexName);
    
    var finalCount = await elastic.CountAsync<T>(c => c.Indices(indexName));
    Console.WriteLine();
    Console.WriteLine($"Complete! Indexed: {finalCount.Count:N0} | Errors: {errors:N0} | Time: {DateTime.Now - startTime:hh\\:mm\\:ss}");
}

// ============================================================================
// DOCUMENT CLASSES
// ============================================================================
interface IHasOlKey
{
    string OlKey { get; }
}

public class OpenLibraryEdition : IHasOlKey
{
    public string OlKey { get; set; } = "";
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Publishers { get; set; }
    public string? PublishDate { get; set; }
    public string? PublishCountry { get; set; }
    public int? NumberOfPages { get; set; }
    public string? PhysicalFormat { get; set; }
    public string? Isbn10 { get; set; }
    public string? Isbn13 { get; set; }
    public string? Lccn { get; set; }
    public string? OclcNumbers { get; set; }
    public string? LanguageCode { get; set; }
    public string? WorkKey { get; set; }
    public string? EditionName { get; set; }
    public string? ByStatement { get; set; }
    public string? SourceRecords { get; set; }
    public int Revision { get; set; }
    public DateTime LastModified { get; set; }
}

public class OpenLibraryWork : IHasOlKey
{
    public string OlKey { get; set; } = "";
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Subjects { get; set; }
    public string? SubjectPlaces { get; set; }
    public string? SubjectTimes { get; set; }
    public string? SubjectPeople { get; set; }
    public string? Description { get; set; }
    public string? FirstSentence { get; set; }
    public string? DeweyNumber { get; set; }
    public string? LcClassifications { get; set; }
    public string? FirstPublishDate { get; set; }
    public int Revision { get; set; }
    public DateTime LastModified { get; set; }
}

public class OpenLibraryAuthor : IHasOlKey
{
    public string OlKey { get; set; } = "";
    public string? Name { get; set; }
    public string? AlternateNames { get; set; }
    public string? PersonalName { get; set; }
    public string? BirthDate { get; set; }
    public string? DeathDate { get; set; }
    public string? Bio { get; set; }
    public string? Wikipedia { get; set; }
    public string? RemoteIds { get; set; }
    public string? SourceRecords { get; set; }
    public int Revision { get; set; }
    public DateTime LastModified { get; set; }
}

// Strongly-typed document class to avoid .NET 10 reflection serialization issues
public class OlRawDoc
{
    public string? OlKey { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }  // For authors
    public string? Isbn13 { get; set; }
    public string? Isbn10 { get; set; }
    public string? Publisher { get; set; }
    public string? PublishDate { get; set; }
    public string? Subjects { get; set; }
    public string? RawJson { get; set; }  // Store full JSON for later use
}
