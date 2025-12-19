#:package Elastic.Clients.Elasticsearch@8.11.0
#:package MySqlConnector@2.3.5
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using MySqlConnector;

// --- CONFIGURATION ---
var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "87500000");
var elasticUrl = "http://localhost:9200";
var apiUrl = "http://localhost:5022/api/metadata/extract-abstract";
var indexName = "scimag";
var mariaDbConnStr = "Server=localhost;Port=3306;Database=allthethings;Uid=root;Pwd=password;AllowUserVariables=True;Command Timeout=300";
var batchSize = 100; 
// ---------------------

using var httpClient = new HttpClient();
httpClient.Timeout = TimeSpan.FromSeconds(30);

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine($"║    Scimag Elasticsearch Indexer - {Path.GetFileName(folderPath)} ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

if (!Directory.Exists(folderPath))
{
    Console.WriteLine($"ERROR: Folder not found: {folderPath}");
    return;
}

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

// Connect to MariaDB
using var conn = new MySqlConnection(mariaDbConnStr);
try {
    await conn.OpenAsync();
    Console.WriteLine("Connected to MariaDB.");
} catch (Exception ex) {
    Console.WriteLine($"WARNING: Could not connect to MariaDB: {ex.Message}. Indexing will continue without metadata enrichment.");
}

// Create index if not exists
var existsResponse = await elastic.Indices.ExistsAsync(indexName);
if (!existsResponse.Exists)
{
    Console.WriteLine($"Creating index '{indexName}'...");
    await elastic.Indices.CreateAsync(indexName, c => c
        .Mappings(m => m
            .Properties<ScimagRecord>(p => p
                .Keyword(f => f.Doi)
                .Keyword(f => f.ZipFile)
                .Keyword(f => f.InternalPath)
                .LongNumber(f => f.Filesize)
                .Text(f => f.Title!)
                .Text(f => f.Author!)
                .IntegerNumber(f => f.Year!)
                .Text(f => f.Abstract!)
            )
        )
        .Settings(s => s.NumberOfReplicas(0).NumberOfShards(1))
    );
}
else
{
    // Update mapping to ensure Abstract exists
    await elastic.Indices.PutMappingAsync<ScimagRecord>(indexName, m => m
        .Properties(p => p.Text(f => f.Abstract!))
    );
}

var zipFiles = Directory.GetFiles(folderPath, "*.zip").OrderBy(f => f).ToList();
Console.WriteLine($"Found {zipFiles.Count} zip files to process.");

int totalIndexed = 0;
var batch = new List<ScimagRecord>();

foreach (var zipPath in zipFiles)
{
    var zipName = Path.GetFileName(zipPath);
    Console.WriteLine($"Processing {zipName}...");
    
    try
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                // Extract DOI from path (e.g. 10.1021/abc.pdf -> 10.1021/abc)
                var doi = entry.FullName;
                if (doi.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    doi = doi.Substring(0, doi.Length - 4);

                var record = new ScimagRecord
                {
                    Id = doi, // Use DOI as ES document ID
                    Doi = doi,
                    ZipFile = zipName,
                    InternalPath = entry.FullName,
                    Filesize = entry.Length
                };

                // Attempt to enrich from MariaDB if connected
                if (conn.State == System.Data.ConnectionState.Open)
                {
                    var metadata = await GetMetadataFromDb(conn, doi);
                    if (metadata != null)
                    {
                        record.Title = metadata.Title;
                        record.Author = metadata.Author;
                        record.Year = metadata.Year;
                        record.Abstract = metadata.Abstract;
                    }
                }

                // If abstract is still missing, try extracting from PDF via API
                if (string.IsNullOrEmpty(record.Abstract))
                {
                    try 
                    {
                        using var entryStream = entry.Open();
                        using var ms = new MemoryStream();
                        await entryStream.CopyToAsync(ms);
                        ms.Position = 0;

                        var content = new StreamContent(ms);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                        
                        var response = await httpClient.PostAsync(apiUrl, content);                        
                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            using var doc = JsonDocument.Parse(json);
                            record.Abstract = doc.RootElement.GetProperty("abstractText").GetString() ?? "";
                            
                            if (!string.IsNullOrEmpty(record.Abstract) && !record.Abstract.StartsWith("["))
                            {
                                Console.WriteLine($"    [Extracted Abstract for {doi}]");
                            }
                            else if (record.Abstract.StartsWith("["))
                            {
                                Console.WriteLine($"    [Extraction Note for {doi}: {record.Abstract}]");
                            }
                        }
                        else
                        {
                            var error = await response.Content.ReadAsStringAsync();
                            Console.WriteLine($"    [API Error for {doi}: {response.StatusCode} - {error}]");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    [Extraction Exception for {doi}: {ex.Message}]");
                    }
                }

                batch.Add(record);

                if (batch.Count >= batchSize)
                {
                    await IndexBatch(elastic, indexName, batch);
                    totalIndexed += batch.Count;
                    batch.Clear();
                    Console.WriteLine($"  Indexed {totalIndexed} records...");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Error reading {zipName}: {ex.Message}");
    }
}

if (batch.Count > 0)
{
    await IndexBatch(elastic, indexName, batch);
    totalIndexed += batch.Count;
    Console.WriteLine($"  Final batch indexed. Total: {totalIndexed}");
}

Console.WriteLine("\nIndexing complete!");

async Task IndexBatch(ElasticsearchClient client, string idx, List<ScimagRecord> records)
{
    var response = await client.BulkAsync(b => b
        .Index(idx)
        .IndexMany(records, (op, rec) => op.Id(rec.Id))
    );

    if (!response.IsValidResponse)
    {
        Console.WriteLine($"  Bulk Error: {response.DebugInformation}");
    }
}

async Task<DbMetadata?> GetMetadataFromDb(MySqlConnection connection, string doi)
{
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
            Title = reader.IsDBNull(0) ? "" : reader.GetString(0),
            Author = reader.IsDBNull(1) ? "" : reader.GetString(1),
            Abstract = reader.IsDBNull(3) ? "" : reader.GetString(3)
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
    public required string Id { get; set; }
    public required string Doi { get; set; }
    public required string ZipFile { get; set; }
    public required string InternalPath { get; set; }
    public long Filesize { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public int? Year { get; set; }
    public string? Abstract { get; set; }
}

public class DbMetadata
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public int? Year { get; set; }
    public string? Abstract { get; set; }
}
