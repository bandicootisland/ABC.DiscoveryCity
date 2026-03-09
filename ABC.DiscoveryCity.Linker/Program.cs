using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.Embeddings;
using System.Diagnostics;

// ============================================================
// ABC.DiscoveryCity.Linker — PDF image generation for datasets
//
// Uses PdfThumbnailPipeline: tries direct Telerik image extraction
// first, falls back to Chrome/pdf.js if extraction returns nothing.
//
// Modes:
//   (default)    Pipeline: extract → fallback
//   --inspect    Diagnostic: inspect PDF content structure
// ============================================================

// Parse command-line arguments
bool forceReprocess = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
bool headless = args.Any(a => a.Equals("--headless", StringComparison.OrdinalIgnoreCase));
bool inspectMode = args.Any(a => a.Equals("--inspect", StringComparison.OrdinalIgnoreCase));
bool extractOnly = args.Any(a => a.Equals("--extract-only", StringComparison.OrdinalIgnoreCase));
int browserCount = 4;
int parallelism = 4;
int limitFiles = 0;

for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--browsers", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        int.TryParse(args[i + 1], out browserCount);
    if (args[i].Equals("--parallel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        int.TryParse(args[i + 1], out parallelism);
    if (args[i].Equals("--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        int.TryParse(args[i + 1], out limitFiles);
}

string[] targetDataSets = args
    .Where(a => !a.StartsWith("--") && !int.TryParse(a, out _))
    .ToArray();

if (targetDataSets.Length == 0)
{
    Console.WriteLine("ABC.DiscoveryCity.Linker — PDF Image Generator");
    Console.WriteLine();
    Console.WriteLine("Usage: dotnet run -- \"DataSet 8\" [\"DataSet 9\"] [options]");
    Console.WriteLine();
    Console.WriteLine("Modes:");
    Console.WriteLine("  (default)         Pipeline: extract images directly, fall back to Chrome/pdf.js");
    Console.WriteLine("  --extract-only    Extract only (no browser fallback)");
    Console.WriteLine("  --inspect         Inspect PDF content structure (diagnostic)");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  <DataSet names>   One or more dataset names to process (required)");
    Console.WriteLine("  --headless        Run Chrome headless for fallback (default: visible)");
    Console.WriteLine("  --browsers N      Number of Chrome tabs for fallback (default: 4)");
    Console.WriteLine("  --parallel N      Parallel extraction threads (default: 4)");
    Console.WriteLine("  --limit N         Max files to process (0 = unlimited)");
    Console.WriteLine("  --force           Reprocess files with existing .done.images");
    Console.WriteLine();
    return;
}

Console.WriteLine("ABC.DiscoveryCity.Linker — PDF Image Generator");
Console.WriteLine("================================================");
Console.WriteLine($"Mode:        {(inspectMode ? "inspect" : extractOnly ? "extract-only" : "pipeline (extract → fallback)")}");
Console.WriteLine($"DataSets:    {string.Join(", ", targetDataSets)}");
Console.WriteLine($"Parallel:    {parallelism}");
if (!extractOnly && !inspectMode)
    Console.WriteLine($"Browsers:    {browserCount} tabs (fallback), Headless: {headless}");
Console.WriteLine($"Limit:       {(limitFiles > 0 ? limitFiles.ToString() : "unlimited")}");
Console.WriteLine($"Force:       {forceReprocess}");
Console.WriteLine();

// Locate dataset folders
string rootFolder = "/media/stephen/18TB/EpsteinFiles/DepartmentofJustice/DOJ_Disclosures/";
var allSubDirs = Directory.GetDirectories(rootFolder, "DataSet*", SearchOption.TopDirectoryOnly);

List<(string Path, string Name)> targetFolders = new();
foreach (var dsName in targetDataSets)
{
    string dsAlt = dsName.Replace(" ", "_");
    var match = allSubDirs.FirstOrDefault(d =>
        System.IO.Path.GetFileName(d).Equals(dsName, StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetFileName(d).Equals(dsAlt, StringComparison.OrdinalIgnoreCase));

    if (match != null)
    {
        targetFolders.Add((match, System.IO.Path.GetFileName(match)));
        Console.WriteLine($"Found folder: {match}");
    }
    else
    {
        Console.WriteLine($"WARNING: Folder not found for '{dsName}'");
    }
}

if (targetFolders.Count == 0)
{
    Console.WriteLine("No valid dataset folders found. Exiting.");
    return;
}

// ==================== INSPECT MODE ====================
if (inspectMode)
{
    Console.WriteLine("\n=== INSPECT MODE: Analyzing PDF content structure ===\n");
    var extractor = new PdfImageExtractor();

    foreach (var (folderPath, dataSetName) in targetFolders)
    {
        var pdfFiles = Directory.GetFiles(folderPath, "*.pdf", SearchOption.AllDirectories);
        int toInspect = limitFiles > 0 ? Math.Min(limitFiles, pdfFiles.Length) : Math.Min(5, pdfFiles.Length);

        Console.WriteLine($"--- {dataSetName}: inspecting {toInspect} of {pdfFiles.Length} PDFs ---");

        foreach (var pdf in pdfFiles.Take(toInspect))
        {
            Console.WriteLine($"\n  File: {System.IO.Path.GetFileName(pdf)}");
            try { extractor.InspectPage(pdf); }
            catch (Exception ex) { Console.WriteLine($"  [ERROR] {ex.Message}"); }
        }
    }

    Console.WriteLine("\n=== INSPECT COMPLETE ===");
    return;
}

// ==================== PIPELINE MODE ====================

// Initialize database
Console.WriteLine("Initializing Database...");
IEmbeddingService? embeddingService = null;
try
{
    embeddingService = new OllamaEmbeddingService();
    Console.WriteLine($"Embedding Service ready (dim: {embeddingService.Dimension})");
}
catch (Exception ex)
{
    Console.WriteLine($"WARNING: Embedding service unavailable: {ex.Message}");
}

var dbService = new DbService(embeddingService);
dbService.InitDb();
Console.WriteLine("Database initialized.");

string sourceName = "DepartmentofJustice";
string baseFilePath = System.IO.Path.GetDirectoryName(rootFolder.TrimEnd(System.IO.Path.DirectorySeparatorChar)) ?? "";
Guid sourceId = dbService.GetOrCreateSource(sourceName, baseFilePath);

// Create pipeline (fallback disabled when --extract-only)
await using var pipeline = new PdfThumbnailPipeline(
    headless: headless,
    browserCount: browserCount,
    remoteViewerUrl: extractOnly ? null : "http://localhost:5022/pdfviewer.html");

var sw = Stopwatch.StartNew();
int totalDone = 0, totalErr = 0;

foreach (var (folderPath, dataSetName) in targetFolders)
{
    Guid dataSetId = dbService.GetOrCreateDataSet(sourceId, dataSetName);
    Console.WriteLine($"\n--- Processing: {dataSetName} (Id: {dataSetId}) ---");

    var pdfFiles = Directory.GetFiles(folderPath, "*.pdf", SearchOption.AllDirectories);
    var toProcess = pdfFiles
        .Where(f => forceReprocess || !File.Exists(f + ".done.images"))
        .ToArray();

    if (limitFiles > 0)
        toProcess = toProcess.Take(limitFiles - totalDone).ToArray();

    int alreadyDone = pdfFiles.Length - toProcess.Length;
    Console.WriteLine($"  PDFs: {pdfFiles.Length}, To process: {toProcess.Length}, Already done: {alreadyDone}");

    if (toProcess.Length == 0)
    {
        Console.WriteLine("  Nothing to do.");
        continue;
    }

    int done = 0, errors = 0;
    var dsSw = Stopwatch.StartNew();

    await Parallel.ForEachAsync(toProcess, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (pdfPath, ct) =>
    {
        int cur = Interlocked.Increment(ref done);
        if (cur % 100 == 0 || cur <= 5)
        {
            double rate = cur / Math.Max(dsSw.Elapsed.TotalSeconds, 0.1);
            int remaining = toProcess.Length - cur;
            double etaMin = remaining / Math.Max(rate, 0.1) / 60;
            Console.WriteLine($"  [{cur}/{toProcess.Length}] {rate:F1}/s ETA:{etaMin:F0}m — {System.IO.Path.GetFileName(pdfPath)}");
        }

        try
        {
            var result = await pipeline.ProcessAsync(pdfPath);

            if (result.Success)
            {
                // For extract method, thumb dimensions come from the _thumb.jpg file
                int thumbW = result.ThumbWidth > 0 ? result.ThumbWidth : 100;
                int thumbH = result.ThumbHeight > 0 ? result.ThumbHeight
                    : (result.Height > 0 && result.Width > 0)
                        ? (int)(100.0 * result.Height / result.Width) : 0;

                lock (dbService)
                {
                    dbService.UpsertDocumentImages(
                        pdfPath, result.FullPath, result.Width, result.Height,
                        result.ThumbPath, thumbW, thumbH);
                }
                File.Create(pdfPath + ".done.images").Dispose();
            }
        }
        catch (Exception ex)
        {
            int err = Interlocked.Increment(ref errors);
            if (err <= 20)
                Console.WriteLine($"  [ERROR] {System.IO.Path.GetFileName(pdfPath)}: {ex.Message}");
        }
    });

    dsSw.Stop();
    double finalRate = done / Math.Max(dsSw.Elapsed.TotalSeconds, 1);
    Console.WriteLine($"  Done: {done}, Errors: {errors}, Time: {dsSw.Elapsed:hh\\:mm\\:ss} ({finalRate:F1}/s)");
    totalDone += done;
    totalErr += errors;

    if (limitFiles > 0 && totalDone >= limitFiles) break;
}

sw.Stop();
Console.WriteLine($"\n=== COMPLETE ===");
Console.WriteLine($"Processed: {totalDone}, Errors: {totalErr}, Total: {sw.Elapsed:hh\\:mm\\:ss}");
pipeline.PrintStats();
