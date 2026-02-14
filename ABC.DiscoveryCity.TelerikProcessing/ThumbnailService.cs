using Microsoft.Playwright;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.IO;
using System.Net;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Service for generating PDF page thumbnails using Playwright (Headless Chrome).
/// Guaranteed to render what Chrome sees (including OCR text overlays or images).
/// </summary>
public class ThumbnailService : IDisposable, IAsyncDisposable
{
    private IPlaywright? _playwright;
    private bool _initialized;
    private static Dictionary<int,Tuple<IBrowser,IBrowserContext,IPage>> _browserinstances { get; set; }
    private static SemaphoreSlim[] _pageLocks = Array.Empty<SemaphoreSlim>();
    private static int browserroundrobin;
    private static int _filesProcessed;
    private static int _instanceCount = 10;
    private static bool _headless = true;
    private static readonly SemaphoreSlim _resetLock = new(1, 1);
    private const int RESET_EVERY_N_FILES = 100;
    
    /// <summary>
    /// Optional: Set a remote viewer URL (e.g. "http://localhost:5022/pdfviewer.html")
    /// When set, uses this URL instead of the local pdf_viewer.html, and passes the PDF
    /// path via the API's /api/images/view endpoint.
    /// </summary>
    public string? RemoteViewerUrl { get; set; }
    
    /// <summary>
    /// Initialize Playwright and launch the browser.
    /// Call this once before processing.
    /// </summary>
    private string _viewerPath = "";

