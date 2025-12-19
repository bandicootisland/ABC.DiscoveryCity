#:package MySqlConnector@2.3.7
#:package Elastic.Clients.Elasticsearch@8.11.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Index OpenLibrary editions from ol_base to Elasticsearch
// Based on working HathiTrust indexer pattern
// Run: .\Run-Script.ps1 -ScriptFile "IndexOpenLibrary.cs"

using MySqlConnector;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using System.Text.Json;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    OpenLibrary Elasticsearch Indexer                         ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var mariaDbConn = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
var elasticUrl = "http://localhost:9200";
var indexName = "ol_editions";
var batchSize = 10000; // Larger batch since we filter in C#

// Setup Elasticsearch client
var settings = new ElasticsearchClientSettings(new Uri(elasticUrl))
    .DefaultIndex(indexName)
    .DisableDirectStreaming()
    .RequestTimeout(TimeSpan.FromMinutes(5));
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

// Get total count - use known value to avoid slow GROUP BY
long totalRecords = 29795943; // Total rows in ol_base
long totalEditions = 14245208; // Estimated editions
Console.WriteLine($"Total records to scan in MariaDB: {totalRecords:N0}");

// Check if index exists
var existsResponse = await elastic.Indices.ExistsAsync(indexName);
string lastKey = "";

if (existsResponse.Exists)
{
    var countResponse = await elastic.CountAsync<OlEdition>(c => c.Indices(indexName));
    var existingCount = countResponse.IsValidResponse ? countResponse.Count : 0;
    Console.WriteLine($"Index '{indexName}' exists with {existingCount:N0} records.");

    Console.Write("Choose action: [D]elete and restart, [R]esume, [A]bort: ");
    var choice = Console.ReadLine()?.ToUpper();
    
    if (choice == "D")
    {
        Console.WriteLine("Deleting existing index...");
        await elastic.Indices.DeleteAsync(indexName);
    }
    else if (choice == "R")
    {
        Console.WriteLine("Resuming from last indexed record...");
        // Find the last key in Elasticsearch
        var resumeSearchResponse = await elastic.SearchAsync<OlEdition>(s => s
            .Index(indexName)
            .Sort(sort => sort.Field(f => f.OlKey, f => f.Order(SortOrder.Desc)))
            .Size(1)
        );
        
        if (resumeSearchResponse.IsValidResponse && resumeSearchResponse.Documents.Any())
        {
            lastKey = resumeSearchResponse.Documents.First().OlKey;
            Console.WriteLine($"Found last key: {lastKey}");
        }
        else
        {
            Console.WriteLine("Could not find last key, starting from beginning.");
        }
        goto StartIndexing;
    }
    else
    {
        Console.WriteLine("Aborting to avoid overwriting or duplicate errors.");
        return;
    }
}

// Create index with mappings
Console.WriteLine("Creating index with mappings...");
var createResponse = await elastic.Indices.CreateAsync(indexName, c => c
    .Settings(s => s
        .NumberOfShards(1)
        .NumberOfReplicas(0)
        .RefreshInterval(TimeSpan.FromSeconds(60)) // Faster indexing
    )
    .Mappings(m => m
        .Properties<OlEdition>(p => p
            .Keyword(k => k.OlKey)
            .Text(t => t.Title, t => t.Analyzer("standard"))
            .Text(t => t.Subtitle)
            .Keyword(k => k.Isbn10)
            .Keyword(k => k.Isbn13)
            .Keyword(k => k.Lccn)
            .Keyword(k => k.OclcNumbers)
            .Text(t => t.Publishers)
            .Keyword(k => k.PublishDate)
            .Keyword(k => k.PublishCountry)
            .IntegerNumber(i => i.NumberOfPages)
            .Keyword(k => k.PhysicalFormat)
            .Keyword(k => k.WorkKey)
            .Keyword(k => k.AuthorKeys)
            .Text(t => t.Subjects)
            .Keyword(k => k.CoverId)
            .Keyword(k => k.IaId)
            .Keyword(k => k.RawJson, k => k.Index(false)) // Store but don't index for search
        )
    )
);

