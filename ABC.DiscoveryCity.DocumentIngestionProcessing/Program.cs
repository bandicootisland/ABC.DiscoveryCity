using ABC.DiscoveryCity.DocumentIngestionProcessing;
using ABC.DiscoveryCity.DocumentIngestionProcessing.Tests;
using ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline;
using ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;
using ABC.DiscoveryCity.Embeddings;
using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.PostgreSQL;
using System.Text.RegularExpressions;
using System.Text.Json;

// CRITICAL: Register font provider for Telerik PDF processing.
// Without this, Telerik cannot decode ToUnicode CMap tables on .NET Core,
// resulting in garbled "cipher text" output from PDFs with embedded fonts.
Telerik.Windows.Documents.Extensibility.FixedExtensibilityManager.FontsProvider =
    new ABC.DiscoveryCity.TelerikProcessing.WindowsFontsProvider();

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
bool noEmbeddings = args.Any(a => a.Equals("--no-embeddings", StringComparison.OrdinalIgnoreCase));
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
if (noEmbeddings) Console.WriteLine("MODE: No embeddings (skip embedding generation, preserve existing)");

// Initialize Embedding Service (Ollama)
IEmbeddingService? embeddingService = null;
if (noEmbeddings)
{
    Console.WriteLine("Skipping Embedding Service (--no-embeddings flag).");
}
else
{
    Console.WriteLine("Initializing Embedding Service...");
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
                string fullText = dbService.GetDocumentFullText(docId);
                if (string.IsNullOrWhiteSpace(fullText))
                {
                    totalSkipped++;
                    continue;
                }

                var (extractedNames, extractedTerms) = MetadataExtractors.ExtractNamesAndTerms(fullText);

                var metadata = string.IsNullOrWhiteSpace(metadataJson)
                    ? new Dictionary<string, JsonElement>()
                    : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(metadataJson)
                      ?? new Dictionary<string, JsonElement>();

                if (extractedNames.Count > 0)
                    metadata["Names"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(extractedNames));
                else
                    metadata.Remove("Names");

                if (extractedTerms.Count > 0)
                    metadata["Terms"] = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(extractedTerms));
                else
                    metadata.Remove("Terms");

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
                    Console.WriteLine($"    [ERROR] Doc {docId} ({Path.GetFileName(filePath)}): {ex.Message}");
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

        await Parallel.ForEachAsync(docs, new ParallelOptions { MaxDegreeOfParallelism = 5 }, async (doc, ct) =>
        {
            try
            {
                var embedding = await embeddingService.GetEmbeddingAsync(doc.SentencesText);
                if (dbService.UpdateDocumentEmbedding(doc.DocId, embedding))
                {
                    int count = Interlocked.Increment(ref totalUpdated);
                    if (count % 100 == 0)
                        Console.WriteLine($"    [{count}/{totalMissing}] embeddings updated...");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref totalErrors);
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
    var imgSubDirs = Directory.GetDirectories(imgRootFolder, "DataSet*", SearchOption.TopDirectoryOnly);
    var imgTargetFolders = new List<string>();
    var imgAddedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
        var pdfFiles = Directory.GetFiles(folder, "*.pdf", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Published" + Path.DirectorySeparatorChar))
            .ToArray();

        foreach (var pdfPath in pdfFiles)
        {
            if (MAX_IMG_FILES > 0 && imgProcessed >= MAX_IMG_FILES) break;

            string doneImages = pdfPath + ".done.images";

            if (!forceReprocess && File.Exists(doneImages))
            {
                imgSkipped++;
                continue;
            }

            imgTotal++;
            int current = ++imgProcessed;
            if (current % 100 == 0 || current <= 5)
                Console.WriteLine($"  [{current}] Image: {Path.GetFileName(pdfPath)}");

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
                        File.Create(doneImages).Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref imgErrors);
                    Console.WriteLine($"  [WARN] Image error for {Path.GetFileName(pdfPath)}: {ex.Message}");
                }
            });
            imgPendingTasks.Add(imageTask);
        }

        if (MAX_IMG_FILES > 0 && imgProcessed >= MAX_IMG_FILES) break;
    }

    if (imgPendingTasks.Count > 0)
    {
        Console.WriteLine($"Waiting for {imgPendingTasks.Count} image tasks to complete...");
        await Task.WhenAll(imgPendingTasks);
    }

    await imgThumbnailService.DisposeAsync();
    Console.WriteLine($"\nImages-only complete! Processed: {imgProcessed}, Skipped: {imgSkipped}, Errors: {imgErrors}");
    return;
}

// ============================================================
// MAIN INGESTION MODE — Step-based pipeline
// ============================================================
int MAX_FILES = limitFiles;
int totalFiles = 0;
int startedFiles = 0;
int processedFiles = 0;
int skippedFiles = 0;
var imageThrottle = new SemaphoreSlim(10);

Console.WriteLine($"Root Folder: {rootFolder}");

// Find DataSet folders
var subDirs = Directory.GetDirectories(rootFolder, "DataSet*", SearchOption.TopDirectoryOnly);
var targetFolders = new List<string>();
var addedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

