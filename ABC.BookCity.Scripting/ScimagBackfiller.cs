#:package Elastic.Clients.Elasticsearch@8.11.0
#:package MySqlConnector@2.3.5
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using MySqlConnector;

// --- CONFIGURATION ---
var elasticUrl = "http://localhost:9200";
var indexName = "scimag";
var mariaDbConnStr = "Server=localhost;Port=3306;Database=allthethings;Uid=root;Pwd=password;AllowUserVariables=True;Command Timeout=300";
var batchSize = 500;
// ---------------------

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║          Scimag Metadata & Abstract Backfiller               ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var settings = new ElasticsearchClientSettings(new Uri(elasticUrl))
    .DefaultIndex(indexName)
    .DisableDirectStreaming();
var elastic = new ElasticsearchClient(settings);

// 1. Update Mapping to include Abstract and other fields
Console.WriteLine("Updating index mapping...");
var mappingResponse = await elastic.Indices.PutMappingAsync<ScimagRecord>(indexName, m => m
    .Properties(p => p
        .Text(f => f.Abstract)
        .Text(f => f.Title)
        .Text(f => f.Author)
        .IntegerNumber(f => f.Year)
    )
);

if (!mappingResponse.IsValidResponse)
{
    Console.WriteLine($"Error updating mapping: {mappingResponse.DebugInformation}");
    return;
}

// 2. Connect to MariaDB
using var conn = new MySqlConnection(mariaDbConnStr);
await conn.OpenAsync();
Console.WriteLine("Connected to MariaDB.");

// 3. Scroll through Elasticsearch documents that are missing Title or Abstract
Console.WriteLine("Searching for documents to enrich...");

var searchResponse = await elastic.SearchAsync<ScimagRecord>(s => s
    .Index(indexName)
    .Size(batchSize)
    .Query(q => q
        .Bool(b => b
            .MustNot(mn => mn
                .Exists(e => e.Field(f => f.Abstract))
            )
        )
    )
    .Scroll("2m")
);

int totalProcessed = 0;
int totalUpdated = 0;

ScrollId? scrollId = searchResponse.ScrollId;
var currentHits = searchResponse.Hits;

while (searchResponse.IsValidResponse && currentHits.Any())
{
    var recordsToUpdate = new List<ScimagRecord>();

    foreach (var hit in currentHits)
    {
        var record = hit.Source;
        if (record == null) continue;

        totalProcessed++;
        
        // Lookup in MariaDB
        var metadata = await GetMetadataFromDb(conn, record.Doi);
        
        if (metadata != null)
        {
            record.Title = metadata.Title;
            record.Author = metadata.Author;
            record.Year = metadata.Year;
            record.Abstract = metadata.Abstract;
            recordsToUpdate.Add(record);
        }
    }

    if (recordsToUpdate.Any())
    {
        // Use Bulk with Update operations
        var bulkResponse = await elastic.BulkAsync(b => b
            .Index(indexName)
            .UpdateMany(recordsToUpdate, (op, rec) => op.Doc(rec).DocAsUpsert(true))
        );

        if (!bulkResponse.IsValidResponse)
        {
            Console.WriteLine($"Bulk update error: {bulkResponse.DebugInformation}");
        }
        else
        {
            totalUpdated += recordsToUpdate.Count;
        }
    }

    Console.WriteLine($"Processed {totalProcessed}... Updated {totalUpdated} with metadata.");

    // Get next scroll
    var scrollResponse = await elastic.ScrollAsync<ScimagRecord>(s => s.ScrollId(scrollId).Scroll("2m"));
    if (!scrollResponse.IsValidResponse) break;
    
    scrollId = scrollResponse.ScrollId;
    currentHits = scrollResponse.Hits;
}

Console.WriteLine($"\nBackfilling complete! Total processed: {totalProcessed}, Total updated: {totalUpdated}");

async Task<DbMetadata?> GetMetadataFromDb(MySqlConnection connection, string doi)
{
    // We join libgenrs_updated (for metadata) with libgenrs_description (for abstract)
    var sql = @"
        SELECT u.Title, u.Author, u.Year, d.descr as Abstract
        FROM libgenrs_updated u
        LEFT JOIN libgenrs_description d ON u.MD5 = d.md5
        WHERE u.Doi = @doi
        LIMIT 1";

    using var cmd = new MySqlCommand(sql, connection);
    cmd.Parameters.AddWithValue("@doi", doi);

    using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        var meta = new DbMetadata
        {
            Title = reader.IsDBNull(0) ? null : reader.GetString(0),
            Author = reader.IsDBNull(1) ? null : reader.GetString(1),
            Abstract = reader.IsDBNull(3) ? null : reader.GetString(3)
        };
        
        if (!reader.IsDBNull(2))
        {
            if (int.TryParse(reader.GetString(2), out var y)) meta.Year = y;
        }
        
        return meta;
    }

    return null;
}

public class ScimagRecord
{
    public string Id { get; set; }
    public string Doi { get; set; }
    public string ZipFile { get; set; }
    public string InternalPath { get; set; }
    public long Filesize { get; set; }
    public string Title { get; set; }
    public string Author { get; set; }
    public int? Year { get; set; }
    public string Abstract { get; set; }
}

public class DbMetadata
{
    public string Title { get; set; }
    public string Author { get; set; }
    public int? Year { get; set; }
    public string Abstract { get; set; }
}
