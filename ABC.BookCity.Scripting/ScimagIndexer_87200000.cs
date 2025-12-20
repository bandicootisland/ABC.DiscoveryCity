#:package Elastic.Clients.Elasticsearch@8.11.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;

// --- CONFIGURATION ---
var folderPath = Path.Combine(Directory.GetCurrentDirectory(), "87200000");
var elasticUrl = "http://localhost:9200";
var indexName = "scimag";
var batchSize = 1000;
// ---------------------

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
                .Text(f => f.Title)
                .Text(f => f.Author)
                .IntegerNumber(f => f.Year)
            )
        )
        .Settings(s => s.NumberOfReplicas(0).NumberOfShards(1))
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

                batch.Add(new ScimagRecord
                {
                    Id = doi, // Use DOI as ES document ID
                    Doi = doi,
                    ZipFile = zipName,
                    InternalPath = entry.FullName,
                    Filesize = entry.Length
                });

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
}

[JsonSerializable(typeof(ScimagRecord))]
[JsonSerializable(typeof(List<ScimagRecord>))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}
