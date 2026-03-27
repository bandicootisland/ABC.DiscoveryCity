using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Extracts metadata from image files (JPG, PNG, CR2, etc.) using ImageSharp.
/// Mirrors the MediaFileProcessor pattern for the ingestion pipeline.
/// </summary>
public static class ImageFileProcessor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".tif", ".cr2", ".webp" };

    public static bool IsImageFile(string filePath)
        => SupportedExtensions.Contains(Path.GetExtension(filePath));

    public static ImageFileMetadata GetMetadata(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var meta = new ImageFileMetadata
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            FileSize = fileInfo.Length,
            Extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant()
        };

        try
        {
            using var image = Image.Load(filePath);
            meta.Width = image.Width;
            meta.Height = image.Height;

            var exif = image.Metadata.ExifProfile;
            if (exif != null)
            {
                meta.CameraMake = GetExifString(exif, ExifTag.Make);
                meta.CameraModel = GetExifString(exif, ExifTag.Model);
                meta.DateTaken = GetExifString(exif, ExifTag.DateTimeOriginal)
                              ?? GetExifString(exif, ExifTag.DateTime);
                meta.Software = GetExifString(exif, ExifTag.Software);

                // GPS coordinates
                if (exif.TryGetValue(ExifTag.GPSLatitude, out var latVal) &&
                    exif.TryGetValue(ExifTag.GPSLatitudeRef, out var latRef))
                {
                    meta.GpsLatitude = RationalsToDecimal(latVal.Value, latRef.Value);
                }
                if (exif.TryGetValue(ExifTag.GPSLongitude, out var lonVal) &&
                    exif.TryGetValue(ExifTag.GPSLongitudeRef, out var lonRef))
                {
                    meta.GpsLongitude = RationalsToDecimal(lonVal.Value, lonRef.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [IMAGE] Metadata extraction failed for {meta.FileName}: {ex.Message}");
        }

        return meta;
    }

    public static List<string> ToSentences(ImageFileMetadata meta)
    {
        var sentences = new List<string>();
        var sb = new StringBuilder();

        sb.Append($"[IMAGE] {meta.FileName}");

        if (meta.Width > 0 && meta.Height > 0)
            sb.Append($" | Resolution: {meta.Width}x{meta.Height}");

        if (!string.IsNullOrEmpty(meta.CameraModel))
        {
            var camera = !string.IsNullOrEmpty(meta.CameraMake) && !meta.CameraModel.StartsWith(meta.CameraMake)
                ? $"{meta.CameraMake} {meta.CameraModel}"
                : meta.CameraModel;
            sb.Append($" | Camera: {camera}");
        }

        if (!string.IsNullOrEmpty(meta.DateTaken))
            sb.Append($" | Date: {meta.DateTaken}");

        if (meta.GpsLatitude.HasValue && meta.GpsLongitude.HasValue)
            sb.Append($" | GPS: {meta.GpsLatitude:F6}, {meta.GpsLongitude:F6}");

        if (meta.FileSize > 0)
            sb.Append($" | Size: {FormatFileSize(meta.FileSize)}");

        sentences.Add($"\n[1] {sb}");
        return sentences;
    }

    private static string? GetExifString(ExifProfile exif, ExifTag<string> tag)
    {
        if (exif.TryGetValue(tag, out var val))
        {
            var s = val.Value?.Trim().TrimEnd('\0');
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static double? RationalsToDecimal(Rational[]? rationals, string? reference)
    {
        if (rationals == null || rationals.Length < 3) return null;
        double degrees = rationals[0].Numerator / (double)rationals[0].Denominator;
        double minutes = rationals[1].Numerator / (double)rationals[1].Denominator;
        double seconds = rationals[2].Numerator / (double)rationals[2].Denominator;
        double result = degrees + minutes / 60.0 + seconds / 3600.0;
        if (reference == "S" || reference == "W") result = -result;
        return result;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}

public class ImageFileMetadata
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Extension { get; set; } = "";
    public long FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? DateTaken { get; set; }
    public string? Software { get; set; }
    public double? GpsLatitude { get; set; }
    public double? GpsLongitude { get; set; }
}
