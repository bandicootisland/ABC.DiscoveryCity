using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Telerik.Windows.Documents.Extensibility;
using Telerik.Documents.Fixed.FormatProviders.Image.Skia;
using Telerik.Windows.Documents.Spreadsheet.Model;
using SpreadPdfProvider = Telerik.Windows.Documents.Spreadsheet.FormatProviders.Pdf.PdfFormatProvider;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Generates thumbnails for spreadsheets.
/// Primary path: renders a real screenshot via Workbook → PDF → Skia image.
/// Fallback (CSV): synthetic grid heatmap showing cell density.
/// </summary>
public static class SpreadsheetThumbnail
{
    /// <summary>
    /// Generate a real screenshot thumbnail from a Workbook by exporting to PDF
    /// and rendering the first page to an image via SkiaImageFormatProvider.
    /// </summary>
    public static (string FullPath, string ThumbPath, int FullW, int FullH,
                    int ThumbW, int ThumbH, byte[] FullData, byte[] ThumbData)
        GenerateFromWorkbook(Workbook workbook, string outputDir, string baseName)
    {
        // Ensure fonts provider is set (same pattern as PdfImageExtractor)
        if (FixedExtensibilityManager.FontsProvider == null)
            FixedExtensibilityManager.FontsProvider = new WindowsFontsProvider();

        // 1. Export Workbook → RadFixedDocument (in-memory, no temp files)
        var pdfProvider = new SpreadPdfProvider();
        var fixedDoc = pdfProvider.ExportToFixedDocument(workbook, TimeSpan.FromSeconds(30));

        if (fixedDoc.Pages.Count == 0)
            return (string.Empty, string.Empty, 0, 0, 0, 0, Array.Empty<byte>(), Array.Empty<byte>());

        // 2. Render first page → PNG bytes via Skia
        var imageProvider = new SkiaImageFormatProvider();
        byte[] pngBytes = imageProvider.Export(fixedDoc.Pages[0], TimeSpan.FromSeconds(10));

        if (pngBytes.Length == 0)
            return (string.Empty, string.Empty, 0, 0, 0, 0, Array.Empty<byte>(), Array.Empty<byte>());

        // 3. Load PNG into ImageSharp for resize + JPEG conversion
        using var image = Image.Load<Rgba32>(pngBytes);

        // Resize preview to max 400px wide (matches PdfImageExtractor convention)
        if (image.Width > 400)
        {
            int previewH = (int)Math.Round(image.Height * (400.0 / image.Width));
            image.Mutate(ctx => ctx.Resize(400, previewH));
        }

        int fullW = image.Width;
        int fullH = image.Height;

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        string fullPath = Path.Combine(outputDir, $"{baseName}_page1.jpg");
        string thumbPath = Path.Combine(outputDir, $"{baseName}_page1_thumb.jpg");

        // Save preview JPEG
        byte[] fullData;
        using (var ms = new MemoryStream())
        {
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 70 });
            fullData = ms.ToArray();
        }
        File.WriteAllBytes(fullPath, fullData);

        // 4. Create thumbnail (100px wide)
        int thumbW = 100;
        int thumbH = (int)Math.Round(image.Height * (100.0 / image.Width));

        byte[] thumbData;
        using (var thumb = image.Clone(ctx => ctx.Resize(thumbW, thumbH)))
        {
            using var ms = new MemoryStream();
            thumb.SaveAsJpeg(ms, new JpegEncoder { Quality = 75 });
            thumbData = ms.ToArray();
        }
        File.WriteAllBytes(thumbPath, thumbData);

        return (fullPath, thumbPath, fullW, fullH, thumbW, thumbH, fullData, thumbData);
    }

    // --- Synthetic heatmap fallback (used for CSV files) ---

    private static readonly Rgba32 Background = new(255, 255, 255);
    private static readonly Rgba32 GridLine = new(220, 220, 220);
    private static readonly Rgba32 HeaderBg = new(66, 133, 244);       // Blue header row
    private static readonly Rgba32 CellFilled = new(200, 220, 245);    // Light blue for content
    private static readonly Rgba32 CellDense = new(140, 180, 230);     // Darker blue for dense content
    private static readonly Rgba32 SheetTab = new(100, 160, 220);      // Sheet tab indicator

    /// <summary>
    /// Generate a synthetic heatmap preview from a SpreadsheetResult (CSV fallback).
    /// Returns (fullBytes, thumbBytes, fullWidth, fullHeight, thumbWidth, thumbHeight).
    /// </summary>
    public static (byte[] FullData, byte[] ThumbData, int FullW, int FullH, int ThumbW, int ThumbH)
        Generate(SpreadsheetResult result, int maxRows = 40, int maxCols = 15)
    {
        if (result.Rows.Count == 0)
            return (Array.Empty<byte>(), Array.Empty<byte>(), 0, 0, 0, 0);

        // Determine grid dimensions from actual data
        int dataRows = Math.Min(result.Rows.Count, maxRows);
        int dataCols = DetectColumnCount(result, maxCols);

        // Image dimensions
        int cellW = 50;
        int cellH = 18;
        int headerH = 22;
        int margin = 8;
        int sheetTabH = result.SheetCount > 1 ? 20 : 0;

        int imgW = margin * 2 + dataCols * cellW;
        int imgH = margin * 2 + headerH + dataRows * cellH + sheetTabH;

        // Clamp to reasonable size
        imgW = Math.Clamp(imgW, 200, 800);
        imgH = Math.Clamp(imgH, 150, 600);

        using var image = new Image<Rgba32>(imgW, imgH);

        // Fill background
        FillRect(image, 0, 0, imgW, imgH, Background);

        // Draw header row
        FillRect(image, margin, margin, imgW - margin * 2, headerH, HeaderBg);

        // Draw data rows
        int startY = margin + headerH;
        string? currentSheet = null;
        int rowIndex = 0;

        foreach (var row in result.Rows.Take(maxRows))
        {
            if (currentSheet != null && row.SheetName != currentSheet)
                break; // Only render first sheet in thumbnail

            currentSheet = row.SheetName;
            int y = startY + rowIndex * cellH;
            if (y + cellH > imgH - margin - sheetTabH) break;

            // Parse cells from pipe-separated text
            var cells = row.Text.Split(" | ");
            for (int col = 0; col < Math.Min(cells.Length, dataCols); col++)
            {
                int x = margin + col * cellW;
                if (x + cellW > imgW - margin) break;

                string cellText = cells[col].Trim();
                if (!string.IsNullOrEmpty(cellText))
                {
                    // Color intensity based on content length
                    var color = cellText.Length > 15 ? CellDense : CellFilled;
                    FillRect(image, x + 1, y + 1, cellW - 2, cellH - 2, color);
                }
            }

            // Draw horizontal grid line
            FillRect(image, margin, y + cellH, imgW - margin * 2, 1, GridLine);
            rowIndex++;
        }

        // Draw vertical grid lines
        for (int col = 0; col <= dataCols; col++)
        {
            int x = margin + col * cellW;
            if (x > imgW - margin) break;
            FillRect(image, x, margin, 1, startY - margin + rowIndex * cellH, GridLine);
        }

        // Draw sheet tabs at bottom if multiple sheets
        if (result.SheetCount > 1)
        {
            int tabY = imgH - sheetTabH;
            var sheetNames = result.Rows.Select(r => r.SheetName).Distinct().Take(5).ToList();
            int tabX = margin;
            foreach (var name in sheetNames)
            {
                int tabW = Math.Min(name.Length * 7 + 16, 120);
                FillRect(image, tabX, tabY + 2, tabW, sheetTabH - 4, SheetTab);
                tabX += tabW + 4;
            }
        }

        // Export full image
        byte[] fullData;
        using (var ms = new MemoryStream())
        {
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 85 });
            fullData = ms.ToArray();
        }

        // Generate thumbnail (100px wide)
        int thumbW = 100;
        int thumbH = (int)(100.0 * imgH / imgW);
        byte[] thumbData;
        using (var thumb = image.Clone(ctx => ctx.Resize(thumbW, thumbH)))
        using (var ms = new MemoryStream())
        {
            thumb.SaveAsJpeg(ms, new JpegEncoder { Quality = 75 });
            thumbData = ms.ToArray();
        }

        return (fullData, thumbData, imgW, imgH, thumbW, thumbH);
    }

    /// <summary>
    /// Generate and save thumbnail files to disk. Returns (fullPath, thumbPath).
    /// </summary>
    public static (string FullPath, string ThumbPath, int FullW, int FullH, int ThumbW, int ThumbH, byte[] FullData, byte[] ThumbData)
        GenerateAndSave(SpreadsheetResult result, string outputDir, string baseName)
    {
        var (fullData, thumbData, fullW, fullH, thumbW, thumbH) = Generate(result);
        if (fullData.Length == 0)
            return (string.Empty, string.Empty, 0, 0, 0, 0, Array.Empty<byte>(), Array.Empty<byte>());

        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        string fullPath = Path.Combine(outputDir, $"{baseName}_page1.jpg");
        string thumbPath = Path.Combine(outputDir, $"{baseName}_page1_thumb.jpg");

        File.WriteAllBytes(fullPath, fullData);
        File.WriteAllBytes(thumbPath, thumbData);

        return (fullPath, thumbPath, fullW, fullH, thumbW, thumbH, fullData, thumbData);
    }

    private static int DetectColumnCount(SpreadsheetResult result, int max)
    {
        int maxCells = 0;
        foreach (var row in result.Rows.Take(50))
        {
            int count = row.Text.Split(" | ").Length;
            if (count > maxCells) maxCells = count;
        }
        return Math.Clamp(maxCells, 1, max);
    }

    private static void FillRect(Image<Rgba32> image, int x, int y, int w, int h, Rgba32 color)
    {
        int maxX = Math.Min(x + w, image.Width);
        int maxY = Math.Min(y + h, image.Height);
        int startX = Math.Max(x, 0);
        int startY = Math.Max(y, 0);

        image.ProcessPixelRows(accessor =>
        {
            for (int py = startY; py < maxY; py++)
            {
                var row = accessor.GetRowSpan(py);
                for (int px = startX; px < maxX; px++)
                {
                    row[px] = color;
                }
            }
        });
    }
}
