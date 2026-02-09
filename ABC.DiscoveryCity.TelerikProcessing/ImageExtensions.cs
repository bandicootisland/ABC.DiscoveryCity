using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ABC.DiscoveryCity.TelerikProcessing;

public static class ImageExtensions
{
    /// <summary>
    /// Creates a thumbnail from a JPEG image using ImageSharp.
    /// Returns (thumbPath, width, height) or ("", 0, 0) on failure.
    /// </summary>
    public static (string ThumbPath, int Width, int Height) CreateThumbnail(
        this string sourcePath,
        int maxWidth = 400,
        int quality = 75)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            return ("", 0, 0);

        try
        {
            string thumbPath = Path.Combine(
                Path.GetDirectoryName(sourcePath) ?? "",
                Path.GetFileNameWithoutExtension(sourcePath) + "_thumb.jpg");

            using var image = Image.Load(sourcePath);

            // Calculate proportional height
            int newHeight = (int)(image.Height * ((float)maxWidth / image.Width));

            image.Mutate(x => x.Resize(maxWidth, newHeight));
            image.SaveAsJpeg(thumbPath, new JpegEncoder { Quality = quality });

            return (thumbPath, maxWidth, newHeight);
        }
        catch
        {
            return ("", 0, 0);
        }
    }

    /// <summary>
    /// Gets JPEG dimensions by parsing the file header directly.
    /// Fast - only reads first few hundred bytes.
    /// Returns (0, 0) if not a valid JPEG or dimensions cannot be found.
    /// </summary>
    public static (int Width, int Height) GetJpegDimensions(this string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return (0, 0);

        try
        {
            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs);

            // Verify SOI marker (0xFFD8)
            if (br.ReadByte() != 0xFF || br.ReadByte() != 0xD8)
                return (0, 0);

            while (fs.Position < fs.Length)
            {
                // Find marker
                byte b = br.ReadByte();
                if (b != 0xFF) continue;

                byte type = br.ReadByte();

                // SOF0 (baseline) or SOF2 (progressive) contain dimensions
                if (type == 0xC0 || type == 0xC2)
                {
                    br.ReadUInt16(); // segment length
                    br.ReadByte();   // precision
                    int h = (br.ReadByte() << 8) | br.ReadByte();
                    int w = (br.ReadByte() << 8) | br.ReadByte();
                    return (w, h);
                }

                // Skip markers without length (RST, SOI, EOI)
                if (type >= 0xD0 && type <= 0xD9) continue;
                if (type == 0x01) continue; // TEM

                // Read segment length and skip
                int len = (br.ReadByte() << 8) | br.ReadByte();
                if (len < 2) break;
                fs.Seek(len - 2, SeekOrigin.Current);
            }
        }
        catch
        {
            // Malformed file
        }

        return (0, 0);
    }
}
