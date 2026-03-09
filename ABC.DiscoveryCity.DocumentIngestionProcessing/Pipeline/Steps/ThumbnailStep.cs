using ABC.DiscoveryCity.TelerikProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class ThumbnailStep : IIngestionStep
{
    public string Name => "Thumbnail";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.NoImages) return Task.CompletedTask;

        switch (ctx.Category)
        {
            case FileCategory.Pdf:
                return GeneratePdfThumbnailAsync(ctx);
            case FileCategory.Spreadsheet:
                GenerateSpreadsheetThumbnail(ctx);
                return Task.CompletedTask;
            case FileCategory.Video:
                GenerateMediaThumbnail(ctx);
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private async Task GeneratePdfThumbnailAsync(IngestionContext ctx)
    {
        if (ctx.ImageThrottle != null)
            await ctx.ImageThrottle.WaitAsync();

        try
        {
            if (ctx.PdfImageExtractor != null)
            {
                var (fPath, tPath, w, h, pData, tData) =
                    ctx.PdfImageExtractor.ExtractPageImage(ctx.FilePath, outputDir: ctx.PublishedDir);

                if (!string.IsNullOrEmpty(fPath))
                {
                    ctx.FullImagePath = fPath;
                    ctx.FullImageWidth = w;
                    ctx.FullImageHeight = h;
                    ctx.FullImageData = pData;
                }
                if (!string.IsNullOrEmpty(tPath))
                {
                    ctx.ThumbImagePath = tPath;
                    ctx.ThumbImageWidth = 100;
                    ctx.ThumbImageHeight = h > 0 && w > 0 ? (int)(100.0 * h / w) : 0;
                    ctx.ThumbImageData = tData;
                }
            }
            else if (ctx.ThumbnailService != null)
            {
                var pageImages = await ctx.ThumbnailService.GeneratePageImagesAsync(ctx.FilePath);
                foreach (var (filePath, width, height, imgData) in pageImages)
                {
                    if (filePath.Contains("_thumb."))
                    {
                        ctx.ThumbImagePath = filePath;
                        ctx.ThumbImageWidth = width;
                        ctx.ThumbImageHeight = height;
                        ctx.ThumbImageData = imgData;
                    }
                    else
                    {
                        ctx.FullImagePath = filePath;
                        ctx.FullImageWidth = width;
                        ctx.FullImageHeight = height;
                        ctx.FullImageData = imgData;
                    }
                }
            }
        }
        finally
        {
            ctx.ImageThrottle?.Release();
        }
    }

    private void GenerateSpreadsheetThumbnail(IngestionContext ctx)
    {
        var result = ctx.SpreadsheetResult;
        if (result == null || result.Rows.Count == 0) return;

        try
        {
            string baseName = Path.GetFileNameWithoutExtension(ctx.FilePath);
            var (fullPath, thumbPath, fullW, fullH, thumbW, thumbH, fullData, thumbData) =
                result.Workbook != null
                    ? SpreadsheetThumbnail.GenerateFromWorkbook(result.Workbook, ctx.OutputDir, baseName)
                    : SpreadsheetThumbnail.GenerateAndSave(result, ctx.OutputDir, baseName);

            if (!string.IsNullOrEmpty(fullPath))
            {
                ctx.FullImagePath = fullPath;
                ctx.FullImageWidth = fullW;
                ctx.FullImageHeight = fullH;
                ctx.FullImageData = fullData;
                ctx.ThumbImagePath = thumbPath;
                ctx.ThumbImageWidth = thumbW;
                ctx.ThumbImageHeight = thumbH;
                ctx.ThumbImageData = thumbData;
                Console.WriteLine($"  [THUMB] {baseName}: {fullW}x{fullH} preview, {thumbW}x{thumbH} thumb");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Spreadsheet thumbnail error: {ex.Message}");
        }
    }

    private void GenerateMediaThumbnail(IngestionContext ctx)
    {
        if (!MediaFileProcessor.IsVideoFile(ctx.FilePath)) return;

        string thumbDir = ctx.OutputDir;
        string baseName = Path.GetFileNameWithoutExtension(ctx.FilePath);
        string? thumbPath = MediaFileProcessor.CaptureThumbnail(ctx.FilePath, thumbDir, baseName);
        if (thumbPath != null)
        {
            ctx.ThumbImagePath = thumbPath;
            ctx.ThumbImageWidth = 100;
            ctx.ThumbImageHeight = 0;
        }
    }
}
