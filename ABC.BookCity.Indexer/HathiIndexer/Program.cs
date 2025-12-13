using MySqlConnector;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Transport;
using System.Text.Json.Serialization;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    HathiTrust Elasticsearch Indexer                          ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var mariaDbConn = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
var elasticUrl = "http://localhost:9200";
var indexName = "hathi_catalog";
var batchSize = 5000;

// Setup Elasticsearch client with compatibility mode for ES 8.x
var settings = new ElasticsearchClientSettings(new Uri(elasticUrl))
    .DefaultIndex(indexName)
    .RequestTimeout(TimeSpan.FromMinutes(5))
    .EnableDebugMode(); // Enable debug to see detailed errors
var elastic = new ElasticsearchClient(settings);

// Check connection
var pingResponse = await elastic.PingAsync();
if (!pingResponse.IsValidResponse)
{
    Console.WriteLine($"ERROR: Cannot connect to Elasticsearch at {elasticUrl}");
    Console.WriteLine($"Debug Info: {pingResponse.DebugInformation}");
    return;
}
Console.WriteLine($"✓ Connected to Elasticsearch at {elasticUrl}");

// Get total count from MariaDB
await using var connection = new MySqlConnection(mariaDbConn);
await connection.OpenAsync();
Console.WriteLine($"✓ Connected to MariaDB");

long totalRecords;
await using (var countCmd = new MySqlCommand("SELECT COUNT(*) FROM hathi_catalog", connection))
{
    totalRecords = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
}
Console.WriteLine($"  Total records in MariaDB: {totalRecords:N0}");

// Check existing ES count
var countResponse = await elastic.CountAsync<HathiDoc>(c => c.Indices(indexName));
var existingCount = countResponse.IsValidResponse ? countResponse.Count : 0;
Console.WriteLine($"  Existing records in Elasticsearch: {existingCount:N0}");

// Delete and recreate index
Console.WriteLine();
Console.WriteLine("Deleting existing index if exists...");
await elastic.Indices.DeleteAsync(indexName);

// Create with mappings optimized for search
Console.WriteLine("Creating index with optimized mappings...");
var createResponse = await elastic.Indices.CreateAsync(indexName, c => c
    .Settings(s => s
        .NumberOfShards(1)
        .NumberOfReplicas(0)
        .RefreshInterval("-1") // Disable refresh during indexing for speed
    )
    .Mappings(m => m
        .Properties<HathiDoc>(p => p
            .Keyword(t => t.Htid!)
            .Text(t => t.Title!)
            .Text(t => t.Author!)
            .Keyword(t => t.Access!)
            .Keyword(t => t.Rights!)
            .Keyword(t => t.Lang!)
            .Keyword(t => t.RightsDateUsed!)
            .LongNumber(t => t.HtBibKey)
            .Keyword(t => t.Isbn!)
            .Keyword(t => t.OclcNum!)
            .Keyword(t => t.Lccn!)
            .Keyword(t => t.Source!)
            .Text(t => t.Imprint!)
            .Text(t => t.Description!)
            .Keyword(t => t.PubPlace!)
            .Keyword(t => t.BibFmt!)
            .Keyword(t => t.CollectionCode!)
            .Boolean(t => t.UsGovDocFlag)
        )
    )
);

if (!createResponse.IsValidResponse)
{
    Console.WriteLine($"ERROR creating index: {createResponse.ElasticsearchServerError?.Error?.Reason}");
    return;
}
Console.WriteLine("✓ Index created");

// Index in batches using cursor-based pagination
Console.WriteLine();
Console.WriteLine($"Starting indexing with batch size {batchSize}...");
Console.WriteLine("─────────────────────────────────────────────────────────────────");
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
    cmd.CommandTimeout = 300;

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
            UsGovDocFlag = reader.IsDBNull(15) ? false : reader.GetBoolean(15),
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
    }

    indexed += batch.Count;

    // Progress
    var elapsed = DateTime.Now - startTime;
    var rate = indexed / elapsed.TotalSeconds;
    var eta = TimeSpan.FromSeconds((totalRecords - indexed) / rate);
    var pct = (indexed * 100.0 / totalRecords);

    Console.Write($"\r  Indexed: {indexed:N0} / {totalRecords:N0} ({pct:F1}%) | {rate:F0}/sec | ETA: {eta:hh\\:mm\\:ss}    ");
}

