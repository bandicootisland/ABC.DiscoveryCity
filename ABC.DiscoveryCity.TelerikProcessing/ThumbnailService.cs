using Microsoft.Playwright;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.IO;
using System.Net;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Service for generating PDF page thumbnails using Playwright (Headless Chrome).
/// Guaranteed to render what Chrome sees (including OCR text overlays or images).
/// </summary>
public class ThumbnailService : IDisposable
{
    private IPlaywright? _playwright;
    //private IBrowser? _browser;
    private bool _initialized;
    private static Dictionary<int,Tuple<IBrowser,IBrowserContext,IPage>> _browserinstances { get; set; }
    private static int browserroundrobin;
    /// <summary>
    /// Initialize Playwright and launch the browser.
    /// Call this once before processing.
    /// </summary>
    private string _viewerPath = "";

    public async Task InitializeAsync(bool headless = true,int instancecount= 10)
    {
        if (_initialized) return;

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
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--allow-file-access-from-files" }
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
        _initialized = true;
        Console.WriteLine("  Playwright Initialized.");
    }
    
    /// <summary>
    /// Generate screenshots of the first 3 pages of the PDF.
    /// </summary>
    public async Task<List<(string FilePath, int Width, int Height)>> GeneratePageImagesAsync(string pdfPath)
    {
        var results = new List<(string, int, int)>();

        if (!_initialized || _browserinstances.Count==0)
        {
            await InitializeAsync(true);
        }

        string dir = Path.GetDirectoryName(pdfPath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(pdfPath);
        
        // Ensure PDF path is a valid URI
        string pdfFileUrl = new Uri(pdfPath).AbsoluteUri;
        string viewerUrl = new Uri(_viewerPath).AbsoluteUri;
        var browser = _browserinstances[browserroundrobin++ % _browserinstances.Count];
        IPage? page = browser.Item3;
        try
        {
        
            
            var pageNum = 1;//page 1 show thumnails in viewer itself, so only 1 page required
            
                string outputPath = Path.Combine(dir, $"{baseName}_page{pageNum}.jpg");
                
                // Construct URL with query params for viewer
                string pageNavUrl = $"{viewerUrl}?file={System.Web.HttpUtility.UrlEncode(pdfFileUrl)}&page={pageNum}";
                
                pageNavUrl = $"{viewerUrl}?file={System.Net.WebUtility.UrlEncode(pdfFileUrl)}&page={pageNum}";

                Console.Error.WriteLine($"  Navigate to: {pageNavUrl}");
                
                try 
                {
                    await page.GotoAsync(pageNavUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load });
                    await page.WaitForTimeoutAsync(3000); //seems to be needed
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
            //results.Add((outputPath, original.Width, original.Height)); but dont kjnow height width
            if (File.Exists(outputPath))
                {
                    // Resize to thumbnail using ImageSharp
                    try
                    {
                        using var original = Image.Load(outputPath);
                        results.Add((outputPath, original.Width, original.Height));

                        int targetWidth = 100; // Small thumbnail for shape preview
                        int targetHeight = (int)((float)original.Height / original.Width * targetWidth);

                        var thumbPath = outputPath.Replace(".jpg", "_thumb.jpg");

                        // Clone and resize for thumbnail
                        using var thumbnail = original.Clone(ctx => ctx.Resize(targetWidth, targetHeight));
                        thumbnail.Save(thumbPath, new JpegEncoder { Quality = 85 });

                        Console.WriteLine($"  Page{pageNum}: Resized to {targetWidth}x{targetHeight} ({outputPath})");
                        results.Add((thumbPath, targetWidth, targetHeight));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [WARN] Resize failed page {pageNum}: {ex.Message}");
                        results.Add((outputPath, 0, 0));
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
            
        }

        return results;
    }

    public void Dispose()
    {
        if (_browserinstances != null)
        {
            foreach (var _browserinstance in _browserinstances)
            {
                var b = _browserinstance.Value;
                var browser = b.Item1;
                var context = b.Item2;
                var page = b.Item3;
                page.CloseAsync().Wait();
                context.CloseAsync().Wait();
                browser?.DisposeAsync().AsTask().Wait();
            }
            _browserinstances.Clear();
        }
        
        _playwright?.Dispose();
    }
    
    // Legacy support for static path not really needed but useful helper
    public static string GetThumbnailPath(string pdfPath)
    {
        var dir = Path.GetDirectoryName(pdfPath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(pdfPath);
        return Path.Combine(dir, $"{nameWithoutExt}.page1.jpg");
    }
}
