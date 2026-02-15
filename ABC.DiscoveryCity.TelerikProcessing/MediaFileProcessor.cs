using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Extracts metadata and thumbnails from video (avi, mp4, vob, mov) and audio (m4a, mp3) files
/// using ffprobe/ffmpeg. Falls back to basic file info if ffmpeg is not installed.
/// </summary>
public static class MediaFileProcessor
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".avi", ".mp4", ".vob", ".mov", ".mkv", ".wmv" };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".m4a", ".mp3", ".wav", ".aac", ".ogg", ".flac" };

    public static bool IsMediaFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext);
    }

    public static bool IsVideoFile(string filePath)
        => VideoExtensions.Contains(Path.GetExtension(filePath));

    public static bool IsAudioFile(string filePath)
        => AudioExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Extract metadata from a media file. Uses ffprobe if available, else basic file info.
    /// </summary>
    public static MediaMetadata GetMetadata(string filePath)
    {
        var meta = new MediaMetadata
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            FileSize = new FileInfo(filePath).Length,
            MediaType = IsVideoFile(filePath) ? "video" : "audio",
            Extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant()
        };

        try
        {
            var probeResult = RunFfprobe(filePath);
            if (probeResult != null)
            {
                ParseFfprobeResult(probeResult, meta);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [MEDIA] ffprobe failed for {meta.FileName}: {ex.Message}");
        }

        return meta;
    }

    /// <summary>
    /// Capture a thumbnail frame from a video file. Returns the output path, or null on failure.
    /// For audio files, returns null (no visual frame to capture).
    /// </summary>
    public static string? CaptureThumbnail(string filePath, string outputDir, string baseName)
    {
        if (!IsVideoFile(filePath)) return null;
        if (!FfmpegAvailable()) return null;

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"{baseName}_thumb.jpg");

        try
        {
            // Capture frame at 1 second (or first frame if shorter)
            var args = $"-ss 1 -i \"{filePath}\" -frames:v 1 -q:v 2 -y \"{outputPath}\"";
            var result = RunProcess("ffmpeg", args, timeoutMs: 15000);

            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
            {
                return outputPath;
            }

            // Retry at 0 seconds for very short videos
            args = $"-ss 0 -i \"{filePath}\" -frames:v 1 -q:v 2 -y \"{outputPath}\"";
            RunProcess("ffmpeg", args, timeoutMs: 15000);

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0
                ? outputPath
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [MEDIA] Thumbnail capture failed for {Path.GetFileName(filePath)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Convert media metadata to sentence strings for the ingestion pipeline.
    /// </summary>
    public static List<string> ToSentences(MediaMetadata meta)
    {
        var sentences = new List<string>();
        var sb = new StringBuilder();

        sb.Append($"[{meta.MediaType.ToUpperInvariant()}] {meta.FileName}");

        if (meta.Duration.HasValue)
            sb.Append($" | Duration: {FormatDuration(meta.Duration.Value)}");

        if (meta.Width > 0 && meta.Height > 0)
            sb.Append($" | Resolution: {meta.Width}x{meta.Height}");

        if (!string.IsNullOrEmpty(meta.VideoCodec))
            sb.Append($" | Video: {meta.VideoCodec}");

        if (!string.IsNullOrEmpty(meta.AudioCodec))
            sb.Append($" | Audio: {meta.AudioCodec}");

        if (meta.FileSize > 0)
            sb.Append($" | Size: {FormatFileSize(meta.FileSize)}");

        sentences.Add($"\n[1] {sb}");
        return sentences;
    }

    // --- ffprobe / ffmpeg helpers ---

    private static bool? _ffmpegAvailable;

    public static bool FfmpegAvailable()
    {
        if (_ffmpegAvailable.HasValue) return _ffmpegAvailable.Value;
        try
        {
            var result = RunProcess("ffprobe", "-version", timeoutMs: 5000);
            _ffmpegAvailable = result != null && result.Contains("ffprobe");
        }
        catch
        {
            _ffmpegAvailable = false;
        }
        return _ffmpegAvailable.Value;
    }

    private static string? RunFfprobe(string filePath)
    {
        if (!FfmpegAvailable()) return null;

        var args = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"";
        return RunProcess("ffprobe", args, timeoutMs: 15000);
    }

    private static void ParseFfprobeResult(string json, MediaMetadata meta)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Format-level metadata
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var dur) &&
                double.TryParse(dur.GetString(), CultureInfo.InvariantCulture, out var seconds))
            {
                meta.Duration = TimeSpan.FromSeconds(seconds);
            }

            if (format.TryGetProperty("bit_rate", out var br) &&
                long.TryParse(br.GetString(), out var bitRate))
            {
                meta.BitRate = bitRate;
            }
        }

        // Stream-level metadata
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var ct) ? ct.GetString() : "";
                var codecName = stream.TryGetProperty("codec_name", out var cn) ? cn.GetString() : "";

                if (codecType == "video" && string.IsNullOrEmpty(meta.VideoCodec))
                {
                    meta.VideoCodec = codecName ?? "";
                    if (stream.TryGetProperty("width", out var w)) meta.Width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h)) meta.Height = h.GetInt32();
                    if (stream.TryGetProperty("r_frame_rate", out var fps))
                    {
                        var fpsStr = fps.GetString() ?? "";
                        var parts = fpsStr.Split('/');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], out var num) &&
                            double.TryParse(parts[1], out var den) && den > 0)
                        {
                            meta.FrameRate = Math.Round(num / den, 2);
                        }
                    }
                }
                else if (codecType == "audio" && string.IsNullOrEmpty(meta.AudioCodec))
                {
                    meta.AudioCodec = codecName ?? "";
                    if (stream.TryGetProperty("sample_rate", out var sr))
                        meta.SampleRate = sr.GetString();
                    if (stream.TryGetProperty("channels", out var ch))
                        meta.AudioChannels = ch.GetInt32();
                }
            }
        }
    }

    private static string? RunProcess(string fileName, string arguments, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return null;

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(timeoutMs);

        return process.ExitCode == 0 ? output : null;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}

public class MediaMetadata
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string MediaType { get; set; } = ""; // "video" or "audio"
    public string Extension { get; set; } = "";
    public long FileSize { get; set; }

    // From ffprobe
    public TimeSpan? Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public double FrameRate { get; set; }
    public long BitRate { get; set; }
    public string? SampleRate { get; set; }
    public int AudioChannels { get; set; }
}
