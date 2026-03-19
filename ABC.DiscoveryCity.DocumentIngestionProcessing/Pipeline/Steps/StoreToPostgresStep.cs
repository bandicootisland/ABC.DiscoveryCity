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

        // 3. Store XLSX package
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
}
