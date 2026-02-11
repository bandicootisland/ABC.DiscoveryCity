namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Unified pipeline for generating PDF page images.
/// 
/// Strategy:
///   1. Primary — PdfImageExtractor: extract embedded raster images directly from
///      PDF content nodes via Telerik RadPdfProcessing.  Fast (~5/s), no browser,
///      native resolution, pixel-perfect original scan quality.
///   
///   2. Fallback — ThumbnailService (pdf.js/Chrome): render the page in headless
///      Chrome using Mozilla pdf.js.  Slower (~2/s), requires a running API server,
///      viewport resolution (1280×720).  Used when extraction returns nothing
///      (e.g. vector-only PDFs with no embedded images).
/// </summary>
public class PdfThumbnailPipeline : IAsyncDisposable
{
    private readonly PdfImageExtractor _extractor;
    private ThumbnailService? _thumbnailService;
    private bool _fallbackInitialised;
    private readonly bool _headless;
    private readonly int _browserCount;
    private readonly string? _remoteViewerUrl;

    // Counters for pipeline-level stats
    private int _extractOk;
    private int _fallbackOk;
    private int _failed;

    public int ExtractOk => _extractOk;
    public int FallbackOk => _fallbackOk;
    public int Failed => _failed;

    /// <summary>
    /// Create the pipeline.
    /// </summary>
    /// <param name="headless">Run Chrome headless when fallback is needed.</param>
    /// <param name="browserCount">Number of Chrome tabs for fallback mode.</param>
    /// <param name="remoteViewerUrl">
    /// If set (e.g. "http://localhost:5022/pdfviewer.html"), the fallback uses
    /// pdf.js served from the API.  If null, uses Chrome's built-in embed viewer.
    /// </param>
    public PdfThumbnailPipeline(
        bool headless = true,
        int browserCount = 4,
        string? remoteViewerUrl = "http://localhost:5022/pdfviewer.html")
    {
        _extractor = new PdfImageExtractor();
        _headless = headless;
        _browserCount = browserCount;
        _remoteViewerUrl = remoteViewerUrl;
    }

    /// <summary>
    /// Generate a page image + thumbnail for a PDF.  Returns unified result.
    /// Thread-safe — can be called from Parallel.ForEachAsync.
    /// </summary>
    public async Task<PipelineResult> ProcessAsync(string pdfPath, int pageIndex = 0)
    {
        // ---- Primary: direct extraction ----
        try
        {
            var (fullPath, thumbPath, w, h) = _extractor.ExtractPageImage(pdfPath, pageIndex);
            if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
            {
                Interlocked.Increment(ref _extractOk);
                return new PipelineResult
                {
                    Success = true,
                    Method = RenderMethod.Extract,
                    FullPath = fullPath,
                    ThumbPath = thumbPath,
                    Width = w,
                    Height = h
                };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [EXTRACT-ERR] {Path.GetFileName(pdfPath)}: {ex.Message}");
        }

        // ---- Fallback: Chrome / pdf.js ----
        try
        {
            await EnsureFallbackAsync();

            var pageImages = await _thumbnailService!.GeneratePageImagesAsync(pdfPath);

            string thumbPath = ""; int thumbW = 0, thumbH = 0;
            string fullPath = ""; int fullW = 0, fullH = 0;

            foreach (var (filePath, width, height) in pageImages)
            {
                if (filePath.Contains("_thumb."))
                    (thumbPath, thumbW, thumbH) = (filePath, width, height);
                else
                    (fullPath, fullW, fullH) = (filePath, width, height);
            }

            if (!string.IsNullOrEmpty(fullPath) || !string.IsNullOrEmpty(thumbPath))
            {
                Interlocked.Increment(ref _fallbackOk);
                return new PipelineResult
                {
                    Success = true,
                    Method = RenderMethod.BrowserFallback,
                    FullPath = fullPath,
                    ThumbPath = thumbPath,
                    Width = fullW,
                    Height = fullH,
                    ThumbWidth = thumbW,
                    ThumbHeight = thumbH
                };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [FALLBACK-ERR] {Path.GetFileName(pdfPath)}: {ex.Message}");
        }

        Interlocked.Increment(ref _failed);
        return new PipelineResult { Success = false };
    }

    /// <summary>
    /// Lazy-initialise the browser fallback (only when first needed).
    /// </summary>
    private async Task EnsureFallbackAsync()
    {
        if (_fallbackInitialised) return;

        // Simple lock — first caller initialises, others wait
        _thumbnailService ??= new ThumbnailService();
        if (!string.IsNullOrEmpty(_remoteViewerUrl))
            _thumbnailService.RemoteViewerUrl = _remoteViewerUrl;

        await _thumbnailService.InitializeAsync(headless: _headless, instancecount: _browserCount);
        _fallbackInitialised = true;
    }

    public void PrintStats()
    {
        int total = _extractOk + _fallbackOk + _failed;
        Console.WriteLine($"Pipeline stats: {total} total — Extract: {_extractOk}, Fallback: {_fallbackOk}, Failed: {_failed}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_thumbnailService != null)
            await _thumbnailService.DisposeAsync();
    }
}

/// <summary>
/// Which rendering method produced the image.
/// </summary>
public enum RenderMethod
{
    Extract,          // Direct Telerik image extraction
    BrowserFallback   // Chrome / pdf.js screenshot
}

/// <summary>
/// Result from the thumbnail pipeline.
/// </summary>
public class PipelineResult
{
    public bool Success { get; init; }
    public RenderMethod Method { get; init; }
    public string FullPath { get; init; } = "";
    public string ThumbPath { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int ThumbWidth { get; init; }
    public int ThumbHeight { get; init; }
}
