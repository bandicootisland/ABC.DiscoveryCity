using ABC.DiscoveryCity.DevExpressProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class PageImagesStep : IIngestionStep
{
    public string Name => "PageImages";

    private const long SmallPdfThreshold = 100 * 1024; // 100KB — PDF itself is the preview
    private const int MaxPreviewPages = 2;            // Only render first 2 pages for large docs

    public Task ExecuteAsync(IngestionContext ctx)
    {
        // Page 1 preview + thumbnail now handled by ThumbnailStep (single render).
        // This step is retained for future multi-page preview needs.
        return Task.CompletedTask;
    }
}