    public async Task InitializeAsync(bool headless = true,int instancecount= 10)
    {
        if (_initialized) return;
        _headless = headless;
        _instanceCount = instancecount;
        _filesProcessed = 0;

        //        // Create a simple PDF viewer HTML to avoid download prompt
        _viewerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pdf_viewer.html");
        if (!File.Exists(_viewerPath))
        {
            string html = @"<!DOCTYPE html>
<html>
<head>
    <style>body,html,embed { width:100%; height:100%; margin:0; padding:0; overflow:hidden; display:block; }</style>
</head>
<body>
    <embed id='pdf-embed' type='application/pdf'>
    <script>
        const params = new URLSearchParams(window.location.search);
        const file = params.get('file');
        const page = params.get('page');
        if (file) {
            document.getElementById('pdf-embed').src = file + (page ? '#page=' + page : '');
        }
    </script>
</body>
</html>";
            await File.WriteAllTextAsync(_viewerPath, html);
        }
        Console.WriteLine($"  Initializing Playwright (Headless: {headless})...");

        _playwright = await Playwright.CreateAsync();
        _browserinstances=new Dictionary<int, Tuple<IBrowser, IBrowserContext, IPage>>();


        Console.WriteLine($"  Launching Chrome...");
        var browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--allow-file-access-from-files" },
                
            });

        for (int i = 0; i < instancecount; i++)
        {
            Console.WriteLine($"      Launching Chrome Browser...{i}");
            var browsercontext = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    JavaScriptEnabled = true,

                });
                IPage page= await browsercontext.NewPageAsync();
                
                _browserinstances.Add(i, new( browser,browsercontext,page));
        }

        // Create per-page semaphores — ensures each browser tab is used by only one PDF at a time
        _pageLocks = new SemaphoreSlim[instancecount];
        for (int i = 0; i < instancecount; i++)
            _pageLocks[i] = new SemaphoreSlim(1, 1);

        _initialized = true;
        Console.WriteLine("  Playwright Initialized.");
    }
    
    /// <summary>
    /// Generate screenshots of the first 3 pages of the PDF.
    /// Returns (FilePath, Width, Height, ImageData) for each image.
    /// </summary>
    public async Task<List<(string FilePath, int Width, int Height, byte[] ImageData)>> GeneratePageImagesAsync(string pdfPath)
    {
        var results = new List<(string, int, int, byte[])>();

        if (!_initialized || _browserinstances.Count==0)
        {
            await InitializeAsync(true);
        }

        string dir = Path.GetDirectoryName(pdfPath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(pdfPath);
        
        // Construct the navigation URL based on whether we're using a remote viewer (pdf.js via API)
        // or the local embedded pdf_viewer.html
        string pageNavUrl;
        if (!string.IsNullOrEmpty(RemoteViewerUrl))
        {
            // Remote viewer: pass PDF path via API endpoint
            string encodedPath = System.Net.WebUtility.UrlEncode(pdfPath);
            pageNavUrl = $"{RemoteViewerUrl}?file=/api/images/view?path={encodedPath}&page=1";
        }
        else
        {
            // Local viewer: use file:// URIs 
            string pdfFileUrl = new Uri(pdfPath).AbsoluteUri;
            string viewerUrl = new Uri(_viewerPath).AbsoluteUri;
            pageNavUrl = $"{viewerUrl}?file={pdfFileUrl}&page=1";
        }

        // Round-robin pick a browser slot and acquire its lock
        int slot = Interlocked.Increment(ref browserroundrobin) % _browserinstances.Count;
        await _pageLocks[slot].WaitAsync();
        try
        {
        var browser = _browserinstances[slot];
        IPage? page = browser.Item3;
        
            
            var pageNum = 1;//page 1 show thumnails in viewer itself, so only 1 page required
            
                string outputPath = Path.Combine(dir, $"{baseName}_page{pageNum}.jpg");
                
                // URL already constructed above (local file:// or remote http://)

                Console.Error.WriteLine($"  Navigate to: {pageNavUrl}");
                
                try 
                {
                    await page.GotoAsync(pageNavUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                    
                    if (!string.IsNullOrEmpty(RemoteViewerUrl))
                    {
                        // pdf.js viewer: wait for title to signal render complete (max 15s)
                        try
                        {
                            await page.WaitForFunctionAsync("() => document.title === 'PDF_RENDERED' || document.title === 'PDF_ERROR'",
                                new PageWaitForFunctionOptions { Timeout = 15000 });
                        }
                        catch (TimeoutException)
                        {
                            Console.Error.WriteLine($"  [WARN] pdf.js render timeout, taking screenshot anyway");
                        }
                    }
                    else
                    {
                        await page.WaitForTimeoutAsync(5000); // local embed: fixed wait
                    }
                }
                catch (Exception navEx)
                {
                    Console.Error.WriteLine($"  [ERROR] Navigation failed: {navEx.Message}");
                    return results;
                }

                Console.Error.WriteLine($"  Taking screenshot for Page {pageNum}...");
                
                await page.ScreenshotAsync(new PageScreenshotOptions 
                { 
                    Path = outputPath, 
                    Type = ScreenshotType.Jpeg,
                    Quality = 85,
                    FullPage = false
                });

                // Check if screenshot is just the viewer background (PDF didn't load)
                if (File.Exists(outputPath) && IsBlankViewerScreenshot(outputPath))
                {
                    Console.Error.WriteLine($"  [RETRY] Blank viewer background detected, waiting 10s and retrying...");
                    await page.WaitForTimeoutAsync(10000);
                    await page.ScreenshotAsync(new PageScreenshotOptions 
                    { 
                        Path = outputPath, 
                        Type = ScreenshotType.Jpeg,
                        Quality = 85,
                        FullPage = false
                    });

                    if (IsBlankViewerScreenshot(outputPath))
                    {
                        Console.Error.WriteLine($"  [SKIP] Still blank after retry - PDF failed to render: {Path.GetFileName(pdfPath)}");
                        File.Delete(outputPath);
                        return results;
                    }
                }
            //results.Add((outputPath, original.Width, original.Height)); but dont kjnow height width
            if (File.Exists(outputPath))
                {
                    // Resize preview and create thumbnail using ImageSharp
                    try
                    {
                        using var original = Image.Load(outputPath);

                        // Resize preview to max 400px wide for compact storage
                        int previewMaxWidth = 400;
                        if (original.Width > previewMaxWidth)
                        {
                            int previewHeight = (int)Math.Round(original.Height * (previewMaxWidth / (double)original.Width));
                            original.Mutate(ctx => ctx.Resize(previewMaxWidth, previewHeight));
                        }
                        original.SaveAsJpeg(outputPath, new JpegEncoder { Quality = 70 });

                        // Capture preview bytes
                        byte[] previewBytes;
                        using (var ms = new MemoryStream())
                        {
                            original.SaveAsJpeg(ms, new JpegEncoder { Quality = 70 });
                            previewBytes = ms.ToArray();
                        }
                        results.Add((outputPath, original.Width, original.Height, previewBytes));

                        int targetWidth = 100; // Small thumbnail for shape preview
                        int targetHeight = (int)((float)original.Height / original.Width * targetWidth);

                        var thumbPath = outputPath.Replace(".jpg", "_thumb.jpg");

                        // Clone and resize for thumbnail
                        using var thumbnail = original.Clone(ctx => ctx.Resize(targetWidth, targetHeight));
                        thumbnail.Save(thumbPath, new JpegEncoder { Quality = 75 });

                        // Capture thumb bytes
                        byte[] thumbBytes;
                        using (var ms = new MemoryStream())
                        {
                            thumbnail.SaveAsJpeg(ms, new JpegEncoder { Quality = 75 });
                            thumbBytes = ms.ToArray();
                        }

                        Console.WriteLine($"  Page{pageNum}: Preview {original.Width}x{original.Height}, Thumb {targetWidth}x{targetHeight} ({outputPath})");
                        results.Add((thumbPath, targetWidth, targetHeight, thumbBytes));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [WARN] Resize failed page {pageNum}: {ex.Message}");
                        results.Add((outputPath, 0, 0, Array.Empty<byte>()));
                    }
                }
                else
                {
                    Console.Error.WriteLine($"  [WARN] Page{pageNum}: File NOT created.");
                }
            
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Playwright error for {Path.GetFileName(pdfPath)}: {ex.Message}");
        }
        finally
        {
            // Release this browser slot so next PDF can use it
            _pageLocks[slot].Release();

            // Periodic browser reset to prevent memory leaks
            int count = Interlocked.Increment(ref _filesProcessed);
            if (count % RESET_EVERY_N_FILES == 0)
            {
                Console.WriteLine($"  [RESET] {count} files processed — recycling browsers to free memory...");
                await ResetBrowsersAsync();
            }
        }

        return results;
    }

    /// <summary>
    /// Gracefully close all browser contexts/pages and relaunch fresh ones.
    /// Prevents Chrome memory leaks from accumulating over hundreds of files.
    /// </summary>
    public async Task ResetBrowsersAsync()
    {
        await _resetLock.WaitAsync();
        try
        {
            if (_browserinstances == null || _browserinstances.Count == 0) return;

            // Acquire all page locks so no renders are in-flight during reset
            for (int i = 0; i < _pageLocks.Length; i++)
                await _pageLocks[i].WaitAsync();

            // Close pages and contexts gracefully (keep the browser process)
            IBrowser? sharedBrowser = null;
            foreach (var instance in _browserinstances.Values)
            {
                sharedBrowser = instance.Item1;
                try { await instance.Item3.CloseAsync(); } catch { }
                try { await instance.Item2.CloseAsync(); } catch { }
            }
            _browserinstances.Clear();

            // Relaunch fresh contexts on the same browser
            if (sharedBrowser != null)
            {
                for (int i = 0; i < _instanceCount; i++)
                {
                    var ctx = await sharedBrowser.NewContextAsync(new BrowserNewContextOptions { JavaScriptEnabled = true });
                    var page = await ctx.NewPageAsync();
                    _browserinstances.Add(i, new(sharedBrowser, ctx, page));
                }
            }

            // Release all page locks
            for (int i = 0; i < _pageLocks.Length; i++)
                _pageLocks[i].Release();

            Console.WriteLine($"  [RESET] Done — {_instanceCount} fresh browser contexts ready.");
        }
        finally
        {
            _resetLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browserinstances != null && _browserinstances.Count > 0)
        {
            IBrowser? sharedBrowser = null;
            foreach (var instance in _browserinstances.Values)
            {
                sharedBrowser = instance.Item1;
                try { await instance.Item3.CloseAsync(); } catch { }
                try { await instance.Item2.CloseAsync(); } catch { }
            }
            _browserinstances.Clear();

            // Close the browser process itself
            if (sharedBrowser != null)
            {
                try { await sharedBrowser.CloseAsync(); } catch { }
                try { await sharedBrowser.DisposeAsync(); } catch { }
            }
        }

        _playwright?.Dispose();
        _initialized = false;
        Console.WriteLine("  Playwright disposed gracefully.");
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    
    /// <summary>
    /// Detects if a screenshot is just the Chrome PDF viewer background (PDF didn't load).
    /// The viewer background is solid dark gray RGB(40,40,40). This does NOT flag:
    /// - Redacted/black pages (RGB near 0,0,0) — those are real content
    /// - Sparse/mostly-white pages — those are real content
    /// - Any page with meaningful color variance — real content
    /// Only catches the very specific viewer-background-gray pattern.
    /// </summary>
    private static bool IsBlankViewerScreenshot(string imagePath)
    {
        try
        {
            using var img = Image.Load<Rgb24>(imagePath);
            int w = img.Width;
            int h = img.Height;
            
            // Sample the center 50% of the image (avoid toolbar/edges)
            int startX = w / 4;
            int endX = w * 3 / 4;
            int startY = h / 4;
            int endY = h * 3 / 4;
            
            int sampleCount = 0;
            int viewerGrayCount = 0;
            
            // Sample every 10th pixel for speed
            for (int y = startY; y < endY; y += 10)
            {
                for (int x = startX; x < endX; x += 10)
                {
                    var pixel = img[x, y];
                    sampleCount++;
                    
                    // Viewer background is RGB(40,40,40) — check for range 30-55
                    // This is clearly distinct from:
                    //   - Redacted black (0-15)
                    //   - White/content pages (200+)
                    //   - Any real rendered content (high variance)
                    if (pixel.R >= 30 && pixel.R <= 55 && 
                        pixel.G >= 30 && pixel.G <= 55 && 
                        pixel.B >= 30 && pixel.B <= 55 &&
                        Math.Abs(pixel.R - pixel.G) <= 5 &&
                        Math.Abs(pixel.G - pixel.B) <= 5)
                    {
                        viewerGrayCount++;
                    }
                }
            }
            
            double viewerGrayPct = (double)viewerGrayCount / sampleCount;
            
            // If >90% of center pixels are viewer-background gray, it's blank
            if (viewerGrayPct > 0.90)
            {
                Console.Error.WriteLine($"  [BLANK] {Path.GetFileName(imagePath)}: {viewerGrayPct:P0} viewer-gray pixels — PDF did not render");
                return true;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [WARN] Blank detection failed: {ex.Message}");
            return false; // If we can't check, assume it's real
        }
    }

    // Legacy support for static path not really needed but useful helper
    public static string GetThumbnailPath(string pdfPath)
    {
        var dir = Path.GetDirectoryName(pdfPath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(pdfPath);
        return Path.Combine(dir, $"{nameWithoutExt}.page1.jpg");
    }
}