// Add priority DataSets in specified order
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

// Add remaining folders (natural numeric sort so DataSet 9 < DataSet 10)
foreach (var dir in subDirs
    .OrderBy(d => Regex.Replace(Path.GetFileName(d) ?? "", @"\d+", m => m.Value.PadLeft(10, '0'))))
{
    if (!addedFolders.Contains(dir)) targetFolders.Add(dir);
}

if (targetFolders.Count == 0) targetFolders.Add(rootFolder);

// Define Source from path
string sourceName = Path.GetFileName(Path.GetDirectoryName(rootFolder.TrimEnd(Path.DirectorySeparatorChar))) ?? "Unknown";
string baseFilePath = Path.GetDirectoryName(rootFolder.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
Guid sourceId = dbService.GetOrCreateSource(sourceName, baseFilePath);
Console.WriteLine($"Created/Found Source: {sourceName} (Id: {sourceId})");

foreach (var folder in targetFolders)
{
    string dataSetName = Path.GetFileName(folder) ?? "Default";
    string publishedRelFolder = dataSetName + "/Published/";
    string publishedDir = Path.Combine(folder, "Published");
    Guid dataSetId = dbService.GetOrCreateDataSet(sourceId, dataSetName, publishedRelFolder);
    Console.WriteLine($"Created/Found DataSet: {dataSetName} (Id: {dataSetId}) for Source: {sourceName}");
    Console.WriteLine($"  Published folder: {publishedDir}");

    Console.WriteLine($"Scanning folder: {folder}");

    if (cleanFiles)
    {
        CleanGeneratedFiles(folder);
    }

    // Scan for all supported file types, excluding the Published output directory
    var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".xlsx", ".xls", ".csv", ".avi", ".mp4", ".vob", ".mov", ".mkv", ".wmv", ".m4a", ".mp3", ".wav", ".aac", ".ogg", ".flac" };
    var allFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Published" + Path.DirectorySeparatorChar))
        .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
        .ToArray();

    // Process files concurrently using pipeline
    int maxDegreeOfParallelism = 5;
    var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism };
    await Parallel.ForEachAsync(allFiles, parallelOptions, async (filePath, ct) =>
    {
        // Atomically claim a slot before doing any work
        if (MAX_FILES > 0 && Interlocked.Increment(ref startedFiles) > MAX_FILES) return;

        try
        {
            string doneFile = filePath + ".done";
            string doneEmbeddings = filePath + ".done.embeddings";
            string doneImages = filePath + ".done.images";

            // Skip if already fully processed (unless --force)
            if (!forceReprocess && File.Exists(doneFile))
            {
                Interlocked.Increment(ref skippedFiles);
                return;
            }

            // Skip if already in DB
            if (!forceReprocess && dbService.DocumentExists(filePath))
            {
                Console.WriteLine($"  [SKIP] Already in DB: {Path.GetFileName(filePath)}");
                return;
            }

            int currentCount = Interlocked.Increment(ref totalFiles);
            Console.WriteLine($"[{currentCount}] Processing: {Path.GetFileName(filePath)}");

            // Build pipeline for this file
            var pipeline = new IngestionPipeline()
                .AddStep(new LoadDocumentStep())
                .AddStep(new TelerikCorpusStep())
                .AddStep(new DevExpressCorpusStep())
                .AddStep(new AssembleDocumentStep())
                .AddStep(new MetadataExtractionStep())
                .AddStep(new SentenceIdStep())
                .AddStep(new ThumbnailStep())
                .AddStep(new PageImagesStep())
                .AddStep(new HtmlViewerStep())
                .AddStep(new XlsxBundleStep())
                .AddStep(new StoreToPostgresStep());

            var ctx = new IngestionContext
            {
                FilePath = filePath,
                PublishedDir = publishedDir,
                DataSetName = dataSetName,
                SourceName = sourceName,
                DataSetId = dataSetId,
                NoImages = noImages,
                NoEmbeddings = noEmbeddings,
                ForceReprocess = forceReprocess,
                DbService = dbService,
                EmbeddingService = embeddingService,
                ThumbnailService = thumbnailService,
                PdfImageExtractor = pdfImageExtractor,
                ImageThrottle = imageThrottle,
            };

            await pipeline.ExecuteAsync(ctx);

            // Mark as done
            File.Create(doneFile).Dispose();
            if (embeddingService != null)
                File.Create(doneEmbeddings).Dispose();
            if (!noImages)
                File.Create(doneImages).Dispose();

            int currentProcessed = Interlocked.Increment(ref processedFiles);
            if (MAX_FILES > 0 && currentProcessed >= MAX_FILES)
                Console.WriteLine($"\n--- BATCH LIMIT REACHED ({MAX_FILES} files) ---");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR processing {filePath}: {ex.Message}");
        }
    });

    if (MAX_FILES > 0 && processedFiles >= MAX_FILES) break;
}

Console.WriteLine($"\nDone! Processed {processedFiles}/{totalFiles} files. Skipped {skippedFiles} already-done.");

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

public static class StringExtensions
{
    public static string D = "";
    public static string Truncate(this string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value.Substring(0, maxLength);
    }
}
