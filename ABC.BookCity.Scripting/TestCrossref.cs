using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

var doi = "10.1021/acs.chemmater.0c04689";
var crossrefUrl = $"https://api.crossref.org/works/{doi}";
var semanticScholarUrl = $"https://api.semanticscholar.org/graph/v1/paper/DOI:{doi}?fields=title,abstract,authors,year";

using var client = new HttpClient();
client.DefaultRequestHeaders.Add("User-Agent", "ABC.BookCity/1.0");

Console.WriteLine($"--- CROSSREF ---");
try
{
    var response = await client.GetStringAsync(crossrefUrl);
    using var doc = JsonDocument.Parse(response);
    var message = doc.RootElement.GetProperty("message");
    var title = message.TryGetProperty("title", out var t) && t.GetArrayLength() > 0 ? t[0].GetString() : "N/A";
    var abstractText = message.TryGetProperty("abstract", out var a) ? a.GetString() : "N/A";
    Console.WriteLine($"Title: {title}");
    Console.WriteLine($"Abstract: {(string.IsNullOrEmpty(abstractText) ? "N/A" : abstractText.Substring(0, Math.Min(100, abstractText.Length)) + "...")}");
}
catch (Exception ex) { Console.WriteLine($"Crossref Error: {ex.Message}"); }

Console.WriteLine($"\n--- SEMANTIC SCHOLAR ---");
try
{
    var response = await client.GetStringAsync(semanticScholarUrl);
    using var doc = JsonDocument.Parse(response);
    var root = doc.RootElement;
    var title = root.TryGetProperty("title", out var t) ? t.GetString() : "N/A";
    var abstractText = root.TryGetProperty("abstract", out var a) ? a.GetString() : "N/A";
    Console.WriteLine($"Title: {title}");
    Console.WriteLine($"Abstract: {(string.IsNullOrEmpty(abstractText) ? "N/A" : abstractText.Substring(0, Math.Min(200, abstractText.Length)) + "...")}");
}
catch (Exception ex) { Console.WriteLine($"Semantic Scholar Error: {ex.Message}"); }
