using ABC.DiscoveryCity.DevExpressProcessing;
using ABC.DiscoveryCity.TelerikProcessing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

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
            // Single DevExpress render of page 1 → preview + thumbnail
            RenderPreviewAndThumb(ctx);
        }
        finally
        {
            ctx.ImageThrottle?.Release();
        }
    }

    private void RenderPreviewAndThumb(IngestionContext ctx)
    {
        try
        {
            byte[] pdfBytes = ctx.PdfBytes ?? File.ReadAllBytes(ctx.FilePath);
            var renderer = new DevExpressPdfPageRenderer();
            byte[]? pageData = renderer.RenderFirstPage(pdfBytes, imageScaleFactor: 0.5f);
            if (pageData == null || pageData.Length == 0) return;

            using var img = Image.Load(pageData);
            int w = img.Width;
            int h = img.Height;

            // Save full preview
            string baseName = Path.GetFileNameWithoutExtension(ctx.FilePath);
            string fullPath = Path.Combine(ctx.OutputDir, $"{baseName}_page1.png");
            File.WriteAllBytes(fullPath, pageData);
            ctx.FullImagePath = fullPath;
            ctx.FullImageWidth = w;
            ctx.FullImageHeight = h;
            ctx.FullImageData = pageData;

            // Generate thumb by resizing from the same render
            int thumbW = 100;
            int thumbH = w > 0 ? (int)(100.0 * h / w) : 0;
            using var thumbImg = img.Clone(x => x.Resize(thumbW, thumbH));
            using var thumbMs = new MemoryStream();
            thumbImg.SaveAsPng(thumbMs);
            byte[] thumbData = thumbMs.ToArray();

            string thumbPath = Path.Combine(ctx.OutputDir, $"{baseName}_page1_thumb.png");
            File.WriteAllBytes(thumbPath, thumbData);
            ctx.ThumbImagePath = thumbPath;
            ctx.ThumbImageWidth = thumbW;
            ctx.ThumbImageHeight = thumbH;
            ctx.ThumbImageData = thumbData;

            Console.WriteLine($"  [THUMB] {w}x{h} → {thumbW}x{thumbH}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Thumbnail render failed: {ex.Message}");
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
