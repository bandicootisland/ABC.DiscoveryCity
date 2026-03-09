using System.Net;
using System.Text;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class HtmlViewerStep : IIngestionStep
{
    public string Name => "HtmlViewer";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.Category != FileCategory.Pdf) return Task.CompletedTask;
        string baseName = Path.GetFileNameWithoutExtension(ctx.FilePath);
        string title = ctx.Metadata?.Title ?? baseName;
        string htmlPath = Path.Combine(ctx.OutputDir, baseName + ".html");

        // Check for page images: in-memory first, then on disk
        var diskPages = FindDiskPageImages(ctx.OutputDir);
        bool hasPages = ctx.RenderedPages.Count > 0 || diskPages.Count > 0;

        if (!hasPages && string.IsNullOrWhiteSpace(ctx.FullText)) return Task.CompletedTask;

        string html;
        if (ctx.RenderedPages.Count > 0)
            html = BuildPageImageHtml(ctx.RenderedPages, title);
        else if (diskPages.Count > 0)
            html = BuildPageImageHtmlFromDisk(diskPages, title);
        else
            html = BuildTextOnlyHtml(ctx.FullText, title);

        File.WriteAllText(htmlPath, html);
        Console.WriteLine($"  HTML viewer: {baseName}.html");

        return Task.CompletedTask;
    }

    private static string BuildPageImageHtml(Dictionary<int, byte[]> pages, string title)
    {
        var body = new StringBuilder();
        foreach (var (pageNumber, _) in pages.OrderBy(p => p.Key))
        {
            string imgSrc = $"pages/page_{pageNumber:D4}.png";
            body.Append("<div class=\"page\">");
            if (pageNumber == 1)
            {
                body.Append($"<div class=\"page-hdr\">{WebUtility.HtmlEncode(title)}</div>");
            }
            body.AppendLine($"<img src=\"{imgSrc}\" alt=\"Page {pageNumber}\" loading=\"lazy\"></div>");
        }

        return RenderTemplate(body.ToString(), title);
    }

    private static List<(int PageNumber, string RelativePath)> FindDiskPageImages(string outputDir)
    {
        string pagesDir = Path.Combine(outputDir, "pages");
        if (!Directory.Exists(pagesDir)) return new();

        return Directory.GetFiles(pagesDir, "page_*.png")
            .Select(f =>
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string numStr = name.Replace("page_", "");
                return int.TryParse(numStr, out int num) ? (num, $"pages/{Path.GetFileName(f)}") : (0, "");
            })
            .Where(x => x.Item1 > 0)
            .OrderBy(x => x.Item1)
            .ToList();
    }

    private static string BuildPageImageHtmlFromDisk(List<(int PageNumber, string RelativePath)> pages, string title)
    {
        var body = new StringBuilder();
        foreach (var (pageNumber, relativePath) in pages)
        {
            body.Append("<div class=\"page\">");
            if (pageNumber == 1)
                body.Append($"<div class=\"page-hdr\">{WebUtility.HtmlEncode(title)}</div>");
            body.AppendLine($"<img src=\"{relativePath}\" alt=\"Page {pageNumber}\" loading=\"lazy\"></div>");
        }
        return RenderTemplate(body.ToString(), title);
    }

    private static string BuildTextOnlyHtml(string fullText, string title)
    {
        var body = new StringBuilder();
        body.Append($"<h1>{WebUtility.HtmlEncode(title)}</h1>");
        foreach (string line in fullText.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                body.AppendLine($"<p>{WebUtility.HtmlEncode(trimmed)}</p>");
        }

        return RenderTemplate(body.ToString(), title);
    }

    private const string Template = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="UTF-8">
        <title>{{TITLE}}</title>
        <style>
        body { font-family: 'Segoe UI', Arial, sans-serif; margin: 0; padding: 20px; background: #f0f0f0; display: flex; flex-direction: column; align-items: center; }
        .page { margin: 0 0 20px; background: #fff; box-shadow: 0 2px 8px rgba(0,0,0,0.15); }
        .page img { display: block; max-width: 100%; }
        .page-hdr { background: #333; color: #fff; padding: 6px 12px; font-size: 12px; font-family: monospace; }
        h1 { font-size: 1.4em; margin: 20px 0; }
        p { max-width: 800px; line-height: 1.6; margin: 4px 0; }
        </style>
        </head>
        <body>
        {{BODY}}
        </body>
        </html>
        """;

    private static string RenderTemplate(string body, string title) =>
        Template
            .Replace("{{TITLE}}", WebUtility.HtmlEncode(title))
            .Replace("{{BODY}}", body);
}
