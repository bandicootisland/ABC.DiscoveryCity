using DevExpress.Pdf;

namespace ABC.DiscoveryCity.DevExpressProcessing;

public class DevExpressPdfPageRenderer
{
    public Dictionary<int, byte[]> RenderPages(byte[] pdfBytes, float imageScaleFactor = 1.0f, int? maxPages = null)
    {
        var renderedPages = new Dictionary<int, byte[]>();

        using var msPdf = new MemoryStream(pdfBytes);
        using var processor = new PdfDocumentProcessor();
        processor.LoadDocument(msPdf);
        int pageCount = maxPages.HasValue
            ? Math.Min(processor.Document.Pages.Count, maxPages.Value)
            : processor.Document.Pages.Count;

        for (int p = 0; p < pageCount; p++)
        {
            int pageNumber = p + 1;

            try
            {
                int dpi = (int)Math.Max(72, Math.Round(96.0 * imageScaleFactor));
                var renderParams = PdfPageRenderingParameters.CreateWithResolution(dpi);

                using var bitmap = processor.CreateDXBitmap(pageNumber, renderParams);
                using var ms = new MemoryStream();
                bitmap.Save(ms, DevExpress.Drawing.DXImageFormat.Png);
                renderedPages[pageNumber] = ms.ToArray();

                if (pageNumber % 10 == 0)
                    Console.WriteLine($"    DevExpress rendered page {pageNumber}/{pageCount}...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WARN] Could not render page {pageNumber}: {ex.Message}");
            }
        }

        return renderedPages;
    }

    public byte[]? RenderFirstPage(byte[] pdfBytes, float imageScaleFactor = 0.5f)
    {
        return RenderSinglePage(pdfBytes, 1, imageScaleFactor);
    }

    public byte[]? RenderSinglePage(byte[] pdfBytes, int pageNumber, float imageScaleFactor = 0.5f)
    {
        using var msPdf = new MemoryStream(pdfBytes);
        using var processor = new PdfDocumentProcessor();
        processor.LoadDocument(msPdf);

        if (pageNumber < 1 || pageNumber > processor.Document.Pages.Count) return null;

        int dpi = (int)Math.Max(72, Math.Round(96.0 * imageScaleFactor));
        var renderParams = PdfPageRenderingParameters.CreateWithResolution(dpi);

        using var bitmap = processor.CreateDXBitmap(pageNumber, renderParams);
        using var ms = new MemoryStream();
        bitmap.Save(ms, DevExpress.Drawing.DXImageFormat.Png);
        return ms.ToArray();
    }

    public int GetPageCount(byte[] pdfBytes)
    {
        using var msPdf = new MemoryStream(pdfBytes);
        using var processor = new PdfDocumentProcessor();
        processor.LoadDocument(msPdf);
        return processor.Document.Pages.Count;
    }
}
