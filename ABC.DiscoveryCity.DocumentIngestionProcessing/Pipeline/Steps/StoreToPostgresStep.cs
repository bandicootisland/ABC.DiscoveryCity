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

        // Insert document into database
        try
        {
            lock (ctx.DbService)
            {
                ctx.DbService.InsertDocument(ctx.PublishedFilePath, ctx.Metadata, ctx.DataSetId,
                    ctx.SentenceIds.Count > 0 ? ctx.SentenceIds : null);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DB Error: {ex.Message}");
        }

        // Store images if generated
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
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Image DB error: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }
}
