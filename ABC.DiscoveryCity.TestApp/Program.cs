using ABC.DiscoveryCity.TestApp.Tests;
using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Domain;
using ABC.DiscoveryCity.TestApp;
using ABC.DiscoveryCity.Embeddings;
using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.Words;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Globalization;

// Parse command-line arguments - accepts multiple DataSet names in order
string[] priorityDataSets = args.Length > 0 ? args : new[] { "DataSet 9" };

// Quick Test Commands
if (args.Length >= 2 && args[0].Equals("test-redaction", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(TelerikBookCorpusIngestionTests.TestRedactionDetection(args[1]));
    return;
}

Console.WriteLine("Discovery City PDF Processor");
Console.WriteLine("============================");
Console.WriteLine($"Priority DataSets: {string.Join(", ", priorityDataSets)}");


// Initialize Embedding Service (Ollama)
Console.WriteLine("Initializing Embedding Service...");
IEmbeddingService? embeddingService = null;
try
{
    embeddingService = new OllamaEmbeddingService();
    // Quick test to verify Ollama is running
    Console.WriteLine($"Embedding Service ready (dimension: {embeddingService.Dimension})");
}
catch (Exception ex)
{
    Console.WriteLine($"WARNING: Embedding service unavailable: {ex.Message}");
    Console.WriteLine("Proceeding without embeddings.");
}

// Initialize DB - MUST succeed before processing
Console.WriteLine("Initializing Database...");
try 
{ 
    new DbService(embeddingService).InitDb(); 
    Console.WriteLine("Database initialized successfully.");
} 
catch (Exception ex) 
{ 
    Console.WriteLine($"FATAL: Database initialization failed: {ex.Message}");
    Console.WriteLine("Cannot proceed without database. Exiting.");
    return;
}

// Initialize Services
using var thumbnailService = new ThumbnailService(); // No logger needed
await thumbnailService.InitializeAsync(headless: false); // Use non-headless for better rendering
var dbService = new DbService(embeddingService);

//await VerifyDocumentData.Run(dbService);
//return;

// VERIFICATION MODE - Check DB and Search
bool RUN_VERIFICATION = false;
if (RUN_VERIFICATION)
{
    Console.WriteLine("\n--- VERIFICATION MODE ---");
    var verifier = new Verification(dbService);
    await verifier.RunVerificationAsync();
    Console.WriteLine("\nVerification complete. Exiting.");
    return;
}

// BATCH TEST LIMIT - set to 0 for unlimited, or a number to limit processing
const int MAX_FILES = 0;
int totalFiles = 0;
int processedFiles = 0; 



string rootFolder = @"S:\EpsteinFiles\DepartmentofJustice\DOJ_Disclosures\"; 

Console.WriteLine($"Default Root Folder: {rootFolder}");

// Find DataSet folders
var subDirs = System.IO.Directory.GetDirectories(rootFolder, "DataSet*", SearchOption.TopDirectoryOnly);
var targetFolders = new List<string>();
var addedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Add priority DataSets in specified order (supports both "DataSet 9" and "DataSet_9" formats)
foreach (var priorityDataSet in priorityDataSets)
{
    string priorityDataSetAlt = priorityDataSet.Replace(" ", "_");
    var priorityFolder = subDirs.FirstOrDefault(d =>
        d.EndsWith(priorityDataSet, StringComparison.OrdinalIgnoreCase) ||
        d.EndsWith(priorityDataSetAlt, StringComparison.OrdinalIgnoreCase));
    if (priorityFolder != null && !addedFolders.Contains(priorityFolder))
    {
        Console.WriteLine($"Queued folder: {priorityFolder}");
        targetFolders.Add(priorityFolder);
        addedFolders.Add(priorityFolder);
    }
}

// Add remaining folders not already queued
foreach (var dir in subDirs)
{
    if (!addedFolders.Contains(dir)) targetFolders.Add(dir);
}

// Fallback to root if no subdirs
if (targetFolders.Count == 0) targetFolders.Add(rootFolder);

// Define Source from path (e.g., "DepartmentofJustice")
string sourceName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(rootFolder.TrimEnd('\\'))) ?? "Unknown";
string baseFilePath = System.IO.Path.GetDirectoryName(rootFolder.TrimEnd('\\')) ?? "";
int sourceId = dbService.GetOrCreateSource(sourceName, baseFilePath);
Console.WriteLine($"Created/Found Source: {sourceName} (Id: {sourceId})");

