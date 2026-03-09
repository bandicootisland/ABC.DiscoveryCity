using ABC.DiscoveryCity.DevExpressProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class PageImagesStep : IIngestionStep
{
    public string Name => "PageImages";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.Category != FileCategory.Pdf) return Task.CompletedTask;
        if (ctx.NoImages) return Task.CompletedTask;

        byte[] pdfBytes = ctx.PdfBytes ?? File.ReadAllBytes(ctx.FilePath);
        var renderer = new DevExpressPdfPageRenderer();
        ctx.RenderedPages = renderer.RenderPages(pdfBytes, imageScaleFactor: 1.0f);

        if (ctx.RenderedPages.Count == 0)
        {
            Console.WriteLine("  [WARN] DevExpress rendered 0 pages");
            return Task.CompletedTask;
        }

        // Save page images to pages/ subfolder
        string pagesDir = Path.Combine(ctx.OutputDir, "pages");
        if (!Directory.Exists(pagesDir)) Directory.CreateDirectory(pagesDir);

        foreach (var (pageNumber, imageData) in ctx.RenderedPages)
        {
            string fileName = $"page_{pageNumber:D4}.png";
            string pagePath = Path.Combine(pagesDir, fileName);
            File.WriteAllBytes(pagePath, imageData);
        }

        Console.WriteLine($"  Rendered {ctx.RenderedPages.Count} page images to pages/");

        return Task.CompletedTask;
    }
}
