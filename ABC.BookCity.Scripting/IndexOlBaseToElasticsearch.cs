#:package MySqlConnector@2.3.7
#:package Elastic.Clients.Elasticsearch@8.11.0

// Index OpenLibrary ol_base directly to Elasticsearch
// Parses JSON on the fly - works without extraction tables
// Run: dotnet run

using MySqlConnector;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using System.Text.Json;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    OpenLibrary → Elasticsearch Indexer (from ol_base)        ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var mariaDbConn = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
var elasticUrl = "http://localhost:9200";
var batchSize = 2000; // Smaller due to large JSON

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
Console.WriteLine("\nWhich record type to index?");
Console.WriteLine("1. Editions (/type/edition) → ol_editions");
Console.WriteLine("2. Works (/type/work) → ol_works");  
Console.WriteLine("3. Authors (/type/author) → ol_authors");
Console.WriteLine("Q. Quit");
Console.Write("\nChoice: ");

var choice = Console.ReadLine()?.Trim().ToLower();
if (choice == "q") return;

string recordType, indexName;
switch (choice)
{
    case "1":
        recordType = "/type/edition";
        indexName = "ol_editions";
        break;
    case "2":
        recordType = "/type/work";
        indexName = "ol_works";
        break;
    case "3":
        recordType = "/type/author";
        indexName = "ol_authors";
        break;
    default:
        Console.WriteLine("Invalid choice");
        return;
}

// Get count
long totalRecords;
await using (var cmd = new MySqlCommand(
    "SELECT COUNT(*) FROM ol_base WHERE type = @type", connection))
{
    cmd.Parameters.AddWithValue("@type", recordType);
    cmd.CommandTimeout = 300;
    totalRecords = Convert.ToInt64(await cmd.ExecuteScalarAsync());
}
Console.WriteLine($"Total {recordType} records in MariaDB: {totalRecords:N0}");

// Check existing
var countResponse = await elastic.CountAsync<OlDoc>(c => c.Indices(indexName));
var existingCount = countResponse.IsValidResponse ? countResponse.Count : 0;
Console.WriteLine($"Existing in Elasticsearch: {existingCount:N0}");

Console.Write($"Delete and recreate {indexName}? (y/n): ");
if (Console.ReadLine()?.ToLower() == "y")
{
    Console.WriteLine("Deleting existing index...");
    try { await elastic.Indices.DeleteAsync(indexName); } catch { }

    Console.WriteLine("Creating index with mappings...");
    var createResponse = await elastic.Indices.CreateAsync(indexName, c => c
        .Settings(s => s
            .NumberOfShards(1)
            .NumberOfReplicas(0)
            .RefreshInterval(TimeSpan.FromSeconds(60))
        )
        .Mappings(m => m
            .Properties<OlDoc>(p => p
                .Keyword(k => k.OlKey)
                .Keyword(k => k.Type)
                .Text(t => t.Title, t => t.Analyzer("standard"))
                .Text(t => t.Name, t => t.Analyzer("standard"))
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
                .IntegerNumber(i => i.CoverId)
                .Keyword(k => k.IaId)
                .Text(t => t.Bio)
                .Keyword(k => k.BirthDate)
                .Keyword(k => k.DeathDate)
                .IntegerNumber(i => i.Revision)
                .Date(d => d.LastModified)
            )
        )
    );

    if (!createResponse.IsValidResponse)
    {
        Console.WriteLine($"ERROR creating index: {createResponse.DebugInformation}");
        return;
    }
}

// Index in batches using cursor-based pagination
Console.WriteLine($"Starting indexing with batch size {batchSize}...");
var startTime = DateTime.Now;
long indexed = 0;
long errors = 0;
long skipped = 0;
string lastKey = "";