foreach (var folder in targetFolders)
{
    // Extract DataSet name from folder (e.g., "DataSet_9")
    string dataSetName = System.IO.Path.GetFileName(folder) ?? "Default";
    int dataSetId = dbService.GetOrCreateDataSet(sourceId, dataSetName);
    Console.WriteLine($"Created/Found DataSet: {dataSetName} (Id: {dataSetId}) for Source: {sourceName}");

    Console.WriteLine($"Scanning folder: {folder}");
    var pdfFiles = System.IO.Directory.GetFiles(folder, "*.pdf", SearchOption.AllDirectories);

    // Use Parallel.ForEachAsync to process files concurrently
    int maxDegreeOfParallelism = 5;
    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
    await Parallel.ForEachAsync(pdfFiles, parallelOptions, async (pdfPath, ct) =>
    {
        // Check global processed count loosely
        if (MAX_FILES > 0 && processedFiles >= MAX_FILES) return;

        try 
        {
            // if (pdfPath.Contains(".processed.")) return; 
            
            // Force re-process for updates
            string doneFile = pdfPath + ".done";
            if (System.IO.File.Exists(doneFile)) System.IO.File.Delete(doneFile);

            // Increment atomic counter
            int currentCount = System.Threading.Interlocked.Increment(ref totalFiles);
            Console.WriteLine($"[{currentCount}] Processing: {System.IO.Path.GetFileName(pdfPath)}");
            
            await ProcessPdf(pdfPath, thumbnailService, dataSetId);
            
            // Mark as done
            System.IO.File.Create(doneFile).Dispose();
            
            int currentProcessed = System.Threading.Interlocked.Increment(ref processedFiles);
            
            if (MAX_FILES > 0 && currentProcessed >= MAX_FILES)
            {
                Console.WriteLine($"\n--- BATCH LIMIT REACHED ({MAX_FILES} files) ---");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR processing {pdfPath}: {ex.Message}");
        }
    });

    if (MAX_FILES > 0 && processedFiles >= MAX_FILES) break; // Break outer folder loop
}

Console.WriteLine($"\nDone! Processed {processedFiles}/{totalFiles} files.");

async Task ProcessPdf(string pdfPath, ThumbnailService thumbnailService, int? dataSetId = null, bool inspectMode = false)
{
    // 1. Parse PDF
    (var digitalBook, var telerikDoc) = TelerikBookCorpusIngestionTests.RunParseBook(pdfPath);
    var simpleText = telerikDoc.ToSimpleTextDocument(TimeSpan.FromSeconds(5 * 60));
    string fullText = simpleText.Text;
    
    // Fallback: if simpleText is empty, try constructing from words
    if (string.IsNullOrWhiteSpace(fullText) && digitalBook.Words.Count > 0)
    {
         fullText = string.Join(" ", digitalBook.Words.Select(w => w.text));
    }

    if (inspectMode)
    {
        Console.WriteLine("\n[INSPECT] Simple Text (First 500 chars):");
        Console.WriteLine(fullText.Length > 500 ? fullText.Substring(0, 500) : fullText);
    }

    // 2. Extract Metadata & Deduce Date
    var deducedDate = DeduceDateFromText(fullText);
    var deducedTitle = DeduceTitleFromText(fullText, System.IO.Path.GetFileNameWithoutExtension(pdfPath));
    
    if (inspectMode)
    {
        Console.WriteLine($"\n[INSPECT] Deduced Date: {deducedDate:yyyy-MM-dd}");
        Console.WriteLine($"[INSPECT] Deduced Title: {deducedTitle}");
        return; 
    }

    var metadata = new PdfMetadata
    {
        FileName = System.IO.Path.GetFileName(pdfPath),
        Title = deducedTitle,
        Author = null, 
        Subject = null,
        Keywords = null,
        Producer = null,
        PageCount = telerikDoc.Pages.Count,
        CreationDate = null, 
        DeducedDate = deducedDate,
        Text = RunCleanUp(digitalBook.Sentences.Select(s => s.text).ToList())
    };

    List<string> RunCleanUp(List<string> input)
    {
        var cleaned = new List<string>();
        int i = 1;
        foreach (var line in input)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            // 1. Remove/Replace Unicodes
            string s = line.Replace("\u25A0", "-").Replace("\"", "'");
                        
            // Add newline before sentence number for better readability
            cleaned.Add($"\n[{i++}] {s.Trim()}");
        }
        return cleaned;
    }

    // 3. Serialize to JSON
    string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
    
    // 4. Determine Output Filename
    string dateStr = metadata.DeducedDate?.ToString("yyyy-MM-dd") ?? "UnknownDate";
    string newFileNameBase = $"{System.IO.Path.GetFileNameWithoutExtension(pdfPath)}_{dateStr}";
    
    string jsonPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(pdfPath) ?? "", newFileNameBase + ".json");
    
    System.IO.File.WriteAllText(jsonPath, json);
    Console.WriteLine($"Saved JSON: {jsonPath}");

    // 5. Insert into Postgres
    try 
    {
        dbService.InsertDocument(pdfPath, metadata, dataSetId); 
    }
    catch(Exception ex) 
    {
        Console.WriteLine($"DB Error: {ex.Message}");
    }

    // 6. Generate page images (thumb + full)
    try
    {
        var pageImages = await thumbnailService.GeneratePageImagesAsync(pdfPath);

        // Extract thumb and full from results (empty string + 0 dimensions = skip)
        string thumbPath = ""; int thumbW = 0, thumbH = 0;
        string fullPath = ""; int fullW = 0, fullH = 0;

        foreach (var (filePath, width, height) in pageImages)
        {
            if (filePath.Contains("_thumb."))
                (thumbPath, thumbW, thumbH) = (filePath, width, height);
            else
                (fullPath, fullW, fullH) = (filePath, width, height);
        }

        // Single upsert call - only updates images with W>0 and H>0
        lock (dbService)
        {
            dbService.UpsertDocumentImages(pdfPath, fullPath, fullW, fullH, thumbPath, thumbW, thumbH);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [WARN] Page image error: {ex.Message}");
    }
}


