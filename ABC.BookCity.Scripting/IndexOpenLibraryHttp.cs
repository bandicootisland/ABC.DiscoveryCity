#:package MySqlConnector@2.3.7

// Index OpenLibrary editions from ol_base to Elasticsearch using raw HTTP
// Avoids Elastic client which has .NET 10 reflection serialization issues
// Run: dotnet run IndexOpenLibraryHttp.cs

using MySqlConnector;
using System.Text;
using System.Text.Json;

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║    OpenLibrary Elasticsearch Indexer (HTTP)                  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");

var mariaDbConn = "Server=localhost;Port=3306;Database=allthethings;User=root;Password=password;";
var elasticUrl = "http://localhost:9200";
var indexName = "ol_editions";
var batchSize = 2000;

// Setup HTTP client
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

// Check ES connection
try
{
    var ping = await http.GetAsync(elasticUrl);
    if (!ping.IsSuccessStatusCode)
    {
        Console.WriteLine($"ERROR: Cannot connect to Elasticsearch at {elasticUrl}");
        return;
    }
    Console.WriteLine($"Connected to Elasticsearch at {elasticUrl}");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: {ex.Message}");
    return;
}

// Connect to MariaDB
await using var connection = new MySqlConnection(mariaDbConn);
await connection.OpenAsync();
Console.WriteLine("Connected to MariaDB");

// Known count (avoids slow GROUP BY)
long totalRecords = 14245208;
Console.WriteLine($"Total edition records: ~{totalRecords:N0}");

// Check existing count
var countResp = await http.GetStringAsync($"{elasticUrl}/{indexName}/_count");
using var countDoc = JsonDocument.Parse(countResp);
var existingCount = countDoc.RootElement.TryGetProperty("count", out var c) ? c.GetInt64() : 0;
Console.WriteLine($"Existing in Elasticsearch: {existingCount:N0}");

// Delete and recreate?
Console.Write($"Delete and recreate {indexName}? (y/n): ");
if (Console.ReadLine()?.ToLower() == "y")
{
    // Delete
    try { await http.DeleteAsync($"{elasticUrl}/{indexName}"); } catch { }
    
    // Create with settings
    var createBody = """
    {
        "settings": {
            "number_of_shards": 1,
            "number_of_replicas": 0,
            "refresh_interval": "60s"
        },
        "mappings": {
            "properties": {
                "ol_key": { "type": "keyword" },
                "title": { "type": "text", "analyzer": "standard" },
                "subtitle": { "type": "text" },
                "isbn_10": { "type": "keyword" },
                "isbn_13": { "type": "keyword" },
                "lccn": { "type": "keyword" },
                "oclc": { "type": "keyword" },
                "publishers": { "type": "text" },
                "publish_date": { "type": "keyword" },
                "pages": { "type": "integer" },
                "format": { "type": "keyword" },
                "work_key": { "type": "keyword" },
                "author_keys": { "type": "keyword" },
                "subjects": { "type": "text" },
                "cover_id": { "type": "keyword" },
                "ia_id": { "type": "keyword" }
            }
        }
    }
    """;
    
    var createResp = await http.PutAsync($"{elasticUrl}/{indexName}", 
        new StringContent(createBody, Encoding.UTF8, "application/json"));
    
    if (!createResp.IsSuccessStatusCode)
    {
        Console.WriteLine($"ERROR creating index: {await createResp.Content.ReadAsStringAsync()}");
        return;
    }
    Console.WriteLine("Index created");
}

// Index in batches
Console.WriteLine($"Starting indexing with batch size {batchSize}...");
var startTime = DateTime.Now;
long indexed = 0;
long skipped = 0;
string lastKey = "";

