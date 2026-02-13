using ABC.DiscoveryCity.Embeddings;
using ABC.DiscoveryCity.PostgreSQL;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing;

public class Verification
{
    private readonly DbService _dbService;

    public Verification(DbService dbService)
    {
        _dbService = dbService;
    }

    public async Task RunVerificationAsync()
    {
        Console.WriteLine("\n--- DATABASE VERIFICATION ---");

        // 1. Check Counts
        var (docs, images, sentences) = _dbService.GetCounts();
        Console.WriteLine($"ParentDocuments: {docs}");
        Console.WriteLine($"DocumentImages:  {images}");
        Console.WriteLine($"Sentences:       {sentences}");

        if (docs == 0)
        {
             Console.WriteLine("[WARN] No documents found.");
        }
        else
        {
             Console.WriteLine("[PASS] Database contains data.");
        }

        // 2. Test Search
        Console.WriteLine("\n--- SEARCH TEST ---");
        // Use a query that might actually appear in the documents or just random text
        // The user asked for "obscure text", so let's try that literally + maybe something relevant if I knew the content.
        // But "obscure test" is what they asked for.
        string query = "obscure text"; 
        
        // Also try a more generic one that might hit
        // query = "Department of Justice"; 
        
        Console.WriteLine($"Query: '{query}'");
        
        var results = await _dbService.SearchSimilarAsync(query);
        if (results.Count == 0)
        {
             Console.WriteLine("[WARN] No search results returned.");
        }
        else
        {
             foreach (var result in results)
             {
                 Console.WriteLine($"Desc: {result.Distance:F4} | File: {result.FileName}");
                 string preview = result.Text.Length > 100 ? result.Text.Substring(0, 100) + "..." : result.Text;
                 Console.WriteLine($"   Preview: {preview}\n");
             }
             Console.WriteLine("[PASS] Search returned results.");
        }
    }
}
