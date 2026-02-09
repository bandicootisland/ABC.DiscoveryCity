using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Extensibility;
using Telerik.Windows.Documents.Fixed.FormatProviders.Pdf;
using Telerik.Windows.Documents.Core.Fonts;
using Telerik.Documents.Fixed.FormatProviders.Image.Skia;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Generates thumbnails directly from RadFixedDocument using Telerik's SkiaSharp renderer.
/// No browser required - pure server-side rendering.
/// </summary>
public class TelerikThumbnailService
{
    private readonly SkiaImageFormatProvider _imageProvider;

    public TelerikThumbnailService()
    {
        // Set up fonts provider for cross-platform rendering
        if (FixedExtensibilityManager.FontsProvider == null)
        {
            FixedExtensibilityManager.FontsProvider = new WindowsFontsProvider();
            Console.WriteLine("  Telerik FontsProvider initialized");
        }

        _imageProvider = new SkiaImageFormatProvider();

        // Configure export settings for quality rendering
        _imageProvider.ExportSettings.ImageFormat = SkiaImageFormat.Jpeg;
        _imageProvider.ExportSettings.Quality = 90;
        _imageProvider.ExportSettings.ScaleFactor = 1.0;

        Console.WriteLine("  SkiaImageFormatProvider configured (JPEG, Q90, Scale 1.0)");
    }

    /// <summary>
    /// Generates thumbnail images for the first page of a PDF document.
    /// </summary>
    /// <param name="document">The RadFixedDocument (not used - we reload for image export)</param>
    /// <param name="pdfPath">Original PDF path</param>
    /// <param name="pageIndex">Which page to render (0-based, default 0 for first page)</param>
    /// <returns>Tuple of (thumbPath, fullPath) for the generated images</returns>
    public (string ThumbPath, string FullPath) GenerateThumbnails(RadFixedDocument document, string pdfPath, int pageIndex = 0)
    {
        // Import PDF fresh for image export (as per Telerik example)
        var pdfProvider = new PdfFormatProvider();
        RadFixedDocument freshDoc;

        using (var fileStream = File.OpenRead(pdfPath))
        {
            freshDoc = pdfProvider.Import(fileStream);
        }

        if (freshDoc.Pages.Count == 0)
        {
            Console.WriteLine($"  No pages in document: {pdfPath}");
            return (string.Empty, string.Empty);
        }

        if (pageIndex >= freshDoc.Pages.Count)
        {
            pageIndex = 0;
        }

        var page = freshDoc.Pages[pageIndex];
        string basePath = Path.ChangeExtension(pdfPath, null);
        string fullPath = $"{basePath}_page{pageIndex + 1}_full.jpg";
        string thumbPath = $"{basePath}_page{pageIndex + 1}.jpg";

        try
        {
            // Export page to image bytes using Skia
            byte[] imageBytes = _imageProvider.Export(page);

            // Load with ImageSharp for resizing and format conversion
            using var stream = new MemoryStream(imageBytes);
            using var image = Image.Load(stream);

            // Save full-size image
            image.SaveAsJpeg(fullPath);

            // Create thumbnail (100px width, maintain aspect ratio)
            int thumbWidth = 100;
            int thumbHeight = (int)Math.Round(image.Height * (thumbWidth / (double)image.Width));

            using var thumb = image.Clone(ctx => ctx.Resize(thumbWidth, thumbHeight));
            thumb.SaveAsJpeg(thumbPath);

            Console.WriteLine($"  Telerik rendered: {thumbWidth}x{thumbHeight} ({Path.GetFileName(thumbPath)})");

            return (thumbPath, fullPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Telerik render failed: {ex.Message}");
            return (string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Async wrapper for parallel processing
    /// </summary>
    public Task<(string ThumbPath, string FullPath)> GenerateThumbnailsAsync(RadFixedDocument document, string pdfPath, int pageIndex = 0)
    {
        return Task.Run(() => GenerateThumbnails(document, pdfPath, pageIndex));
    }
}

/// <summary>
/// Provides font data from Windows system fonts folder
/// </summary>
public class WindowsFontsProvider : FontsProviderBase
{
    private static readonly string FontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private static readonly Dictionary<string, byte[]> FontCache = new();

    public override byte[]? GetFontData(FontProperties fontProperties)
    {
        string fontFamily = fontProperties.FontFamilyName?.ToLowerInvariant() ?? "arial";

        // Try to find a matching font file
        string[] possibleNames = fontFamily switch
        {
            "arial" => new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" },
            "times new roman" => new[] { "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf" },
            "courier new" => new[] { "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf" },
            "calibri" => new[] { "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf" },
            _ => new[] { "arial.ttf" }
        };

        foreach (var fontFile in possibleNames)
        {
            string fontPath = Path.Combine(FontsFolder, fontFile);
            if (File.Exists(fontPath))
            {
                if (!FontCache.TryGetValue(fontPath, out var data))
                {
                    data = File.ReadAllBytes(fontPath);
                    FontCache[fontPath] = data;
                }
                return data;
            }
        }

        // Ultimate fallback to Arial
        string fallbackPath = Path.Combine(FontsFolder, "arial.ttf");
        if (File.Exists(fallbackPath))
        {
            if (!FontCache.TryGetValue(fallbackPath, out var data))
            {
                data = File.ReadAllBytes(fallbackPath);
                FontCache[fallbackPath] = data;
            }
            return data;
        }

        return null;
    }
}