DateTime? DeduceDateFromText(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return null;

    // Limit scope to first 4000 chars for header dates
    string snippet = text.Length > 4000 ? text.Substring(0, 4000) : text;
    
    // 1. Standard Patterns (High Confidence)
    var standardPatterns = new[] 
    {
        @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b",
        @"\b\d{1,2}[/-]\d{1,2}[/-]\d{4}\b",
        @"\b\d{4}-\d{2}-\d{2}\b"
    };

    foreach (var pattern in standardPatterns)
    {
        var match = Regex.Match(snippet, pattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (DateTime.TryParse(match.Value, out DateTime date)) return date;
        }
    }

    // 2. Scrappy OCR Patterns (Medium Confidence)
    // Matches "17-/_ 42-/" -> 17/??/42 -> 1942?
    // Matches "DATE ___17-/_ 42-/"
    // Regex looking for 3 groups of digits separated by "junk" (non-word, non-digit)
    // Junk allowed: - / _ | \ space .
    // e.g. 17 _/_ 05 - 1999
    var scrappyPattern = @"\b(\d{1,2})[^\w\d]{1,5}(\d{1,2})[^\w\d]{1,5}(\d{2,4})\b";
    
    var matchScrappy = Regex.Match(snippet, scrappyPattern);
    if (matchScrappy.Success)
    {
        // Try to parse components
        int p1 = int.Parse(matchScrappy.Groups[1].Value);
        int p2 = int.Parse(matchScrappy.Groups[2].Value);
        int p3 = int.Parse(matchScrappy.Groups[3].Value);

        // Heuristics for Day/Month/Year
        int year = p3;
        if (year < 100) year += (year > 30 ? 1900 : 2000); // e.g. 99 -> 1999, 15 -> 2015
        
        // Month/Day? Assume US format (Month/Day) usually, or check > 12
        int month = p1;
        int day = p2;

        if (month > 12 && day <= 12) 
        {
            // Swap if p1 is definitely not month
            month = p2;
            day = p1;
        }
        
        if (month <= 12 && day <= 31)
        {
            try { return new DateTime(year, month, day); } catch { }
        }
    }

    // 3. Last Resort: Just Year (Low Confidence)
    // "199X" or "20XX" isolated.
    var yearMatch = Regex.Match(snippet, @"\b(19|20)\d{2}\b");
    if (yearMatch.Success)
    {
        return new DateTime(int.Parse(yearMatch.Value), 1, 1);
    }
    
    return null;
}

string DeduceTitleFromText(string text, string filename)
{
    if (string.IsNullOrWhiteSpace(text)) return filename;
    string snippet = text.Length > 2000 ? text.Substring(0, 2000) : text;
    
    // Heuristic: Check for specific document types
    var keywords = new Dictionary<string, string>
    {
        { "IMAGE", "Image" },
        { "MEMORANDUM", "Memorandum" },
        { "REPORT", "Report" },
        { "EMAIL", "Email" },
        { "LETTER", "Letter" },
        { "COURT", "Court Document" },
        { "ORDER", "Court Order" },
        { "MOTION", "Motion" },
        { "SUBPOENA", "Subpoena" },
        { "CASE ID", "Case File" }, 
        { "FBI", "FBI Document" },
        { "TRANSCRIPT", "Transcript" },
        { "AFFIDAVIT", "Affidavit" },
        { "WARRANT", "Warrant" }
    };

    foreach (var kvp in keywords)
    {
        if (snippet.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
        {
             return $"{kvp.Value} - {filename}";
        }
    }
    
    return filename; // Default
}

// Helper extension
public static class StringExtensions
{
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}






