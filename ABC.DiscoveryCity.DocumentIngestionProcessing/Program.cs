using ABC.DiscoveryCity.DocumentIngestionProcessing.Tests;
using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Domain;
using ABC.DiscoveryCity.DocumentIngestionProcessing;
using ABC.DiscoveryCity.Embeddings;
using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.Words;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Globalization;

// Parse command-line arguments
bool resetDb = args.Any(a => a.Equals("--reset-db", StringComparison.OrdinalIgnoreCase));
bool cleanFiles = args.Any(a => a.Equals("--clean", StringComparison.OrdinalIgnoreCase));
bool headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));
bool usePlaywright = args.Any(a => a.Equals("--use-playwright", StringComparison.OrdinalIgnoreCase));
bool renderDirect = !usePlaywright && !args.Any(a => a.Equals("--no-images", StringComparison.OrdinalIgnoreCase)); // render-direct is now the default
bool noImages = args.Any(a => a.Equals("--no-images", StringComparison.OrdinalIgnoreCase));
bool embeddingsOnly = args.Any(a => a.Equals("--embeddings-only", StringComparison.OrdinalIgnoreCase));
bool imagesOnly = args.Any(a => a.Equals("--images-only", StringComparison.OrdinalIgnoreCase));
bool forceReprocess = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
bool reprocessMode = args.Any(a => a.Equals("--reprocess", StringComparison.OrdinalIgnoreCase));
bool extractNamesLlm = args.Any(a => a.Equals("--extract-names-llm", StringComparison.OrdinalIgnoreCase));
int limitFiles = 0;
string? folderArg = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        int.TryParse(args[i + 1], out limitFiles);
    }
    if (args[i].Equals("--folder", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        folderArg = args[i + 1];
    }
}

// Resolve root folder: --folder arg > environment variable > default
string rootFolder = folderArg
    ?? Environment.GetEnvironmentVariable("DISCOVERYCITY_ROOT_FOLDER")
    ?? "/media/stephen/18TB/EpsteinFiles/DepartmentofJustice/DOJ_Disclosures/";

// Ensure trailing separator
if (!rootFolder.EndsWith(Path.DirectorySeparatorChar) && !rootFolder.EndsWith(Path.AltDirectorySeparatorChar))
    rootFolder += Path.DirectorySeparatorChar;

// Filter out --folder value and --limit value from positional args
var skipArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "--folder", "--limit" };
var positionalArgs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (skipArgs.Contains(args[i]) && i + 1 < args.Length) { i++; continue; } // skip flag + value
    if (!args[i].StartsWith("--") && !int.TryParse(args[i], out _))
        positionalArgs.Add(args[i]);
}
string[] priorityDataSets = positionalArgs.Count > 0 ? positionalArgs.ToArray() : new[] { "DataSet 11" };