while (true)
{
    var bulkBody = new StringBuilder();
    int batchCount = 0;
    
    var sql = @"SELECT ol_key, json FROM ol_base 
                WHERE type = '/type/edition' AND ol_key > @lastKey
                ORDER BY ol_key LIMIT @limit";
    
    await using var cmd = new MySqlCommand(sql, connection);
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
            
            // Build clean document extracting key fields
            var doc = new StringBuilder("{");
            doc.Append($"\"ol_key\":\"{EscapeJson(olKey)}\"");
            
            if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                doc.Append($",\"title\":\"{EscapeJson(title.GetString()!)}\"");
            if (root.TryGetProperty("subtitle", out var sub) && sub.ValueKind == JsonValueKind.String)
                doc.Append($",\"subtitle\":\"{EscapeJson(sub.GetString()!)}\"");
                
            // ISBNs - first value only for simplicity
            if (root.TryGetProperty("isbn_13", out var i13) && i13.ValueKind == JsonValueKind.Array && i13.GetArrayLength() > 0)
                doc.Append($",\"isbn_13\":\"{EscapeJson(i13[0].GetString()!)}\"");
            if (root.TryGetProperty("isbn_10", out var i10) && i10.ValueKind == JsonValueKind.Array && i10.GetArrayLength() > 0)
                doc.Append($",\"isbn_10\":\"{EscapeJson(i10[0].GetString()!)}\"");
            if (root.TryGetProperty("lccn", out var lccn) && lccn.ValueKind == JsonValueKind.Array && lccn.GetArrayLength() > 0)
                doc.Append($",\"lccn\":\"{EscapeJson(lccn[0].GetString()!)}\"");
            if (root.TryGetProperty("oclc_numbers", out var oclc) && oclc.ValueKind == JsonValueKind.Array && oclc.GetArrayLength() > 0)
                doc.Append($",\"oclc\":\"{EscapeJson(oclc[0].GetString()!)}\"");
                
            // Publishers
            if (root.TryGetProperty("publishers", out var pubs) && pubs.ValueKind == JsonValueKind.Array && pubs.GetArrayLength() > 0)
                doc.Append($",\"publishers\":\"{EscapeJson(pubs[0].GetString()!)}\"");
            if (root.TryGetProperty("publish_date", out var pd) && pd.ValueKind == JsonValueKind.String)
                doc.Append($",\"publish_date\":\"{EscapeJson(pd.GetString()!)}\"");
                
            // Pages
            if (root.TryGetProperty("number_of_pages", out var pages) && pages.ValueKind == JsonValueKind.Number)
                doc.Append($",\"pages\":{pages.GetInt32()}");
            if (root.TryGetProperty("physical_format", out var fmt) && fmt.ValueKind == JsonValueKind.String)
                doc.Append($",\"format\":\"{EscapeJson(fmt.GetString()!)}\"");
                
            // Links
            if (root.TryGetProperty("works", out var works) && works.ValueKind == JsonValueKind.Array && works.GetArrayLength() > 0)
            {
                if (works[0].TryGetProperty("key", out var wk))
                    doc.Append($",\"work_key\":\"{EscapeJson(wk.GetString()!)}\"");
            }
            if (root.TryGetProperty("authors", out var auths) && auths.ValueKind == JsonValueKind.Array && auths.GetArrayLength() > 0)
            {
                var authorKeys = auths.EnumerateArray()
                    .Where(a => a.TryGetProperty("key", out _))
                    .Take(5)
                    .Select(a => a.GetProperty("key").GetString())
                    .ToList();
                if (authorKeys.Count > 0)
                    doc.Append($",\"author_keys\":\"{EscapeJson(string.Join(",", authorKeys))}\"");
            }
            
            // Subjects (first few)
            if (root.TryGetProperty("subjects", out var subj) && subj.ValueKind == JsonValueKind.Array)
            {
                var subjects = subj.EnumerateArray().Take(10)
                    .Select(s => s.GetString())
                    .Where(s => s != null);
                doc.Append($",\"subjects\":\"{EscapeJson(string.Join("; ", subjects))}\"");
            }
            
            // Cover and IA
            if (root.TryGetProperty("covers", out var covers) && covers.ValueKind == JsonValueKind.Array && covers.GetArrayLength() > 0)
                doc.Append($",\"cover_id\":\"{covers[0].GetInt64()}\"");
            if (root.TryGetProperty("ocaid", out var ia) && ia.ValueKind == JsonValueKind.String)
                doc.Append($",\"ia_id\":\"{EscapeJson(ia.GetString()!)}\"");
            
            doc.Append("}");
            
            // Bulk action + document
            bulkBody.AppendLine($"{{\"index\":{{\"_id\":\"{EscapeJson(olKey)}\"}}}}");
            bulkBody.AppendLine(doc.ToString());
            batchCount++;
        }
        catch
        {
            skipped++;
        }
    }
    
    if (batchCount == 0) break;
    
    // Send bulk request
    var content = new StringContent(bulkBody.ToString(), Encoding.UTF8, "application/x-ndjson");
    var resp = await http.PostAsync($"{elasticUrl}/{indexName}/_bulk", content);
    
    if (!resp.IsSuccessStatusCode)
    {
        Console.WriteLine($"\nBulk error: {resp.StatusCode}");
        break;
    }
    
    indexed += batchCount;
    
    // Progress
    var elapsed = DateTime.Now - startTime;
    var rate = indexed / Math.Max(1, elapsed.TotalSeconds);
    var eta = TimeSpan.FromSeconds((totalRecords - indexed) / Math.Max(1, rate));
    var pct = indexed * 100.0 / totalRecords;
    
    Console.Write($"\r  Indexed: {indexed:N0} ({pct:F1}%) | {rate:F0}/sec | ETA: {eta:hh\\:mm\\:ss} | Skip: {skipped}    ");
}

// Refresh
Console.WriteLine("\nRefreshing index...");
await http.PostAsync($"{elasticUrl}/{indexName}/_refresh", null);

// Final count
var finalResp = await http.GetStringAsync($"{elasticUrl}/{indexName}/_count");
using var finalDoc = JsonDocument.Parse(finalResp);
var finalCount = finalDoc.RootElement.GetProperty("count").GetInt64();

Console.WriteLine($"\n=== Complete ===");
Console.WriteLine($"Indexed: {finalCount:N0}");
Console.WriteLine($"Skipped: {skipped:N0}");
Console.WriteLine($"Time: {DateTime.Now - startTime:hh\\:mm\\:ss}");

// Test search
Console.WriteLine("\nTesting search for 'harry potter'...");
var searchBody = """{"query":{"match":{"title":"harry potter"}},"size":3}""";
var searchResp = await http.PostAsync($"{elasticUrl}/{indexName}/_search",
    new StringContent(searchBody, Encoding.UTF8, "application/json"));
var searchResult = await searchResp.Content.ReadAsStringAsync();
using var searchDoc = JsonDocument.Parse(searchResult);
var hits = searchDoc.RootElement.GetProperty("hits").GetProperty("hits");
Console.WriteLine($"Found results:");
foreach (var hit in hits.EnumerateArray().Take(5))
{
    var src = hit.GetProperty("_source");
    var t = src.TryGetProperty("title", out var tv) ? tv.GetString() : "?";
    var d = src.TryGetProperty("publish_date", out var dv) ? dv.GetString() : "";
    Console.WriteLine($"  - {t} ({d})");
}

// Helper to escape JSON strings
static string EscapeJson(string s)
{
    return s.Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
}
