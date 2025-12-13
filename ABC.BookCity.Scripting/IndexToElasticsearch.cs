#:package MySqlConnector@2.3.7
#:package Elastic.Clients.Elasticsearch@8.11.0

// Index HathiTrust catalog from MariaDB to Elasticsearch
// Run: dotnet run

using MySqlConnector;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using System.Text.Json;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    HathiTrust Elasticsearch Indexer                          ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var mariaDbConn = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
var elasticUrl = "http://localhost:9200";
var indexName = "hathi_catalog";
var batchSize = 5000;

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

// Get total count from MariaDB
await using var connection = new MySqlConnection(mariaDbConn);
await connection.OpenAsync();

long totalRecords;
await using (var countCmd = new MySqlCommand("SELECT COUNT(*) FROM hathi_catalog", connection))
{
    totalRecords = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
}
Console.WriteLine($"Total records in MariaDB: {totalRecords:N0}");

// Check existing ES count
var countResponse = await elastic.CountAsync<HathiDoc>(c => c.Index(indexName));
var existingCount = countResponse.Count;
Console.WriteLine($"Existing records in Elasticsearch: {existingCount:N0}");

if (existingCount > 0)
{
    Console.Write("Index has data. Delete and reindex? (y/n): ");
    var answer = Console.ReadLine();
    if (answer?.ToLower() != "y")
    {
        Console.WriteLine("Aborted.");
        return;
    }
    
    Console.WriteLine("Deleting existing index...");
    await elastic.Indices.DeleteAsync(indexName);
    
    // Recreate with mappings
    Console.WriteLine("Creating index with mappings...");
    var createResponse = await elastic.Indices.CreateAsync(indexName, c => c
        .Settings(s => s
            .NumberOfShards(1)
            .NumberOfReplicas(0)
            .RefreshInterval(TimeSpan.FromSeconds(30)) // Faster indexing
        )
        .Mappings(m => m
            .Properties<HathiDoc>(p => p
                .Keyword(k => k.Htid)
                .Text(t => t.Title, t => t.Analyzer("standard"))
                .Text(t => t.Author, t => t.Analyzer("standard"))
                .Keyword(k => k.Access)
                .Keyword(k => k.Rights)
                .Keyword(k => k.Lang)
                .Keyword(k => k.RightsDateUsed)
                .LongNumber(l => l.HtBibKey)
                .Keyword(k => k.Isbn)
                .Keyword(k => k.OclcNum)
                .Keyword(k => k.Lccn)
                .Keyword(k => k.Source)
                .Text(t => t.Imprint)
                .Text(t => t.Description)
                .Keyword(k => k.PubPlace)
                .Keyword(k => k.BibFmt)
                .Keyword(k => k.CollectionCode)
                .Boolean(b => b.UsGovDocFlag)
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
string lastHtid = "";

while (true)
{
    var batch = new List<HathiDoc>();
    
    var sql = @"
        SELECT htid, access, rights, ht_bib_key, description, source, source_bib_num,
               oclc_num, isbn, issn, lccn, title, imprint, rights_reason_code,
               rights_timestamp, us_gov_doc_flag, rights_date_used, pub_place, lang,
               bib_fmt, collection_code, content_provider_code, responsible_entity_code,
               digitization_agent_code, access_profile_code, author
        FROM hathi_catalog
        WHERE htid > @lastHtid
        ORDER BY htid
        LIMIT @limit";
    
    await using var cmd = new MySqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("@lastHtid", lastHtid);
    cmd.Parameters.AddWithValue("@limit", batchSize);
    cmd.CommandTimeout = 120;
    
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var doc = new HathiDoc
        {
            Htid = reader.GetString(0),
            Access = reader.IsDBNull(1) ? null : reader.GetString(1),
            Rights = reader.IsDBNull(2) ? null : reader.GetString(2),
            HtBibKey = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            Source = reader.IsDBNull(5) ? null : reader.GetString(5),
            SourceBibNum = reader.IsDBNull(6) ? null : reader.GetString(6),
            OclcNum = reader.IsDBNull(7) ? null : reader.GetString(7),
            Isbn = reader.IsDBNull(8) ? null : reader.GetString(8),
            Issn = reader.IsDBNull(9) ? null : reader.GetString(9),
            Lccn = reader.IsDBNull(10) ? null : reader.GetString(10),
            Title = reader.IsDBNull(11) ? null : reader.GetString(11),
            Imprint = reader.IsDBNull(12) ? null : reader.GetString(12),
            RightsReasonCode = reader.IsDBNull(13) ? null : reader.GetString(13),
            RightsTimestamp = reader.IsDBNull(14) ? null : reader.GetDateTime(14),
            UsGovDocFlag = reader.IsDBNull(15) ? null : reader.GetBoolean(15),
            RightsDateUsed = reader.IsDBNull(16) ? null : reader.GetString(16),
            PubPlace = reader.IsDBNull(17) ? null : reader.GetString(17),
            Lang = reader.IsDBNull(18) ? null : reader.GetString(18),
            BibFmt = reader.IsDBNull(19) ? null : reader.GetString(19),
            CollectionCode = reader.IsDBNull(20) ? null : reader.GetString(20),
            ContentProviderCode = reader.IsDBNull(21) ? null : reader.GetString(21),
            ResponsibleEntityCode = reader.IsDBNull(22) ? null : reader.GetString(22),
            DigitizationAgentCode = reader.IsDBNull(23) ? null : reader.GetString(23),
            AccessProfileCode = reader.IsDBNull(24) ? null : reader.GetString(24),
            Author = reader.IsDBNull(25) ? null : reader.GetString(25)
        };
        batch.Add(doc);
        lastHtid = doc.Htid;
    }
    
    if (batch.Count == 0)
        break;
    
    // Bulk index
    var bulkResponse = await elastic.BulkAsync(b => b
        .Index(indexName)
        .IndexMany(batch, (op, doc) => op.Id(doc.Htid))
    );
    
    if (bulkResponse.Errors)
    {
        errors += bulkResponse.ItemsWithErrors.Count();
        Console.WriteLine($"  Batch errors: {bulkResponse.ItemsWithErrors.Count()}");
    }
    
    indexed += batch.Count;
    
    // Progress
    var elapsed = DateTime.Now - startTime;
    var rate = indexed / elapsed.TotalSeconds;
    var eta = TimeSpan.FromSeconds((totalRecords - indexed) / rate);
    var pct = (indexed * 100.0 / totalRecords);
    
    Console.Write($"\r  Indexed: {indexed:N0} / {totalRecords:N0} ({pct:F1}%) | {rate:F0}/sec | ETA: {eta:hh\\:mm\\:ss}    ");
}

// Refresh index
Console.WriteLine();
Console.WriteLine("Refreshing index...");
await elastic.Indices.RefreshAsync(indexName);

// Final stats
var finalCount = await elastic.CountAsync<HathiDoc>(c => c.Index(indexName));
var totalTime = DateTime.Now - startTime;

Console.WriteLine();
Console.WriteLine("=== Indexing Complete ===");
Console.WriteLine($"Total indexed: {finalCount.Count:N0}");
Console.WriteLine($"Errors: {errors:N0}");
Console.WriteLine($"Time: {totalTime:hh\\:mm\\:ss}");
Console.WriteLine($"Rate: {indexed / totalTime.TotalSeconds:F0} docs/sec");

// Test search
Console.WriteLine();
Console.WriteLine("Testing search for 'shakespeare'...");
var searchResponse = await elastic.SearchAsync<HathiDoc>(s => s
    .Index(indexName)
    .Query(q => q
        .MultiMatch(mm => mm
            .Query("shakespeare")
            .Fields(new[] { "title", "author" })
            .Fuzziness(new Fuzziness("AUTO"))
        )
    )
    .Size(3)
);

Console.WriteLine($"Found {searchResponse.Total} results:");
foreach (var hit in searchResponse.Hits)
{
    Console.WriteLine($"  - {hit.Source?.Title} by {hit.Source?.Author}");
}

// Document class
public class HathiDoc
{
    public string Htid { get; set; } = "";
    public string? Access { get; set; }
    public string? Rights { get; set; }
    public long? HtBibKey { get; set; }
    public string? Description { get; set; }
    public string? Source { get; set; }
    public string? SourceBibNum { get; set; }
    public string? OclcNum { get; set; }
    public string? Isbn { get; set; }
    public string? Issn { get; set; }
    public string? Lccn { get; set; }
    public string? Title { get; set; }
    public string? Imprint { get; set; }
    public string? RightsReasonCode { get; set; }
    public DateTime? RightsTimestamp { get; set; }
    public bool? UsGovDocFlag { get; set; }
    public string? RightsDateUsed { get; set; }
    public string? PubPlace { get; set; }
    public string? Lang { get; set; }
    public string? BibFmt { get; set; }
    public string? CollectionCode { get; set; }
    public string? ContentProviderCode { get; set; }
    public string? ResponsibleEntityCode { get; set; }
    public string? DigitizationAgentCode { get; set; }
    public string? AccessProfileCode { get; set; }
    public string? Author { get; set; }
}
