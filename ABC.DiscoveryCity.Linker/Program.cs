using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.Embeddings;
using SixLabors.ImageSharp;
using System.Collections.Concurrent;
using System.Diagnostics;

// ============================================================
// ABC.DiscoveryCity.Linker — Fast image generation via Telerik/SkiaSharp
// No browser required. Pure CPU rendering.
// ============================================================

// Parse command-line arguments
bool forceReprocess = args.Any(a => a.Equals("--force", StringComparison.OrdinalIgnoreCase));
int maxParallelism = 5; // default
int limitFiles = 0;

for (int i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--parallel", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        int.TryParse(args[i + 1], out maxParallelism);
    if (args[i].Equals("--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        int.TryParse(args[i + 1], out limitFiles);
}

string[] targetDataSets = args.Where(a => !a.StartsWith("--") && !int.TryParse(a, out _)).ToArray();
if (targetDataSets.Length == 0)
{
    Console.WriteLine("Usage: dotnet run -- \"DataSet 8\" [\"DataSet 9\"] [--parallel 5] [--limit 100] [--force]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  <DataSet names>   One or more dataset names to process (required)");
    Console.WriteLine("  --parallel N      Max parallel PDF renders (default: 5)");
    Console.WriteLine("  --limit N         Max files to process (0 = unlimited)");
    Console.WriteLine("  --force           Reprocess files with existing .done.images");
    Console.WriteLine();
    Console.WriteLine("This tool uses Telerik/SkiaSharp direct rendering (no browser).");
    Console.WriteLine("Run alongside TestApp which uses Chrome for a different dataset.");
    return;
}

Console.WriteLine("ABC.DiscoveryCity.Linker — Direct Image Renderer");
Console.WriteLine("=================================================");
Console.WriteLine($"DataSets:    {string.Join(", ", targetDataSets)}");
Console.WriteLine($"Parallelism: {maxParallelism}");
Console.WriteLine($"Limit:       {(limitFiles > 0 ? limitFiles.ToString() : "unlimited")}");
Console.WriteLine($"Force:       {forceReprocess}");
Console.WriteLine();

// Initialize services
Console.WriteLine("Initializing Telerik renderer...");
var telerikService = new TelerikThumbnailService();

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
    Console.WriteLine("Proceeding without embeddings.");
}

var dbService = new DbService(embeddingService);
dbService.InitDb();
Console.WriteLine("Database initialized.");

// Locate dataset folders
string rootFolder = "/media/stephen/18TB/EpsteinFiles/DepartmentofJustice/DOJ_Disclosures/";
var allSubDirs = Directory.GetDirectories(rootFolder, "DataSet*", SearchOption.TopDirectoryOnly);
var targetFolders = new List<(string Path, string Name)>();

foreach (var dsName in targetDataSets)
{
    string dsAlt = dsName.Replace(" ", "_");
    var match = allSubDirs.FirstOrDefault(d =>
        Path.GetFileName(d).Equals(dsName, StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(d).Equals(dsAlt, StringComparison.OrdinalIgnoreCase));
    
    if (match != null)
    {
        targetFolders.Add((match, Path.GetFileName(match)));
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

// Ensure source/dataset records exist
string sourceName = "DepartmentofJustice";
string baseFilePath = Path.GetDirectoryName(rootFolder.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
int sourceId = dbService.GetOrCreateSource(sourceName, baseFilePath);

// Process each dataset
var sw = Stopwatch.StartNew();
int totalProcessed = 0;
int totalSkipped = 0;
int totalErrors = 0;

foreach (var (folderPath, dataSetName) in targetFolders)
{
    int dataSetId = dbService.GetOrCreateDataSet(sourceId, dataSetName);
    Console.WriteLine($"\n--- Processing: {dataSetName} (Id: {dataSetId}) ---");

    var pdfFiles = Directory.GetFiles(folderPath, "*.pdf", SearchOption.AllDirectories);
    
    // Filter to unprocessed files
    var filesToProcess = pdfFiles
        .Where(f => forceReprocess || !File.Exists(f + ".done.images"))
        .ToArray();

    if (limitFiles > 0)
        filesToProcess = filesToProcess.Take(limitFiles - totalProcessed).ToArray();

    Console.WriteLine($"  PDFs found: {pdfFiles.Length}, To process: {filesToProcess.Length}, Already done: {pdfFiles.Length - filesToProcess.Length}");

    if (filesToProcess.Length == 0)
    {
        Console.WriteLine("  Nothing to do for this dataset.");
        continue;
    }

    int datasetProcessed = 0;
    int datasetErrors = 0;
    var datasetSw = Stopwatch.StartNew();

    // Process in parallel using Telerik direct rendering
    await Parallel.ForEachAsync(filesToProcess, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism }, async (pdfPath, ct) =>
    {
        try
        {
            // Load PDF with Telerik
            var pdfProvider = new Telerik.Windows.Documents.Fixed.FormatProviders.Pdf.PdfFormatProvider();
            Telerik.Windows.Documents.Fixed.Model.RadFixedDocument doc;
            using (var fs = File.OpenRead(pdfPath))
            {
                doc = pdfProvider.Import(fs);
            }

            if (doc.Pages.Count == 0)
            {
                int err = Interlocked.Increment(ref datasetErrors);
                if (err <= 10) Console.WriteLine($"  [SKIP] No pages: {Path.GetFileName(pdfPath)}");
                return;
            }

            // Generate images using Telerik/SkiaSharp
            var (thumbPath, fullPath) = telerikService.GenerateThumbnails(doc, pdfPath);

            string thumbFile = thumbPath;
            int thumbW = 0, thumbH = 0;
            string fullFile = fullPath;
            int fullW = 0, fullH = 0;

            if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
            {
                using var img = Image.Load(thumbPath);
                (thumbW, thumbH) = (img.Width, img.Height);
            }
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
            {
                using var img = Image.Load(fullPath);
                (fullW, fullH) = (img.Width, img.Height);
            }

            // Upsert into DB
            if (!string.IsNullOrEmpty(thumbFile) || !string.IsNullOrEmpty(fullFile))
            {
                lock (dbService)
                {
                    dbService.UpsertDocumentImages(pdfPath, fullFile, fullW, fullH, thumbFile, thumbW, thumbH);
                }
                // Mark done
                File.Create(pdfPath + ".done.images").Dispose();
            }

            int count = Interlocked.Increment(ref datasetProcessed);
            if (count % 100 == 0 || count <= 5)
            {
                double rate = count / datasetSw.Elapsed.TotalSeconds;
                int remaining = filesToProcess.Length - count;
                double etaMinutes = remaining / rate / 60;
                Console.WriteLine($"  [{count}/{filesToProcess.Length}] {rate:F1} files/sec, ETA: {etaMinutes:F0} min — {Path.GetFileName(pdfPath)}");
            }
        }
        catch (Exception ex)
        {
            int err = Interlocked.Increment(ref datasetErrors);
            if (err <= 20)
                Console.WriteLine($"  [ERROR] {Path.GetFileName(pdfPath)}: {ex.Message}");
        }
    });

    datasetSw.Stop();
    double finalRate = datasetProcessed / Math.Max(datasetSw.Elapsed.TotalSeconds, 1);
    Console.WriteLine($"  Done: {datasetProcessed} processed, {datasetErrors} errors in {datasetSw.Elapsed:hh\\:mm\\:ss} ({finalRate:F1} files/sec)");

    totalProcessed += datasetProcessed;
    totalErrors += datasetErrors;

    if (limitFiles > 0 && totalProcessed >= limitFiles) break;
}

sw.Stop();
Console.WriteLine($"\n=== COMPLETE ===");
Console.WriteLine($"Processed: {totalProcessed}, Errors: {totalErrors}");
Console.WriteLine($"Total time: {sw.Elapsed:hh\\:mm\\:ss}");

