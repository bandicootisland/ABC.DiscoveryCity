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
bool exportSignatures = args.Any(a => a.Equals("--export-signatures", StringComparison.OrdinalIgnoreCase));
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
    ?? @"S:\EpsteinFiles\DepartmentofJustice\DOJ_Disclosures";

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
bool explicitDataSets = positionalArgs.Count > 0;
string[] priorityDataSets = explicitDataSets ? positionalArgs.ToArray() : new[] { "DataSet 12" };

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

if (args.Any(a => a.Equals("--alter-vectors", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("=== ALTER VECTOR COLUMNS: 384 → 1024 ===\n");
    using var conn = new Npgsql.NpgsqlConnection("Host=192.168.1.114;Port=5435;Database=discoverycity;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo");
    conn.Open();

    // Step 1: Null out existing embeddings (can't ALTER with mismatched dimension data)
    string[] clearSql = [
        "UPDATE ParentDocuments SET Embedding = NULL WHERE Embedding IS NOT NULL",
        "UPDATE DocumentChunks SET Embedding = NULL WHERE Embedding IS NOT NULL",
    ];
    foreach (var sql in clearSql)
    {
        using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 300;
        int rows = cmd.ExecuteNonQuery();
        Console.WriteLine($"  Cleared {rows} rows: {sql.Split(' ')[1]}");
    }

    // Step 2: Drop vector indexes (they reference the old dimension)
    string[] dropIndexSql = [
        "DROP INDEX IF EXISTS idx_documentchunks_embedding",
        "DROP INDEX IF EXISTS idx_parentdocuments_embedding",
        "DROP INDEX IF EXISTS idx_chunks_embedding_hnsw",
        "DROP INDEX IF EXISTS idx_parent_embedding_hnsw",
    ];
    foreach (var sql in dropIndexSql)
    {
        using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
        Console.WriteLine($"  OK: {sql}");
    }

    // Step 3: ALTER parent tables only (partitions inherit automatically)
    string[] alterSql = [
        "ALTER TABLE ParentDocuments ALTER COLUMN Embedding TYPE vector(1024)",
        "ALTER TABLE DocumentChunks ALTER COLUMN Embedding TYPE vector(1024)",
    ];
    foreach (var sql in alterSql)
    {
        try
        {
            using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 300;
            cmd.ExecuteNonQuery();
            Console.WriteLine($"  OK: {sql}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {sql} — {ex.Message}");
        }
    }

    Console.WriteLine("\nDone. Run --embeddings-only to re-embed with mxbai-embed-large (1024-dim).");
    return;
}

if (args.Any(a => a.Equals("--import-dictionary", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("=== IMPORT DICTIONARY FROM BOOKCITY ===\n");

    // BookCity DB
    string[] bookCityCandidates = [
        "Host=192.168.1.100;Port=5432;Database=bookcity;Username=bookcity;Password=bookcity_dev",
    ];
    string discoveryCityConn = "Host=192.168.1.114;Port=5435;Database=discoverycity;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo";

    string dictName = "oed_cd_v4";

    // Read from BookCity — try each connection candidate
    Console.WriteLine($"  Reading '{dictName}' from BookCity...");
    byte[]? xlsxData = null;
    foreach (var bookCityConn in bookCityCandidates)
    {
        try
        {
            using var srcConn = new Npgsql.NpgsqlConnection(bookCityConn);
            srcConn.Open();
            using var cmd = new Npgsql.NpgsqlCommand("SELECT package_data FROM dictionary_packages WHERE dictionary_name = @name;", srcConn);
            cmd.Parameters.AddWithValue("name", dictName);
            var result = cmd.ExecuteScalar();
            if (result is byte[] data)
            {
                xlsxData = data;
                Console.WriteLine($"  Connected via: {bookCityConn.Split(';')[1]}");
                break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Tried {bookCityConn.Split(';')[1]} — {ex.Message.Split('\n')[0]}");
        }
    }

    if (xlsxData == null)
    {
        Console.WriteLine($"  ERROR: Dictionary '{dictName}' not found in any BookCity database.");
        Console.WriteLine("  Make sure BookCity has the OED loaded. Check: SELECT dictionary_name FROM dictionary_packages;");
        return;
    }

    Console.WriteLine($"  Loaded {xlsxData.Length / 1024.0 / 1024.0:F2} MB from BookCity");

    // Ensure DiscoveryCity has the table
    var db = new DbService(null);
    db.InitDb();

    // Write to DiscoveryCity
    Console.WriteLine($"  Writing to DiscoveryCity...");
    using (var dstConn = new Npgsql.NpgsqlConnection(discoveryCityConn))
    {
        dstConn.Open();
        using var cmd = new Npgsql.NpgsqlCommand(@"
            INSERT INTO dictionary_packages (id, dictionary_name, package_data)
            VALUES (@id, @name, @data)
            ON CONFLICT (dictionary_name) DO UPDATE SET
                package_data = EXCLUDED.package_data,
                updated_at = NOW();", dstConn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("name", dictName);
        cmd.Parameters.AddWithValue("data", xlsxData);
        cmd.ExecuteNonQuery();
    }

    Console.WriteLine($"  Dictionary '{dictName}' imported successfully ({xlsxData.Length / 1024.0 / 1024.0:F2} MB).");
    return;
}

if (args.Any(a => a.Equals("--inspect", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("=== DATABASE INSPECTION ===\n");
    var db = new DbService(null);
    using var conn = new Npgsql.NpgsqlConnection("Host=192.168.1.114;Port=5435;Database=discoverycity;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo");
    conn.Open();

    // Table counts
    foreach (var tbl in new[] { "Sources", "DataSets", "ParentDocuments", "DocumentChunks", "DocumentImages" })
    {
        try {
            using var cmd = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM {tbl}", conn);
            Console.WriteLine($"  {tbl}: {cmd.ExecuteScalar()} rows");
        } catch { Console.WriteLine($"  {tbl}: (not found)"); }
    }

    // Embedding counts
    Console.WriteLine("\n--- Embeddings ---");
    using (var cmd = new Npgsql.NpgsqlCommand(@"
        SELECT 'ParentDocs with embedding' as metric, count(*) FROM ParentDocuments WHERE Embedding IS NOT NULL
        UNION ALL SELECT 'ParentDocs without embedding', count(*) FROM ParentDocuments WHERE Embedding IS NULL
        UNION ALL SELECT 'Chunks with embedding', count(*) FROM DocumentChunks WHERE Embedding IS NOT NULL
        UNION ALL SELECT 'Chunks without embedding', count(*) FROM DocumentChunks WHERE Embedding IS NULL", conn))
    using (var r = cmd.ExecuteReader()) { while (r.Read()) Console.WriteLine($"  {r.GetString(0)}: {r.GetInt64(1)}"); }

    // Column types for Sources (verify UUID)
    Console.WriteLine("\n--- Sources schema ---");
    using (var cmd = new Npgsql.NpgsqlCommand("SELECT column_name, data_type FROM information_schema.columns WHERE table_name='sources' ORDER BY ordinal_position", conn))
    using (var r = cmd.ExecuteReader()) { while (r.Read()) Console.WriteLine($"  {r.GetString(0)}: {r.GetString(1)}"); }

    // Sample documents
    Console.WriteLine("\n--- Sample ParentDocuments (first 5) ---");
    using (var cmd = new Npgsql.NpgsqlCommand("SELECT Id, FileName, DataSetId, jsonb_array_length(COALESCE(Sentences,'[]'::jsonb)) as sent_count, jsonb_array_length(COALESCE(SentenceIds,'[]'::jsonb)) as id_count FROM ParentDocuments ORDER BY ProcessedAt DESC LIMIT 5", conn))
    using (var r = cmd.ExecuteReader()) {
        while (r.Read()) {
            var id = r.GetGuid(0);
            var fn = r.GetString(1);
            var dsId = r.IsDBNull(2) ? "null" : r.GetGuid(2).ToString()[..8];
            var sentCount = r.GetInt32(3);
            var idCount = r.GetInt32(4);
            Console.WriteLine($"  {id.ToString()[..8]}.. {fn,-40} ds={dsId} sentences={sentCount} sentenceIds={idCount}");
        }
    }

    // Sample sentence + ID pairing
    Console.WriteLine("\n--- Sample sentence/ID pairs (from latest doc) ---");
    using (var cmd = new Npgsql.NpgsqlCommand(@"
        SELECT Sentences->0, Sentences->1, Sentences->2,
               SentenceIds->0, SentenceIds->1, SentenceIds->2
        FROM ParentDocuments WHERE SentenceIds IS NOT NULL AND jsonb_array_length(SentenceIds) > 0
        ORDER BY ProcessedAt DESC LIMIT 1", conn))
    using (var r = cmd.ExecuteReader()) {
        if (r.Read()) {
            for (int i = 0; i < 3; i++) {
                var sent = r.IsDBNull(i) ? "(null)" : r.GetString(i);
                var sid = r.IsDBNull(i+3) ? "(null)" : r.GetString(i+3);
                if (sent.Length > 80) sent = sent[..80] + "...";
                Console.WriteLine($"  [{i}] ID={sid}");
                Console.WriteLine($"      Text={sent}");
            }
        } else Console.WriteLine("  (no documents with SentenceIds)");
    }

    // Vector column dimensions
    Console.WriteLine("\n--- Vector columns ---");
    using (var cmd = new Npgsql.NpgsqlCommand(@"
        SELECT table_name, column_name, udt_name,
               CASE WHEN udt_name = 'vector' THEN
                 (SELECT atttypmod FROM pg_attribute a JOIN pg_class c ON a.attrelid=c.oid
                  WHERE c.relname=columns.table_name AND a.attname=columns.column_name)
               END as dim
        FROM information_schema.columns
        WHERE table_schema='public' AND udt_name='vector'
        ORDER BY table_name", conn))
    using (var r = cmd.ExecuteReader()) {
        while (r.Read()) {
            var dim = r.IsDBNull(3) ? "?" : r.GetInt32(3).ToString();
            Console.WriteLine($"  {r.GetString(0)}.{r.GetString(1)}: vector({dim})");
        }
    }

    // Dictionary packages
    Console.WriteLine("\n--- Dictionary Packages ---");
    using (var cmd = new Npgsql.NpgsqlCommand("SELECT dictionary_name, entry_count, pg_size_pretty(length(package_data)::bigint) as size, created_at FROM dictionary_packages ORDER BY dictionary_name", conn))
    using (var r = cmd.ExecuteReader()) {
        if (!r.HasRows) Console.WriteLine("  (none — run --import-dictionary to load OED from BookCity)");
        while (r.Read()) {
            var entries = r.IsDBNull(1) ? "?" : r.GetInt32(1).ToString("N0");
            Console.WriteLine($"  {r.GetString(0)}: {entries} entries, {r.GetString(2)}, imported {r.GetDateTime(3):yyyy-MM-dd HH:mm}");
        }
    }

    // DataSet breakdown
    Console.WriteLine("\n--- DataSets ---");
    using (var cmd = new Npgsql.NpgsqlCommand("SELECT ds.Name, count(p.Id) FROM DataSets ds LEFT JOIN ParentDocuments p ON p.DataSetId = ds.Id GROUP BY ds.Name ORDER BY count DESC", conn))
    using (var r = cmd.ExecuteReader()) { while (r.Read()) Console.WriteLine($"  {r.GetString(0)}: {r.GetInt64(1)} docs"); }

    Console.WriteLine("\n=== DONE ===");
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
        Console.WriteLine("⚠ --reset-db: Dropping all tables for UUID v7 schema migration...");
        var resetService = new DbService(embeddingService);
        resetService.DropAllTables();
        Console.WriteLine("All tables dropped. Recreating with UUID schema...");
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
// EXPORT SIGNATURES MODE: High-speed ingestion indexing pipeline
// ============================================================
if (exportSignatures)
{
    Console.WriteLine("\n--- EXPORT SIGNATURES MODE ---");
    // Connect using DbService string or default
    string connStr = "Host=192.168.1.114;Port=5435;Database=discoverycity;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo";
    string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "signatures.bin");
    
    var manager = new IngestionManager(connStr, binPath);
    await manager.RunAsync();
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

// Add remaining folders only when no explicit datasets were specified on the command line
if (!explicitDataSets)
{
    foreach (var dir in subDirs
        .OrderBy(d => Regex.Replace(Path.GetFileName(d) ?? "", @"\d+", m => m.Value.PadLeft(10, '0'))))
    {
        if (!addedFolders.Contains(dir)) targetFolders.Add(dir);
    }
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
        { ".pdf", ".xlsx", ".xls", ".csv", ".avi", ".mp4", ".vob", ".mov", ".mkv", ".wmv", ".m4a", ".mp3", ".wav", ".aac", ".ogg", ".flac",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".cr2", ".webp" };
    var allFiles = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Published" + Path.DirectorySeparatorChar))
        .Where(f => supportedExtensions.Contains(Path.GetExtension(f)))
        .ToArray();

    // Process files concurrently using pipeline
    int maxDegreeOfParallelism = 3; // optimal proven by sampling (1/2/3/5/10 tested)
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
                .AddStep(new WordSplitStep())
                .AddStep(new AssembleDocumentStep())
                .AddStep(new TextEnhanceStep())
                .AddStep(new MetadataExtractionStep())
                .AddStep(new SentenceIdStep())
                .AddStep(new ThumbnailStep())
                .AddStep(new VideoProxyStep())
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
