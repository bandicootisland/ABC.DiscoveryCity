using System.IO.Compression;
using System.Text.Json;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class StoreToPostgresStep : IIngestionStep
{
    public string Name => "StoreToPostgres";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.Metadata == null)
            throw new InvalidOperationException("Metadata must be populated before storing to database.");

        // Save JSON metadata file
        string dateStr = ctx.Metadata.DeducedDate != DateTime.MinValue
            ? ctx.Metadata.DeducedDate.ToString("yyyy-MM-dd")
            : "UnknownDate";
        string newFileNameBase = $"{Path.GetFileNameWithoutExtension(ctx.FilePath)}_{dateStr}";
        string jsonPath = Path.Combine(ctx.OutputDir, newFileNameBase + ".json");
        string json = JsonSerializer.Serialize(ctx.Metadata, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);

        Guid parentId = Guid.Empty;

        // 1. Insert/update parent document + chunks (existing logic)
        try
        {
            lock (ctx.DbService)
            {
                parentId = ctx.DbService.InsertDocument(ctx.PublishedFilePath, ctx.Metadata, ctx.DataSetId,
                    ctx.SentenceIds.Count > 0 ? ctx.SentenceIds : null);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DB Error: {ex.Message}");
        }

        if (parentId == Guid.Empty) return Task.CompletedTask;

        // 2. Store images (legacy documentimages + new document_previews)
        if (!string.IsNullOrEmpty(ctx.ThumbImagePath) || !string.IsNullOrEmpty(ctx.FullImagePath))
        {
            try
            {
                lock (ctx.DbService)
                {
                    ctx.DbService.UpsertDocumentImages(
                        ctx.PublishedFilePath,
                        ctx.FullImagePath, ctx.FullImageWidth, ctx.FullImageHeight,
                        ctx.ThumbImagePath, ctx.ThumbImageWidth, ctx.ThumbImageHeight,
                        previewData: ctx.FullImageData, thumbData: ctx.ThumbImageData);

                    // New: document_previews (BookCity pattern)
                    ctx.DbService.UpsertDocumentPreview(parentId, "full",
                        ctx.FullImageWidth, ctx.FullImageHeight, ctx.FullImageData);
                    ctx.DbService.UpsertDocumentPreview(parentId, "thumb",
                        ctx.ThumbImageWidth, ctx.ThumbImageHeight, ctx.ThumbImageData);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Image DB error: {ex.Message}");
            }
        }

        // 3. Store package: all non-PDF types use ZIP with data/{filename}, PDFs use XLSX bundle
        if (ctx.VideoBytesForPackage is { Length: > 0 })
        {
            try
            {
                // Video: store as ZIP with data/{name}.mp4 (always mp4 — original or transcoded)
                string videoName = Path.ChangeExtension(ctx.FileName, ".mp4");
                byte[] zipPackage = CreateZipPackage(videoName, ctx.VideoBytesForPackage);
                lock (ctx.DbService)
                {
                    ctx.DbService.UpsertDocumentPackage(parentId, zipPackage);
                }
                Console.WriteLine($"  [DB] Video package: {zipPackage.Length / (1024.0 * 1024):F1} MB ZIP ({(ctx.IsVideoProxy ? "proxy 720p" : "original")})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Video package store error: {ex.Message}");
            }
        }
        else if (ctx.Category == FileCategory.Image && File.Exists(ctx.FilePath))
        {
            try
            {
                byte[] zipPackage = CreateZipPackage(ctx.FileName, File.ReadAllBytes(ctx.FilePath));
                lock (ctx.DbService)
                {
                    ctx.DbService.UpsertDocumentPackage(parentId, zipPackage);
                }
                Console.WriteLine($"  [DB] Image package: {zipPackage.Length / (1024.0 * 1024):F1} MB (ZIP)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Image package store error: {ex.Message}");
            }
        }
        else
        {
            string xlsxPath = Path.Combine(ctx.OutputDir, Path.GetFileNameWithoutExtension(ctx.FilePath) + ".xlsx");
            if (File.Exists(xlsxPath))
            {
                try
                {
                    byte[] xlsxBytes = File.ReadAllBytes(xlsxPath);
                    lock (ctx.DbService)
                    {
                        ctx.DbService.UpsertDocumentPackage(parentId, xlsxBytes);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [WARN] Package store error: {ex.Message}");
                }
            }
        }

        // 4. Store sentences (new relational table)
        var sentences = ctx.EnhancedSentences ?? ctx.DisplaySentences;
        if (sentences.Count > 0)
        {
            try
            {
                lock (ctx.DbService)
                {
                    ctx.DbService.StoreDocumentSentences(parentId, sentences, ctx.SentenceIds, ctx.PageCount);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Sentences store error: {ex.Message}");
            }
        }

        // 5. Store pages
        if (ctx.PageCount > 0)
        {
            try
            {
                string? htmlContent = null;
                string htmlPath = Path.Combine(ctx.OutputDir, Path.GetFileNameWithoutExtension(ctx.FilePath) + ".html");
                if (File.Exists(htmlPath))
                    htmlContent = File.ReadAllText(htmlPath);

                lock (ctx.DbService)
                {
                    ctx.DbService.StoreDocumentPages(parentId, ctx.PageCount, htmlContent, sentences: sentences);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Pages store error: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a minimal ZIP package with the file under data/{filename}.
    /// Same OPC layout as XLSX bundles so the API can extract with the same ZipArchive pattern.
    /// </summary>
    private static byte[] CreateZipPackage(string fileName, byte[] fileBytes)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry($"data/{fileName}", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            entryStream.Write(fileBytes, 0, fileBytes.Length);
        }
        return ms.ToArray();
    }
}
