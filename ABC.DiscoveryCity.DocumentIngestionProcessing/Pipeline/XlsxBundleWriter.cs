using System.IO.Packaging;
using System.Text;
using ABC.DiscoveryCity.PostgreSQL;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline;

/// <summary>
/// Writes a document to an XLSX OPC bundle for DiscoveryCity.
/// Simpler than BookCity's version — no DigitalBook/WordLayers.
///
/// Structure:
///   - Sheet "Metadata": document-level properties
///   - Sheet "Text": one sentence per row
///   - OPC parts:
///     /data/source.pdf — original PDF (small docs only; large PDFs use page images instead)
///     /images/pages/page_NNNN.png — rendered page images (large docs only)
///     /data/viewer.html — standalone HTML viewer
/// </summary>
public static class XlsxBundleWriter
{
    public static void Write(string outputPath, PdfMetadata metadata,
        List<string> sentences, Dictionary<int, byte[]> renderedPages,
        string? htmlContent = null, List<Guid>? sentenceIds = null,
        byte[]? pdfBytes = null)
    {
        // Phase 1: Write XLSX spreadsheet
        using (var doc = SpreadsheetDocument.Create(outputPath, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            uint sheetId = 1;

            AddMetadataSheet(workbookPart, sheets, ref sheetId, metadata, renderedPages.Count);
            AddTextSheet(workbookPart, sheets, ref sheetId, sentences, sentenceIds);

            workbookPart.Workbook.Save();
        }

        // Phase 2: Embed PDF/images and HTML as OPC parts
        using (var package = Package.Open(outputPath, FileMode.Open, FileAccess.ReadWrite))
        {
            int pdfSize = 0;
            int pageCount = 0;

            if (pdfBytes is { Length: > 0 })
            {
                // Small PDF: embed source PDF directly (cheaper than page images)
                pdfSize = EmbedPdf(package, pdfBytes);
            }
            else
            {
                // Large PDF: embed rendered page images instead
                pageCount = EmbedPageImages(package, renderedPages);
            }

            int htmlBytes = 0;
            if (!string.IsNullOrEmpty(htmlContent))
                htmlBytes = EmbedHtml(package, htmlContent);

            if (pdfSize > 0)
                Console.WriteLine($"  [XlsxBundle] source PDF ({pdfSize / 1024}KB), {htmlBytes:N0} bytes HTML");
            else
                Console.WriteLine($"  [XlsxBundle] {pageCount} page images, {htmlBytes:N0} bytes HTML");
        }
    }

    private static void AddMetadataSheet(WorkbookPart workbookPart, Sheets sheets,
        ref uint sheetId, PdfMetadata metadata, int pageImageCount)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(sheetData);

        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId++,
            Name = "Metadata"
        });

        AppendRow(sheetData, "Property", "Value");
        AppendRow(sheetData, "Title", metadata.Title);
        AppendRow(sheetData, "Date", metadata.DeducedDate != DateTime.MinValue
            ? metadata.DeducedDate.ToString("yyyy-MM-dd") : "");
        AppendRow(sheetData, "PageCount", metadata.PageCount.ToString());
        AppendRow(sheetData, "PageImageCount", pageImageCount.ToString());
        AppendRow(sheetData, "TextLength", (metadata.Text?.Sum(s => s.Length) ?? 0).ToString());
        AppendRow(sheetData, "SentenceCount", (metadata.Text?.Count ?? 0).ToString());

        if (metadata.Names != null && metadata.Names.Count > 0)
            AppendRow(sheetData, "People", string.Join("; ", metadata.Names));
        if (metadata.Terms != null && metadata.Terms.Count > 0)
            AppendRow(sheetData, "Terms", string.Join("; ", metadata.Terms));
    }

    private static void AddTextSheet(WorkbookPart workbookPart, Sheets sheets,
        ref uint sheetId, List<string> sentences, List<Guid>? sentenceIds)
    {
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var sheetData = new SheetData();
        worksheetPart.Worksheet = new Worksheet(sheetData);

        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = sheetId++,
            Name = "Text"
        });

        bool hasIds = sentenceIds != null && sentenceIds.Count == sentences.Count;
        AppendRow(sheetData, hasIds ? new[] { "Index", "SentenceId", "Sentence" } : new[] { "Index", "Sentence" });
        for (int i = 0; i < sentences.Count; i++)
        {
            if (hasIds)
                AppendRow(sheetData, (i + 1).ToString(), sentenceIds![i].ToString(), sentences[i]);
            else
                AppendRow(sheetData, (i + 1).ToString(), sentences[i]);
        }
    }

    private static int EmbedPageImages(Package package, Dictionary<int, byte[]> renderedPages)
    {
        if (renderedPages.Count == 0) return 0;

        int count = 0;
        foreach (var (pageNumber, imageData) in renderedPages.OrderBy(p => p.Key))
        {
            if (imageData.Length == 0) continue;

            var uri = PackUriHelper.CreatePartUri(
                new Uri($"images/pages/page_{pageNumber:D4}.png", UriKind.Relative));
            var part = package.CreatePart(uri, "image/png", CompressionOption.Maximum);
            using var stream = part.GetStream(FileMode.Create);
            stream.Write(imageData, 0, imageData.Length);
            count++;
        }
        return count;
    }

    private static int EmbedPdf(Package package, byte[] pdfBytes)
    {
        var uri = PackUriHelper.CreatePartUri(new Uri("data/source.pdf", UriKind.Relative));
        var part = package.CreatePart(uri, "application/pdf", CompressionOption.Maximum);
        using var stream = part.GetStream(FileMode.Create);
        stream.Write(pdfBytes, 0, pdfBytes.Length);
        return pdfBytes.Length;
    }

    private static int EmbedHtml(Package package, string htmlContent)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(htmlContent);
        var uri = PackUriHelper.CreatePartUri(new Uri("data/viewer.html", UriKind.Relative));
        var part = package.CreatePart(uri, "text/html; charset=utf-8", CompressionOption.Maximum);
        using var stream = part.GetStream(FileMode.Create);
        stream.Write(bytes, 0, bytes.Length);
        return bytes.Length;
    }

    private static void AppendRow(SheetData sheetData, params string[] values)
    {
        var row = new Row();
        foreach (var val in values)
        {
            row.Append(new Cell
            {
                DataType = CellValues.String,
                CellValue = new CellValue(SanitizeForXml(val ?? ""))
            });
        }
        sheetData.Append(row);
    }

    private static string SanitizeForXml(string input)
    {
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                continue;
            return BuildFiltered(input, i);
        }
        return input;
    }

    private static string BuildFiltered(string input, int firstBad)
    {
        var sb = new StringBuilder(input.Length);
        sb.Append(input, 0, firstBad);
        for (int i = firstBad; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\t' || c == '\n' || c == '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD))
                sb.Append(c);
        }
        return sb.ToString();
    }
}