// Quick diagnostic: dump ImageSource internals for a PDF
if (args.Any(a => a.Equals("--diag-image", StringComparison.OrdinalIgnoreCase)))
{
    string diagPdf = args.SkipWhile(a => !a.Equals("--diag-image", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault()
        ?? "/media/stephen/18TB/EpsteinFiles/DepartmentofJustice/DOJ_Disclosures/DataSet 10/PDFs/EFTA01302373.pdf";
    var ext = new PdfImageExtractor();
    ext.DiagnoseDump(diagPdf);
    return;
}

// Quick Test Commands
if (args.Length >= 2 && args[0].Equals("test-redaction", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(TelerikBookCorpusIngestionTests.TestRedactionDetection(args[1]));
    return;
}

if (args.Any(a => a.Equals("--stats", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("Checking Database Stats...");
    try 
    {
        var stats = new DbService(null).GetSystemStats();
        Console.WriteLine($"\n=== DATABASE STATE ===");
        Console.WriteLine($"Documents: {stats.TotalDocuments}");
        Console.WriteLine($"Images:    {stats.TotalImages}");
        Console.WriteLine($"Sentences: {stats.TotalSentences}");
        Console.WriteLine("======================\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n[ERROR] Database check failed: {ex.Message}");
        Console.WriteLine("(Tables likely do not exist or connection failed)\n");
    }
    return;
}

Console.WriteLine("Discovery City PDF Processor");
Console.WriteLine("============================");
Console.WriteLine($"Priority DataSets: {string.Join(", ", priorityDataSets)}");
if (embeddingsOnly) Console.WriteLine("MODE: Embeddings-only (backfill NULL embeddings)");
if (imagesOnly) Console.WriteLine("MODE: Images-only (generate thumbnails for files without .done.images)");
if (reprocessMode) Console.WriteLine("MODE: Reprocess (re-extract metadata from stored text, no PDF re-parsing)");;
if (forceReprocess) Console.WriteLine("MODE: Force reprocess (ignore .done flags)");
if (extractNamesLlm) Console.WriteLine("MODE: LLM Names extraction (using Ollama) — NOT YET IMPLEMENTED");

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
    if (resetDb)
    {
        Console.WriteLine("WARNING: --reset-db flag is DISABLED for safety. Skipping.");
    }
    
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
ThumbnailService? thumbnailService = null;
PdfImageExtractor? pdfImageExtractor = null;

if (noImages)
{
    Console.WriteLine("Skipping image generation (--no-images flag)");
}
else if (usePlaywright)
{
    Console.WriteLine("Using Playwright browser for thumbnails (--use-playwright)");
    thumbnailService = new ThumbnailService();
    await thumbnailService.InitializeAsync(headless: headless, instancecount: 10);
}
else
{
    Console.WriteLine("Using Telerik direct image extraction for thumbnails (default, ~5x faster)");
    pdfImageExtractor = new PdfImageExtractor();
}
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

// ============================================================
// REPROCESS MODE: Re-extract metadata from stored text (no PDF re-parsing)
// ============================================================
if (reprocessMode)
{
    Console.WriteLine("\n--- REPROCESS MODE ---");
    Console.WriteLine("Reading documents from DB and re-extracting metadata...");

    int batchSize = 500;
    int totalUpdated = 0;
    int totalSkipped = 0;
    int totalErrors = 0;
    int offset = 0;
    int batchLimit = limitFiles > 0 ? limitFiles : int.MaxValue;

    while (offset < batchLimit)
    {
        int fetchSize = Math.Min(batchSize, batchLimit - offset);
        var docs = dbService.GetDocumentsForReprocessing(fetchSize, offset);
        if (docs.Count == 0) break;

        Console.WriteLine($"  Batch: {docs.Count} documents (offset: {offset})");

        foreach (var (docId, filePath, metadataJson) in docs)
        {
            try
            {
                // Reconstruct text from chunks for names extraction
                string fullText = dbService.GetDocumentFullText(docId);
                if (string.IsNullOrWhiteSpace(fullText))
                {
                    totalSkipped++;
                    continue;
                }

                // --- Reprocess steps (add future extractions here) ---
                var extractedNames = ExtractNamesFromText(fullText);

                // Merge into existing metadata
                var metadata = string.IsNullOrWhiteSpace(metadataJson)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson)
                      ?? new Dictionary<string, JsonElement>();

                // Update Names field
                if (extractedNames.Count > 0)
                    metadata["Names"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(extractedNames));
                else
                    metadata.Remove("Names");

                // Write back updated metadata
                string updatedJson = JsonSerializer.Serialize(metadata);
                dbService.UpdateDocumentMetadata(docId, updatedJson);
                totalUpdated++;

                if (totalUpdated % 1000 == 0)
                    Console.WriteLine($"    [{totalUpdated}] documents reprocessed...");
            }
            catch (Exception ex)
            {
                totalErrors++;
                if (totalErrors <= 10)
                    Console.WriteLine($"    [ERROR] Doc {docId} ({System.IO.Path.GetFileName(filePath)}): {ex.Message}");
            }
        }

        offset += docs.Count;
    }

    Console.WriteLine($"\nReprocess complete! Updated: {totalUpdated}, Skipped: {totalSkipped}, Errors: {totalErrors}");
    return;
}

// ============================================================
// EMBEDDINGS-ONLY MODE: Backfill NULL embeddings from DB
// ============================================================
if (embeddingsOnly)
{
    if (embeddingService == null)
    {
        Console.WriteLine("FATAL: --embeddings-only requires a running embedding service (Ollama).");
        return;
    }

    long totalMissing = dbService.CountDocsWithoutEmbeddings();
    Console.WriteLine($"Documents without embeddings: {totalMissing}");
    if (totalMissing == 0) { Console.WriteLine("Nothing to do."); return; }

    int batchSize = 500;
    int totalUpdated = 0;
    int totalErrors = 0;
    int batchLimit = limitFiles > 0 ? limitFiles : int.MaxValue;

    while (totalUpdated < batchLimit)
    {
        var docs = dbService.GetDocsWithoutEmbeddings(Math.Min(batchSize, batchLimit - totalUpdated));
        if (docs.Count == 0) break;

        Console.WriteLine($"  Batch: {docs.Count} documents (total updated so far: {totalUpdated}/{totalMissing})");

        // Process in parallel (5 concurrent)
        await Parallel.ForEachAsync(docs, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (doc, ct) =>
        {
            try
            {
                var embedding = await embeddingService.GetEmbeddingAsync(doc.SentencesText);
                if (dbService.UpdateDocumentEmbedding(doc.DocId, embedding))
                {
                    int count = System.Threading.Interlocked.Increment(ref totalUpdated);
                    if (count % 100 == 0)
                        Console.WriteLine($"    [{count}/{totalMissing}] embeddings updated...");
                }
            }
            catch (Exception ex)
            {
                System.Threading.Interlocked.Increment(ref totalErrors);
                if (totalErrors <= 5)
                    Console.WriteLine($"    [WARN] Embedding error for doc {doc.DocId}: {ex.Message}");
            }
        });
    }

    Console.WriteLine($"\nEmbeddings complete! Updated: {totalUpdated}, Errors: {totalErrors}");
    return;
}

// ============================================================
// IMAGES-ONLY MODE: Generate thumbnails for files without .done.images
// ============================================================
if (imagesOnly)
{
    Console.WriteLine("\n--- IMAGES-ONLY MODE ---");
    
    // Initialize thumbnail service
    ThumbnailService imgThumbnailService = new ThumbnailService();
    await imgThumbnailService.InitializeAsync(headless: headless, instancecount: 10);
    var imgDbService = new DbService(embeddingService);

    int MAX_IMG_FILES = limitFiles;
    int imgProcessed = 0;
    int imgSkipped = 0;
    int imgErrors = 0;
    int imgTotal = 0;
    var imgPendingTasks = new System.Collections.Concurrent.ConcurrentBag<Task>();

    string imgRootFolder = rootFolder;
    var imgSubDirs = System.IO.Directory.GetDirectories(imgRootFolder, "DataSet*", SearchOption.TopDirectoryOnly);
    var imgTargetFolders = new List<string>();
    var imgAddedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Add priority DataSets first
    foreach (var ds in priorityDataSets)
    {
        string dsAlt = ds.Replace(" ", "_");
        var match = imgSubDirs.FirstOrDefault(d =>
            d.EndsWith(ds, StringComparison.OrdinalIgnoreCase) ||
            d.EndsWith(dsAlt, StringComparison.OrdinalIgnoreCase));
        if (match != null && imgAddedFolders.Add(match))
            imgTargetFolders.Add(match);
    }
    foreach (var dir in imgSubDirs)
    {
        if (!imgAddedFolders.Contains(dir)) imgTargetFolders.Add(dir);
    }
    if (imgTargetFolders.Count == 0) imgTargetFolders.Add(imgRootFolder);

    foreach (var folder in imgTargetFolders)
    {
        Console.WriteLine($"Scanning for images: {folder}");
        var pdfFiles = System.IO.Directory.GetFiles(folder, "*.pdf", SearchOption.AllDirectories)
            .Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + "Published" + System.IO.Path.DirectorySeparatorChar))
            .ToArray();

        foreach (var pdfPath in pdfFiles)
        {
            if (MAX_IMG_FILES > 0 && imgProcessed >= MAX_IMG_FILES) break;

            string doneImages = pdfPath + ".done.images";

            // Skip if images already generated (unless --force)
            if (!forceReprocess && System.IO.File.Exists(doneImages))
            {
                imgSkipped++;
                continue;
            }

            imgTotal++;
            int current = ++imgProcessed;
            if (current % 100 == 0 || current <= 5)
                Console.WriteLine($"  [{current}] Image: {System.IO.Path.GetFileName(pdfPath)}");

            // Fire image task
            var imageTask = Task.Run(async () =>
            {
                try
                {
                    var pageImages = await imgThumbnailService.GeneratePageImagesAsync(pdfPath);

                    string thumbPath = ""; int thumbW = 0, thumbH = 0;
                    string fullPath = ""; int fullW = 0, fullH = 0;
                    byte[] previewData = Array.Empty<byte>(); byte[] thumbData = Array.Empty<byte>();

                    foreach (var (filePath, width, height, imgData) in pageImages)
                    {
                        if (filePath.Contains("_thumb."))
                        {
                            (thumbPath, thumbW, thumbH) = (filePath, width, height);
                            thumbData = imgData;
                        }
                        else
                        {
                            (fullPath, fullW, fullH) = (filePath, width, height);
                            previewData = imgData;
                        }
                    }

                    if (!string.IsNullOrEmpty(thumbPath) || !string.IsNullOrEmpty(fullPath))
                    {
                        lock (imgDbService)
                        {
                            imgDbService.UpsertDocumentImages(pdfPath, fullPath, fullW, fullH, thumbPath, thumbW, thumbH,
                                previewData: previewData, thumbData: thumbData);
                        }
                        // Mark images as done
                        System.IO.File.Create(doneImages).Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref imgErrors);
                    Console.WriteLine($"  [WARN] Image error for {System.IO.Path.GetFileName(pdfPath)}: {ex.Message}");
                }
            });
            imgPendingTasks.Add(imageTask);
        }

        if (MAX_IMG_FILES > 0 && imgProcessed >= MAX_IMG_FILES) break;
    }

    // Wait for all image tasks
    if (imgPendingTasks.Count > 0)
    {
        Console.WriteLine($"Waiting for {imgPendingTasks.Count} image tasks to complete...");
        await Task.WhenAll(imgPendingTasks);
    }

    await imgThumbnailService.DisposeAsync();
    Console.WriteLine($"\nImages-only complete! Processed: {imgProcessed}, Skipped: {imgSkipped}, Errors: {imgErrors}");
    return;
}

// BATCH TEST LIMIT - set to 0 for unlimited, or a number to limit processing
int MAX_FILES = limitFiles;
int totalFiles = 0;
int startedFiles = 0;   // Atomically claimed BEFORE work begins (for accurate limit enforcement)
int processedFiles = 0;  // Incremented AFTER work completes
int skippedFiles = 0;
var pendingImageTasks = new System.Collections.Concurrent.ConcurrentBag<Task>(); 

Console.WriteLine($"Root Folder: {rootFolder}");

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

// Add remaining folders not already queued (natural numeric sort so DataSet 9 < DataSet 10)
foreach (var dir in subDirs
    .OrderBy(d => Regex.Replace(System.IO.Path.GetFileName(d) ?? "", @"\d+", m => m.Value.PadLeft(10, '0'))))
{
    if (!addedFolders.Contains(dir)) targetFolders.Add(dir);
}

// Fallback to root if no subdirs
if (targetFolders.Count == 0) targetFolders.Add(rootFolder);

// Define Source from path (e.g., "DepartmentofJustice")
string sourceName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(rootFolder.TrimEnd(System.IO.Path.DirectorySeparatorChar))) ?? "Unknown";
string baseFilePath = System.IO.Path.GetDirectoryName(rootFolder.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? "";
int sourceId = dbService.GetOrCreateSource(sourceName, baseFilePath);
Console.WriteLine($"Created/Found Source: {sourceName} (Id: {sourceId})");

foreach (var folder in targetFolders)
{
    // Extract DataSet name from folder (e.g., "DataSet_9")
    string dataSetName = System.IO.Path.GetFileName(folder) ?? "Default";
    // Published folder: standardised output location for all processed files
    // Relative path is "DataSet N/Published/" — identical on Windows and Linux
    string publishedRelFolder = dataSetName + "/Published/";
    string publishedDir = System.IO.Path.Combine(folder, "Published");
    int dataSetId = dbService.GetOrCreateDataSet(sourceId, dataSetName, publishedRelFolder);
    Console.WriteLine($"Created/Found DataSet: {dataSetName} (Id: {dataSetId}) for Source: {sourceName}");
    Console.WriteLine($"  Published folder: {publishedDir}");

    Console.WriteLine($"Scanning folder: {folder}");

    // Clean up generated files if --clean flag is set
    if (cleanFiles)
    {
        CleanGeneratedFiles(folder);
    }

    // Scan for source PDFs, excluding the Published output directory
    var pdfFiles = System.IO.Directory.GetFiles(folder, "*.pdf", SearchOption.AllDirectories)
        .Where(f => !f.Contains(System.IO.Path.DirectorySeparatorChar + "Published" + System.IO.Path.DirectorySeparatorChar))
        .ToArray();

    // Use Parallel.ForEachAsync to process files concurrently
    int maxDegreeOfParallelism = 5;
    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
    await Parallel.ForEachAsync(pdfFiles, parallelOptions, async (pdfPath, ct) =>
    {
        // Atomically claim a slot before doing any work — prevents over-processing
        if (MAX_FILES > 0 && System.Threading.Interlocked.Increment(ref startedFiles) > MAX_FILES) return;

        try 
        {
            // Flag file paths
            string doneFile = pdfPath + ".done";              // PDF parsed + text saved to DB
            string doneEmbeddings = pdfPath + ".done.embeddings"; // Embeddings generated
            string doneImages = pdfPath + ".done.images";        // Images generated

            // Skip if already fully processed (unless --force)
            if (!forceReprocess && System.IO.File.Exists(doneFile))
            {
                System.Threading.Interlocked.Increment(ref skippedFiles);
                return;
            }

            // Increment atomic counter
            int currentCount = System.Threading.Interlocked.Increment(ref totalFiles);
            Console.WriteLine($"[{currentCount}] Processing: {System.IO.Path.GetFileName(pdfPath)}");
            
            await ProcessPdf(pdfPath, thumbnailService, pdfImageExtractor, dataSetId, publishedDir: publishedDir, dataSetName: dataSetName, sourceFolderName: sourceName);
            
            // Mark text extraction as done
            System.IO.File.Create(doneFile).Dispose();

            // Mark embeddings done if embedding service was available and succeeded
            if (embeddingService != null)
            {
                System.IO.File.Create(doneEmbeddings).Dispose();
            }

            // Mark images done if images were generated
            if (!noImages)
            {
                System.IO.File.Create(doneImages).Dispose();
            }
            
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

Console.WriteLine($"\nDone! Processed {processedFiles}/{totalFiles} files. Skipped {skippedFiles} already-done.");

// Wait for all background image tasks to complete
if (pendingImageTasks.Count > 0)
{
    Console.WriteLine($"Waiting for {pendingImageTasks.Count} image tasks to complete...");
    await Task.WhenAll(pendingImageTasks);
    Console.WriteLine("All image tasks completed.");
}

async Task ProcessPdf(string pdfPath, ThumbnailService? thumbnailService, PdfImageExtractor? pdfImageExtractor, int? dataSetId = null, bool inspectMode = false, string? publishedDir = null, string? dataSetName = null, string? sourceFolderName = null)
{
    // Skip if already processed (for distributed processing)
    if (dbService.DocumentExists(pdfPath))
    {
        Console.WriteLine($"  [SKIP] Already in DB: {Path.GetFileName(pdfPath)}");
        return;
    }

    // 1. Parse PDF
    (var digitalBook, var telerikDoc) = TelerikBookCorpusIngestionTests.RunParseBook(pdfPath);
    var simpleText = telerikDoc.ToSimpleTextDocument(TimeSpan.FromSeconds(5 * 60));
    string fullText = simpleText.Text;
    
    // Fallback: if simpleText is empty, try constructing from words
    if (string.IsNullOrWhiteSpace(fullText) && digitalBook.Words.Count > 0)
    {
         fullText = string.Join(" ", digitalBook.Words.Select(w => w.text));
    }

    // Ensure Published output directory exists
    if (!string.IsNullOrEmpty(publishedDir) && !System.IO.Directory.Exists(publishedDir))
        System.IO.Directory.CreateDirectory(publishedDir);

    // NOTE: Do NOT clean fullText — raw OCR text is evidence and must be preserved exactly.
    // MIME artifacts (= replacing characters) are handled as a matching problem in people extraction.

    if (inspectMode)
    {
        Console.WriteLine("\n[INSPECT] Simple Text (First 500 chars):");
        Console.WriteLine(fullText.Length > 500 ? fullText.Substring(0, 500) : fullText);
    }

    // 2. Extract Metadata & Deduce Date
    var deducedDate = DeduceDateFromText(fullText);
    var deducedTitle = DeduceTitleFromText(fullText, System.IO.Path.GetFileNameWithoutExtension(pdfPath));
    var extractedNames = ExtractNamesFromText(fullText);
    
    if (inspectMode)
    {
        Console.WriteLine($"\n[INSPECT] Deduced Date: {deducedDate:yyyy-MM-dd}");
        Console.WriteLine($"[INSPECT] Deduced Title: {deducedTitle}");
        Console.WriteLine($"[INSPECT] Names: {string.Join(", ", extractedNames)}");
        return; 
    }

    // Gather file-level info for enriched metadata
    var fileInfo = new System.IO.FileInfo(pdfPath);
    int wordCount = string.IsNullOrWhiteSpace(fullText) ? 0 : fullText.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries).Length;
    // Source subfolder relative to root (e.g., "VOL00008/IMAGES/0001")
    string? sourceSubFolder = null;
    try
    {
        var pdfDir = System.IO.Path.GetDirectoryName(pdfPath);
        if (pdfDir != null && publishedDir != null)
        {
            // Strip from the dataset folder down, exclude Published
            var dataSetDir = System.IO.Path.GetDirectoryName(publishedDir.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (dataSetDir != null && pdfDir.StartsWith(dataSetDir))
                sourceSubFolder = pdfDir.Substring(dataSetDir.Length).TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }
    }
    catch { }

    var metadata = new PdfMetadata
    {
        FileName = System.IO.Path.GetFileName(pdfPath),
        Title = deducedTitle,
        PageCount = telerikDoc.Pages.Count,
        DeducedDate = deducedDate ?? DateTime.MinValue,
        Text = RunCleanUp(digitalBook.Sentences.Select(s => s.text).ToList()),
        Names = extractedNames,
        // Enriched metadata
        DataSetName = dataSetName ?? "",
        SourceName = sourceFolderName ?? "",
        OriginalFilePath = pdfPath,
        SourceFolder = sourceSubFolder ?? "",
        IngestedAtUtc = DateTime.UtcNow,
        FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
        WordCount = wordCount
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

            // NOTE: Do NOT clean MIME artifacts from sentences — raw text is evidence.

            // 2. Add newline before EFTA file IDs (e.g., EFTA00039885)
            s = Regex.Replace(s, @"(EFTA\d{8,})", "\n$1");

            // Add newline before sentence number for better readability
            cleaned.Add($"\n[{i++}] {s.Trim()}");
        }
        return cleaned;
    }

    // 3. Serialize to JSON
    string json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
    
    // 4. Determine Output Filename & Published output location
    string dateStr = metadata.DeducedDate != DateTime.MinValue ? metadata.DeducedDate.ToString("yyyy-MM-dd") : "UnknownDate";
    string newFileNameBase = $"{System.IO.Path.GetFileNameWithoutExtension(pdfPath)}_{dateStr}";
    string outputDir = publishedDir ?? System.IO.Path.GetDirectoryName(pdfPath) ?? "";
    
    // Copy source PDF to Published folder (if Published dir is set and different from source)
    string publishedPdfPath = pdfPath; // default: original location
    if (!string.IsNullOrEmpty(publishedDir))
    {
        publishedPdfPath = System.IO.Path.Combine(publishedDir, System.IO.Path.GetFileName(pdfPath));
        if (!System.IO.File.Exists(publishedPdfPath))
        {
            System.IO.File.Copy(pdfPath, publishedPdfPath);
            Console.WriteLine($"  Copied PDF → Published: {System.IO.Path.GetFileName(pdfPath)}");
        }
    }
    
    string jsonPath = System.IO.Path.Combine(outputDir, newFileNameBase + ".json");
    System.IO.File.WriteAllText(jsonPath, json);
    Console.WriteLine($"Saved JSON: {jsonPath}");

    // 5. Insert into Postgres — store the Published path (lock for thread-safety with parallel processing)
    try
    {
        lock (dbService)
        {
            dbService.InsertDocument(publishedPdfPath, metadata, dataSetId);
        }
    }
    catch(Exception ex)
    {
        Console.WriteLine($"DB Error: {ex.Message}");
    }

    // 6. Generate page images (thumb + full) - skip if --no-images flag is set
    //    Images go to Published folder when set, otherwise alongside source PDF
    if (!noImages)
    {
        var imageTask = Task.Run(async () =>
        {
            try
            {
                string thumbPath = ""; int thumbW = 0, thumbH = 0;
                string fullPath = ""; int fullW = 0, fullH = 0;
                byte[] previewData = Array.Empty<byte>(); byte[] thumbData = Array.Empty<byte>();

                if (pdfImageExtractor != null)
                {
                    // Use Telerik direct image extraction (no browser needed)
                    // outputDir = publishedDir so images land in Published/
                    var (fPath, tPath, w, h, pData, tData) = pdfImageExtractor.ExtractPageImage(pdfPath, outputDir: publishedDir);

                    if (!string.IsNullOrEmpty(fPath))
                    {
                        (fullPath, fullW, fullH) = (fPath, w, h);
                        previewData = pData;
                    }
                    if (!string.IsNullOrEmpty(tPath))
                    {
                        (thumbPath, thumbW, thumbH) = (tPath, 100, h > 0 && w > 0 ? (int)(100.0 * h / w) : 0);
                        thumbData = tData;
                    }
                }
                else if (thumbnailService != null)
                {
                    // Use Playwright browser rendering
                    var pageImages = await thumbnailService.GeneratePageImagesAsync(pdfPath);

                    foreach (var (filePath, width, height, imgData) in pageImages)
                    {
                        if (filePath.Contains("_thumb."))
                        {
                            (thumbPath, thumbW, thumbH) = (filePath, width, height);
                            thumbData = imgData;
                        }
                        else
                        {
                            (fullPath, fullW, fullH) = (filePath, width, height);
                            previewData = imgData;
                        }
                    }
                }

                // Single upsert call - only updates images with W>0 and H>0
                if (!string.IsNullOrEmpty(thumbPath) || !string.IsNullOrEmpty(fullPath))
                {
                    lock (dbService)
                    {
                        dbService.UpsertDocumentImages(publishedPdfPath, fullPath, fullW, fullH, thumbPath, thumbW, thumbH,
                            previewData: previewData, thumbData: thumbData);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Page image error for {System.IO.Path.GetFileName(pdfPath)}: {ex.Message}");
            }
        });
        pendingImageTasks.Add(imageTask);
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

/// <summary>
/// Clean MIME quoted-printable artifacts from OCR'd email text.
/// In these PDFs, the '=' character replaces exactly one letter due to MIME encoding
/// that was baked into the document before printing/scanning. Examples:
///   Ep=tein → Eptein (was Epstein, 's' replaced by '=')
///   bo=tom → botom (was bottom, 't' replaced by '=')
///   =ddressee → ddressee (was addressee, 'a' replaced by '=')
///   recipie=t → recipiet (was recipient, 'n' replaced by '=')
///   co=] → co] (was com], 'm' replaced by '=')
/// 
/// Also handles:
///   =XX hex sequences (proper quoted-printable: =20 → space, =3D → '=', etc.)
///   =\r\n or =\n soft line breaks (remove entirely)
///   =0A, =0D line break codes
/// </summary>
string CleanMimeArtifacts(string text)
{
    if (string.IsNullOrEmpty(text)) return text;

    // 1. Decode proper =XX hex sequences FIRST (e.g., =20 → space, =3D → '=')
    text = Regex.Replace(text, @"=([0-9A-Fa-f]{2})", m =>
    {
        int charCode = Convert.ToInt32(m.Groups[1].Value, 16);
        char decoded = (char)charCode;
        // Only decode printable ASCII or common whitespace
        if (charCode == 0x0D || charCode == 0x0A) return " "; // CR/LF → space
        if (charCode >= 0x20 && charCode <= 0x7E) return decoded.ToString();
        return ""; // Strip non-printable
    });

    // 2. Remove soft line breaks: = at end of line (MIME continuation)
    text = Regex.Replace(text, @"=\r?\n", "");

    // 3. Remove remaining '=' between letters (the "replacing a character" artifact)
    //    Pattern: letter = letter  →  join them (the = ate one char, nothing to restore)
    text = Regex.Replace(text, @"(?<=[a-zA-Z])=(?=[a-zA-Z])", "");

    // 4. Remove '=' at start of a word (before letters, e.g., =ddressee)
    text = Regex.Replace(text, @"(?<=\s|^)=(?=[a-zA-Z])", "");

    // 5. Remove '=' before punctuation within words (e.g., co=] → co])
    text = Regex.Replace(text, @"(?<=[a-zA-Z])=(?=[)\]}>.,;:!?/])", "");

    return text;
}

/// <summary>
/// Extract names from document text using regex/heuristic patterns.
/// Targets email headers (From:, To:, Cc:, Sent by:) and common name patterns.
/// </summary>
List<string> ExtractNamesFromText(string text)
{
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(text)) return names.ToList();

    // Use first 8000 chars - most names appear in headers at the top
    string snippet = text.Length > 8000 ? text.Substring(0, 8000) : text;

    // --- 1. Email header patterns (From:, To:, Cc:, Sent by:) ---
    // Matches "From: FirstName LastName" or "To: FirstName LastName"
    // Stops at common non-name tokens (email, angle brackets, dates, etc.)
    // NOTE: [a-z=] and [A-Z=] allow '=' as a wildcard for MIME-damaged characters
    //       e.g. "Ep=tein" matches as a name, then '=' is stripped in CleanExtractedName
    var headerPatterns = new[]
    {
        @"(?:From|To|Cc|Bcc|Sent\s*(?:by)?)\s*:\s*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})",
        // "From: Jeffrey Epstein <email>"
        @"(?:From|To|Cc|Bcc)\s*:\s*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})\s*<",
        // Multi-recipient: "To: FirstName LastName; FirstName2 LastName2"
        @"(?:To|Cc|Bcc)\s*:\s*(?:(?:[A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})\s*;\s*)*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})",
    };

    foreach (var pattern in headerPatterns)
    {
        foreach (Match m in Regex.Matches(snippet, pattern))
        {
            var name = CleanExtractedName(m.Groups[1].Value);
            if (IsValidPersonName(name)) names.Add(name);
        }
    }

    // --- 2. "Dear X" / "Hi X" / "Hello X" patterns ---
    foreach (Match m in Regex.Matches(snippet, @"\b(?:Dear|Hi|Hello|Attn)\s+([A-Z=][a-z=]+(?:\s+[A-Z=][a-z=]{1,20})?)", RegexOptions.None))
    {
        var name = CleanExtractedName(m.Groups[1].Value);
        if (IsValidPersonName(name)) names.Add(name);
    }

    // --- 3. Known-name-context patterns ---
    // "Appt w/ PersonName" or "LUNCH w/ PersonName" or "meeting with PersonName"
    foreach (Match m in Regex.Matches(snippet, @"\b(?:w/|with|meeting\s+with|Appt\s+w/|LUNCH\s+w/)\s+([A-Z=][a-z=]+(?:\s+[A-Z=][a-z=]{1,20}))", RegexOptions.None))
    {
        var name = CleanExtractedName(m.Groups[1].Value);
        if (IsValidPersonName(name)) names.Add(name);
    }

    // --- 4. Capitalized "Firstname Lastname" sequences that look like person names ---
    // This is the broadest pattern - two adjacent capitalized words not matching common non-name patterns
    foreach (Match m in Regex.Matches(snippet, @"\b([A-Z=][a-z=]{2,15}\s+[A-Z=][a-z=]{2,20})\b"))
    {
        var candidate = m.Groups[1].Value;
        if (IsValidPersonName(candidate) && !IsCommonPhrase(candidate))
        {
            names.Add(candidate);
        }
    }

    return names.OrderBy(p => p).ToList();
}

string CleanExtractedName(string name)
{
    if (string.IsNullOrWhiteSpace(name)) return "";
    // Remove newlines - OCR artifacts
    name = name.Replace("\n", " ").Replace("\r", " ");
    // Remove trailing punctuation, digits, email artifacts
    name = Regex.Replace(name, @"[\d<>\[\]@.,;:!?\-_/\\()]+$", "").Trim();
    name = Regex.Replace(name, @"^[\d<>\[\]@.,;:!?\-_/\\()]+", "").Trim();
    // Strip MIME '=' artifacts from the name (evidence is preserved in raw text;
    // this only cleans the derived Names metadata field)
    name = name.Replace("=", "");
    // Collapse multiple spaces
    name = Regex.Replace(name, @"\s{2,}", " ").Trim();
    // Remove trailing words that are common email artifacts (e.g., "Jeffrey E. Sent" -> trim "Sent")
    name = Regex.Replace(name, @"\s+(Sent|From|To|Cc|Subject|Date|Re|Fwd|Mon|Tue|Wed|Thu|Fri|Sat|Sun)$", "", RegexOptions.IgnoreCase).Trim();
    return name;
}

bool IsValidPersonName(string name)
{
    if (string.IsNullOrWhiteSpace(name) || name.Length < 4) return false;
    
    // Must contain at least a space (first + last)
    if (!name.Contains(' ')) return false;
    
    // Reject if contains digits, @, newlines, or common non-name chars
    if (Regex.IsMatch(name, @"[\d@#$%^&*(){}|<>\n\r]")) return false;

    // Reject single-char first or last names
    var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2) return false;
    if (parts[0].Length < 2 || parts[^1].Length < 2) return false;

    // Reject names starting with common non-name words
    var rejectFirstWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Sent", "Hello", "Dear", "Hey", "The", "This", "That", "Your", "Our", "My",
        "From", "Date", "Subject", "Reply", "Forward", "Original", "Attachment",
        "Please", "Thanks", "Thank", "Best", "Kind", "Warm", "Good", "Look",
        "Flight", "Stem", "Med", "Image", "File", "Case", "Help", "Earth",
        "Click", "View", "Open", "Read", "Copy", "Save", "Print", "Delete",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "San", "New", "South", "North", "East", "West", "Los", "Santa", "Palm"
    };
    if (rejectFirstWords.Contains(parts[0])) return false;

    return true;
}

bool IsCommonPhrase(string candidate)
{
    // Common two-word phrases that are NOT person names — frequently found in legal/email docs
    var nonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Sent from", "Sent From", "Original Message", "Court Order", "Court Document",
        "New York", "Los Angeles", "San Francisco", "Santa Monica", "Palm Beach",
        "United States", "South Florida", "Southern District", "Northern District",
        "Dear Sir", "Dear Madam", "Good Morning", "Good Afternoon", "Good Evening",
        "Best Regards", "Kind Regards", "Warm Regards", "Many Thanks",
        "Please Note", "For Immediate", "Private Communication", "All Rights",
        "Rights Reserved", "Jeffrey Epstein", // Often appears in disclaimers; keep if in From/To but filter as generic
        "East Street", "West Street", "North Street", "South Street",
        "Monday Morning", "Tuesday Morning", "Wednesday Morning", "Thursday Morning",
        "Friday Morning", "Saturday Morning", "Sunday Morning",
        "January February", "February March", "Unauthorized Use",
        "Your Email", "This Email", "This Message", "Earth Link",
        "Flash Player", "Internet Explorer", "Microsoft Office", "Google Chrome",
        "Apple Inc", "Subject Line", "Read Receipt", "Return Receipt",
        "Thank You", "Look Forward", "Property List", "Attachment Name",
        "Cell Number", "Phone Number", "Office Number",
        "Image Format", "File Size", "File Name", "Date Received",
        "Help Save", "Feminine Care", "Gillette Blade",
        "Building Entrance", "Front Door",
        "Attorney Client", "Inside Information", "Strictly Prohibited",
    };

    return nonNames.Contains(candidate);
}

void CleanGeneratedFiles(string folder)
{
    var extensions = new[] { "*.jpg", "*.done", "*.done.embeddings", "*.done.images", "*.json", "*.html" };
    int count = 0;

    foreach (var ext in extensions)
    {
        var files = Directory.GetFiles(folder, ext, SearchOption.AllDirectories);
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                count++;
            }
            catch { /* ignore locked files */ }
        }
    }

    Console.WriteLine($"  Cleaned {count} generated files (.jpg, .done*, .json, .html)");
}

// Helper extension
public static class StringExtensions
{
    public static string D = "";
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}






