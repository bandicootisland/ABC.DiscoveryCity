using ABC.DiscoveryCity.TelerikProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

/// <summary>
/// Prepares video bytes for DB storage. Small MP4s are stored as-is.
/// Non-MP4 formats and large files are transcoded to H.264/AAC MP4 (720p for large files).
/// </summary>
public class VideoProxyStep : IIngestionStep
{
    public string Name => "VideoProxy";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.Category != FileCategory.Video) return Task.CompletedTask;
        if (!MediaFileProcessor.FfmpegAvailable()) return Task.CompletedTask;

        var fileSize = new FileInfo(ctx.FilePath).Length;
        bool isMp4 = ctx.FileExtension == ".mp4";

        if (isMp4 && fileSize <= ctx.VideoProxyThreshold)
        {
            // Small MP4: store original bytes directly
            ctx.VideoBytesForPackage = File.ReadAllBytes(ctx.FilePath);
            ctx.IsVideoProxy = false;
            Console.WriteLine($"  [VIDEO] {fileSize / (1024.0 * 1024):F1} MB — storing original MP4");
        }
        else if (fileSize <= ctx.VideoProxyThreshold)
        {
            // Small non-MP4 (AVI, VOB, etc.): transcode to MP4 at original resolution for browser playback
            var proxyPath = Transcode(ctx, scaleDown: false);
            if (proxyPath != null)
            {
                ctx.VideoBytesForPackage = File.ReadAllBytes(proxyPath);
                ctx.IsVideoProxy = false; // Same resolution, just format conversion
                Console.WriteLine($"  [VIDEO] Converted {ctx.FileExtension} → MP4 ({ctx.VideoBytesForPackage.Length / (1024.0 * 1024):F1} MB)");
            }
        }
        else
        {
            // Large file: transcode to 720p proxy
            var proxyPath = Transcode(ctx, scaleDown: true);
            if (proxyPath != null)
            {
                ctx.VideoBytesForPackage = File.ReadAllBytes(proxyPath);
                ctx.IsVideoProxy = true;
                Console.WriteLine($"  [VIDEO] Proxy: {fileSize / (1024.0 * 1024):F1} MB → {ctx.VideoBytesForPackage.Length / (1024.0 * 1024):F1} MB (720p)");
            }
            else
            {
                Console.WriteLine($"  [VIDEO] Transcode failed — video only available from disk");
            }
        }

        // Set quality flag on metadata (MetadataExtractionStep already ran)
        if (ctx.Metadata != null && ctx.VideoBytesForPackage != null)
            ctx.Metadata.VideoQuality = ctx.IsVideoProxy ? "proxy_720p" : "original";

        return Task.CompletedTask;
    }

    private static string? Transcode(IngestionContext ctx, bool scaleDown)
    {
        string baseName = Path.GetFileNameWithoutExtension(ctx.FilePath);
        string proxyPath = Path.Combine(ctx.OutputDir, $"{baseName}_proxy.mp4");

        string scaleFilter = scaleDown ? "-vf scale=-2:720 " : "";
        string args = $"-i \"{ctx.FilePath}\" {scaleFilter}-c:v libx264 -crf 28 -c:a aac -b:a 128k -movflags +faststart -y \"{proxyPath}\"";

        try
        {
            MediaFileProcessor.RunProcess("ffmpeg", args, timeoutMs: 600_000);

            if (File.Exists(proxyPath) && new FileInfo(proxyPath).Length > 0)
                return proxyPath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [VIDEO] Transcode error: {ex.Message}");
        }

        return null;
    }
}