// Re-enable refresh and force refresh
Console.WriteLine();
Console.WriteLine();
Console.WriteLine("Forcing index refresh...");
await elastic.Indices.RefreshAsync(indexName);

// Final stats
var finalCount = await elastic.CountAsync<HathiDoc>(c => c.Indices(indexName));
var totalTime = DateTime.Now - startTime;

Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("                     INDEXING COMPLETE                          ");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine($"  Total indexed: {finalCount.Count:N0}");
Console.WriteLine($"  Errors: {errors:N0}");
Console.WriteLine($"  Time: {totalTime:hh\\:mm\\:ss}");
Console.WriteLine($"  Rate: {indexed / totalTime.TotalSeconds:F0} docs/sec");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

// Test search
Console.WriteLine();
Console.WriteLine("Testing search for 'shakespeare'...");
var searchResponse = await elastic.SearchAsync<HathiDoc>(s => s
    .Indices(indexName)
    .Query(q => q
        .MultiMatch(mm => mm
            .Query("shakespeare")
            .Fields(new[] { "title", "author" })
            .Fuzziness(new Fuzziness("AUTO"))
        )
    )
    .Size(3)
);

if (searchResponse.IsValidResponse)
{
    Console.WriteLine($"Found {searchResponse.Total} results:");
    foreach (var hit in searchResponse.Hits)
    {
        Console.WriteLine($"  • {hit.Source?.Title} by {hit.Source?.Author}");
    }
}

// Document class
public class HathiDoc
{
    [JsonPropertyName("htid")]
    public string? Htid { get; set; }
    
    [JsonPropertyName("access")]
    public string? Access { get; set; }
    
    [JsonPropertyName("rights")]
    public string? Rights { get; set; }
    
    [JsonPropertyName("ht_bib_key")]
    public long? HtBibKey { get; set; }
    
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    
    [JsonPropertyName("source")]
    public string? Source { get; set; }
    
    [JsonPropertyName("source_bib_num")]
    public string? SourceBibNum { get; set; }
    
    [JsonPropertyName("oclc_num")]
    public string? OclcNum { get; set; }
    
    [JsonPropertyName("isbn")]
    public string? Isbn { get; set; }
    
    [JsonPropertyName("issn")]
    public string? Issn { get; set; }
    
    [JsonPropertyName("lccn")]
    public string? Lccn { get; set; }
    
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    
    [JsonPropertyName("imprint")]
    public string? Imprint { get; set; }
    
    [JsonPropertyName("rights_reason_code")]
    public string? RightsReasonCode { get; set; }
    
    [JsonPropertyName("rights_timestamp")]
    public DateTime? RightsTimestamp { get; set; }
    
    [JsonPropertyName("us_gov_doc_flag")]
    public bool? UsGovDocFlag { get; set; }
    
    [JsonPropertyName("rights_date_used")]
    public string? RightsDateUsed { get; set; }
    
    [JsonPropertyName("pub_place")]
    public string? PubPlace { get; set; }
    
    [JsonPropertyName("lang")]
    public string? Lang { get; set; }
    
    [JsonPropertyName("bib_fmt")]
    public string? BibFmt { get; set; }
    
    [JsonPropertyName("collection_code")]
    public string? CollectionCode { get; set; }
    
    [JsonPropertyName("content_provider_code")]
    public string? ContentProviderCode { get; set; }
    
    [JsonPropertyName("responsible_entity_code")]
    public string? ResponsibleEntityCode { get; set; }
    
    [JsonPropertyName("digitization_agent_code")]
    public string? DigitizationAgentCode { get; set; }
    
    [JsonPropertyName("access_profile_code")]
    public string? AccessProfileCode { get; set; }
    
    [JsonPropertyName("author")]
    public string? Author { get; set; }
}