if (!createResponse.IsValidResponse)
{
    Console.WriteLine($"ERROR creating index: {createResponse.DebugInformation}");
    return;
}
Console.WriteLine("Index created successfully");

StartIndexing:
// Index in batches using cursor-based pagination
Console.WriteLine($"Starting indexing with batch size {batchSize}...");
var startTime = DateTime.Now;
long indexed = 0;
long errors = 0;
long skipped = 0;
long totalScanned = 0;
// lastKey is already set above if resuming

while (true)
{
    var batch = new List<OlEdition>();
    int rowsInBatch = 0;
    
    // We remove the 'type' filter from SQL to allow a pure primary key scan.
    // This is MUCH faster than filtering on a non-indexed column while ordering.
    var sql = @"SELECT ol_key, type, json FROM ol_base 
                WHERE ol_key > @lastKey
                ORDER BY ol_key LIMIT @limit";
    
    await using var cmd = new MySqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("@lastKey", lastKey);
    cmd.Parameters.AddWithValue("@limit", batchSize);
    cmd.CommandTimeout = 300;
    
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rowsInBatch++;
        var olKey = reader.GetString(0);
        var type = reader.GetString(1);
        var jsonStr = reader.GetString(2);
        lastKey = olKey;
        
        if (type != "/type/edition")
            continue;
        
        try
        {
            using var jsonDoc = JsonDocument.Parse(jsonStr);
            var root = jsonDoc.RootElement;
            
            var doc = new OlEdition { OlKey = olKey, RawJson = jsonStr };
            
            // Title
            if (root.TryGetProperty("title", out var title))
                doc.Title = title.GetString();
            if (root.TryGetProperty("subtitle", out var subtitle))
                doc.Subtitle = subtitle.GetString();
                
            // ISBNs
            if (root.TryGetProperty("isbn_10", out var isbn10) && isbn10.ValueKind == JsonValueKind.Array)
                doc.Isbn10 = isbn10.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            if (root.TryGetProperty("isbn_13", out var isbn13) && isbn13.ValueKind == JsonValueKind.Array)
                doc.Isbn13 = isbn13.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            if (root.TryGetProperty("lccn", out var lccn) && lccn.ValueKind == JsonValueKind.Array)
                doc.Lccn = lccn.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            if (root.TryGetProperty("oclc_numbers", out var oclc) && oclc.ValueKind == JsonValueKind.Array)
                doc.OclcNumbers = oclc.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                
            // Publishers
            if (root.TryGetProperty("publishers", out var pubs) && pubs.ValueKind == JsonValueKind.Array)
                doc.Publishers = pubs.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            if (root.TryGetProperty("publish_date", out var pubDate))
                doc.PublishDate = pubDate.GetString();
            if (root.TryGetProperty("publish_country", out var pubCountry))
                doc.PublishCountry = pubCountry.GetString();
                
            // Physical
            if (root.TryGetProperty("number_of_pages", out var pages) && pages.ValueKind == JsonValueKind.Number)
                doc.NumberOfPages = pages.GetInt32();
            if (root.TryGetProperty("physical_format", out var format))
                doc.PhysicalFormat = format.GetString();
                
            // Links to works/authors
            if (root.TryGetProperty("works", out var works) && works.ValueKind == JsonValueKind.Array)
            {
                var workKeys = works.EnumerateArray()
                    .Where(w => w.TryGetProperty("key", out _))
                    .Select(w => w.GetProperty("key").GetString() ?? "")
                    .ToList();
                doc.WorkKey = workKeys.FirstOrDefault();
            }
            if (root.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
            {
                doc.AuthorKeys = authors.EnumerateArray()
                    .Where(a => a.TryGetProperty("key", out _))
                    .Select(a => a.GetProperty("key").GetString() ?? "")
                    .ToList();
            }
            
            // Subjects
            if (root.TryGetProperty("subjects", out var subjects) && subjects.ValueKind == JsonValueKind.Array)
                doc.Subjects = subjects.EnumerateArray().Take(20).Select(x => x.GetString() ?? "").ToList();
                
            // Cover and Internet Archive
            if (root.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                doc.CoverId = covers[0].GetInt32().ToString();
            if (root.TryGetProperty("ocaid", out var ocaid))
                doc.IaId = ocaid.GetString();
            
            batch.Add(doc);
        }
        catch
        {
            skipped++;
        }
    }
    
    if (rowsInBatch == 0)
        break;
    
    if (batch.Count > 0)
    {
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
    }
    
    totalScanned += rowsInBatch;
    
    // Progress
    var elapsed = DateTime.Now - startTime;
    var rate = indexed / Math.Max(1, elapsed.TotalSeconds);
    var scanRate = totalScanned / Math.Max(1, elapsed.TotalSeconds);
    var eta = TimeSpan.FromSeconds((totalRecords - totalScanned) / Math.Max(1, scanRate));
    var pct = (totalScanned * 100.0 / totalRecords);
    
    Console.Write($"\r  Scanned: {totalScanned:N0} | Indexed: {indexed:N0} ({pct:F1}%) | {rate:F0}/sec | ETA: {eta:hh\\:mm\\:ss} | Err: {errors}    ");
}

// Refresh index
Console.WriteLine();
Console.WriteLine("Refreshing index...");
await elastic.Indices.RefreshAsync(indexName);

// Final stats
var finalCount = await elastic.CountAsync<OlEdition>(c => c.Indices(indexName));
var totalTime = DateTime.Now - startTime;

Console.WriteLine();
Console.WriteLine("=== Indexing Complete ===");
Console.WriteLine($"Total indexed: {finalCount.Count:N0}");
Console.WriteLine($"Skipped (bad JSON): {skipped:N0}");
Console.WriteLine($"Errors: {errors:N0}");
Console.WriteLine($"Time: {totalTime:hh\\:mm\\:ss}");
Console.WriteLine($"Rate: {indexed / Math.Max(1, totalTime.TotalSeconds):F0} docs/sec");

// Test search
Console.WriteLine();
Console.WriteLine("Testing search for 'harry potter'...");
var searchResponse = await elastic.SearchAsync<OlEdition>(s => s
    .Index(indexName)
    .Query(q => q
        .MultiMatch(mm => mm
            .Query("harry potter")
            .Fields(new[] { "title", "subjects" })
            .Fuzziness(new Fuzziness("AUTO"))
        )
    )
    .Size(5)
);

Console.WriteLine($"Found {searchResponse.Total} results:");
foreach (var hit in searchResponse.Hits)
{
    Console.WriteLine($"  - {hit.Source?.Title} ({hit.Source?.PublishDate}) ISBN: {hit.Source?.Isbn13 ?? hit.Source?.Isbn10}");
}

// Document class - strongly typed to match Hathi pattern
public class OlEdition
{
    public string OlKey { get; set; } = "";
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public List<string>? Isbn10 { get; set; }
    public List<string>? Isbn13 { get; set; }
    public List<string>? Lccn { get; set; }
    public List<string>? OclcNumbers { get; set; }
    public List<string>? Publishers { get; set; }
    public string? PublishDate { get; set; }
    public string? PublishCountry { get; set; }
    public int? NumberOfPages { get; set; }
    public string? PhysicalFormat { get; set; }
    public string? WorkKey { get; set; }
    public List<string>? AuthorKeys { get; set; }
    public List<string>? Subjects { get; set; }
    public string? CoverId { get; set; }
    public string? IaId { get; set; }
    public string? RawJson { get; set; }
}