while (true)
{
    var batch = new List<OlDoc>();

    var sql = @"SELECT ol_key, json FROM ol_base 
                WHERE type = @type AND ol_key > @lastKey
                ORDER BY ol_key LIMIT @limit";

    await using var cmd = new MySqlCommand(sql, connection);
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

            var doc = new OlDoc
            {
                OlKey = olKey,
                Type = recordType
            };

            // Common fields
            if (root.TryGetProperty("title", out var title))
                doc.Title = title.GetString();
            if (root.TryGetProperty("name", out var name))
                doc.Name = name.GetString();
            if (root.TryGetProperty("subtitle", out var subtitle))
                doc.Subtitle = subtitle.GetString();
            if (root.TryGetProperty("revision", out var rev))
                doc.Revision = rev.GetInt32();

            // Edition fields
            if (root.TryGetProperty("isbn_10", out var isbn10) && isbn10.ValueKind == JsonValueKind.Array)
                doc.Isbn10 = string.Join(",", isbn10.EnumerateArray().Select(x => x.GetString()));
            if (root.TryGetProperty("isbn_13", out var isbn13) && isbn13.ValueKind == JsonValueKind.Array)
                doc.Isbn13 = string.Join(",", isbn13.EnumerateArray().Select(x => x.GetString()));
            if (root.TryGetProperty("lccn", out var lccn) && lccn.ValueKind == JsonValueKind.Array)
                doc.Lccn = string.Join(",", lccn.EnumerateArray().Select(x => x.GetString()));
            if (root.TryGetProperty("oclc_numbers", out var oclc) && oclc.ValueKind == JsonValueKind.Array)
                doc.OclcNumbers = string.Join(",", oclc.EnumerateArray().Select(x => x.GetString()));
            if (root.TryGetProperty("publishers", out var pubs) && pubs.ValueKind == JsonValueKind.Array)
                doc.Publishers = string.Join("; ", pubs.EnumerateArray().Select(x => x.GetString()));
            if (root.TryGetProperty("publish_date", out var pubDate))
                doc.PublishDate = pubDate.GetString();
            if (root.TryGetProperty("publish_country", out var pubCountry))
                doc.PublishCountry = pubCountry.GetString();
            if (root.TryGetProperty("number_of_pages", out var pages))
                doc.NumberOfPages = pages.TryGetInt32(out var p) ? p : null;
            if (root.TryGetProperty("physical_format", out var format))
                doc.PhysicalFormat = format.GetString();

            // Work link
            if (root.TryGetProperty("works", out var works) && works.ValueKind == JsonValueKind.Array)
            {
                var first = works.EnumerateArray().FirstOrDefault();
                if (first.TryGetProperty("key", out var wk))
                    doc.WorkKey = wk.GetString();
            }

            // Author links
            if (root.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array)
            {
                var authorKeys = new List<string>();
                foreach (var a in authors.EnumerateArray())
                {
                    // Can be {key: ...} or {author: {key: ...}}
                    if (a.TryGetProperty("key", out var ak))
                        authorKeys.Add(ak.GetString() ?? "");
                    else if (a.TryGetProperty("author", out var authorObj) && authorObj.TryGetProperty("key", out var aok))
                        authorKeys.Add(aok.GetString() ?? "");
                }
                doc.AuthorKeys = string.Join(",", authorKeys.Where(x => !string.IsNullOrEmpty(x)));
            }

            // Work fields
            if (root.TryGetProperty("subjects", out var subjects) && subjects.ValueKind == JsonValueKind.Array)
                doc.Subjects = string.Join("; ", subjects.EnumerateArray().Take(20).Select(x => x.GetString()));
            if (root.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array)
            {
                var first = covers.EnumerateArray().FirstOrDefault();
                if (first.TryGetInt32(out var coverId))
                    doc.CoverId = coverId;
            }

            // Internet Archive
            if (root.TryGetProperty("ocaid", out var ocaid))
                doc.IaId = ocaid.GetString();
            else if (root.TryGetProperty("ia", out var ia))
                doc.IaId = ia.GetString();

            // Author fields
            if (root.TryGetProperty("bio", out var bio))
            {
                if (bio.ValueKind == JsonValueKind.String)
                    doc.Bio = bio.GetString();
                else if (bio.TryGetProperty("value", out var bioVal))
                    doc.Bio = bioVal.GetString();
            }
            if (root.TryGetProperty("birth_date", out var birth))
                doc.BirthDate = birth.GetString();
            if (root.TryGetProperty("death_date", out var death))
                doc.DeathDate = death.GetString();

            // Last modified
            if (root.TryGetProperty("last_modified", out var lm))
            {
                if (lm.TryGetProperty("value", out var lmVal))
                {
                    if (DateTime.TryParse(lmVal.GetString(), out var dt))
                        doc.LastModified = dt;
                }
            }

            batch.Add(doc);
        }
        catch
        {
            skipped++;
        }
    }

    if (batch.Count == 0)
        break;

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

// Refresh index
Console.WriteLine();
Console.WriteLine("Refreshing index...");
await elastic.Indices.RefreshAsync(indexName);

// Final stats
var finalCount = await elastic.CountAsync<OlDoc>(c => c.Indices(indexName));
Console.WriteLine($"\n========== COMPLETE ==========");
Console.WriteLine($"Total indexed: {indexed:N0}");
Console.WriteLine($"Errors: {errors:N0}");
Console.WriteLine($"Skipped (bad JSON): {skipped:N0}");
Console.WriteLine($"Final ES count: {finalCount.Count:N0}");
Console.WriteLine($"Time: {DateTime.Now - startTime}");

// Document class
public class OlDoc
{
    public string OlKey { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Subtitle { get; set; }
    public string? Isbn10 { get; set; }
    public string? Isbn13 { get; set; }
    public string? Lccn { get; set; }
    public string? OclcNumbers { get; set; }
    public string? Publishers { get; set; }
    public string? PublishDate { get; set; }
    public string? PublishCountry { get; set; }
    public int? NumberOfPages { get; set; }
    public string? PhysicalFormat { get; set; }
    public string? WorkKey { get; set; }
    public string? AuthorKeys { get; set; }
    public string? Subjects { get; set; }
    public int? CoverId { get; set; }
    public string? IaId { get; set; }
    public string? Bio { get; set; }
    public string? BirthDate { get; set; }
    public string? DeathDate { get; set; }
    public int Revision { get; set; }
    public DateTime? LastModified { get; set; }
}
