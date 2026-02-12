using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Objects;
using Telerik.Windows.Documents.Fixed.Model.Resources;
using Telerik.Windows.Documents.Fixed.Model.Text;
using Telerik.Windows.Documents.Fixed.FormatProviders.Pdf;
using Telerik.Windows.Documents.Extensibility;
using Telerik.Windows.Documents.Core.Fonts;
using System.IO.Compression;
using System.Reflection;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Extracts embedded raster images directly from PDF content nodes.
/// For scanned/image-based PDFs, the page content is typically one large Image element.
/// This bypasses any browser-based renderer entirely.
/// </summary>
public class PdfImageExtractor
{
    public PdfImageExtractor()
    {
        // Ensure fonts provider is set (needed for PDF import even if we only want images)
        if (FixedExtensibilityManager.FontsProvider == null)
        {
            FixedExtensibilityManager.FontsProvider = new WindowsFontsProvider();
        }
    }

    /// <summary>
    /// Attempts to extract embedded images from a PDF page.
    /// Returns info about all images found on the first page.
    /// </summary>
    public List<ExtractedImageInfo> InspectPage(string pdfPath, int pageIndex = 0)
    {
        var results = new List<ExtractedImageInfo>();

        var provider = new PdfFormatProvider();
        RadFixedDocument doc;

        using (var stream = File.OpenRead(pdfPath))
        {
            doc = provider.Import(stream);
        }

        if (doc.Pages.Count == 0 || pageIndex >= doc.Pages.Count)
            return results;

        var page = doc.Pages[pageIndex];

        Console.WriteLine($"  Page {pageIndex + 1}: Size={page.Size.Width:F0}x{page.Size.Height:F0}, Content elements: {page.Content.Count}");

        int imageIndex = 0;
        int textCount = 0;
        int otherCount = 0;

        foreach (var element in page.Content)
        {
            if (element is Telerik.Windows.Documents.Fixed.Model.Objects.Image img)
            {
                imageIndex++;
                var info = new ExtractedImageInfo
                {
                    Index = imageIndex,
                    Width = (int)(img.Width),
                    Height = (int)(img.Height),
                    ElementType = "Image"
                };

                // Try to get ImageSource details
                if (img.ImageSource != null)
                {
                    info.SourceWidth = (int)img.ImageSource.Width;
                    info.SourceHeight = (int)img.ImageSource.Height;

                    // Try GetEncodedImageData (may not be available in .NET Standard builds)
                    try
                    {
                        var encodedData = img.ImageSource.GetEncodedImageData();
                        if (encodedData != null)
                        {
                            info.HasEncodedData = true;
                            info.EncodedDataLength = encodedData.Data?.Length ?? 0;

                            // Get filter info via reflection (Filters property)
                            try
                            {
                                info.Filters = string.Join(",", encodedData.Filters ?? Array.Empty<string>());
                                info.BitsPerComponent = encodedData.BitsPerComponent;
                                info.ColorSpace = encodedData.ColorSpace?.ToString() ?? "unknown";
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        info.EncodedDataError = ex.GetType().Name + ": " + ex.Message;
                    }
                }

                results.Add(info);
                Console.WriteLine($"    Image[{imageIndex}]: Element={info.Width}x{info.Height}, Source={info.SourceWidth}x{info.SourceHeight}, " +
                    $"Encoded={info.HasEncodedData} ({info.EncodedDataLength:N0} bytes), Filters={info.Filters}, " +
                    $"BPC={info.BitsPerComponent}, CS={info.ColorSpace}");
                if (!string.IsNullOrEmpty(info.EncodedDataError))
                    Console.WriteLine($"      Error: {info.EncodedDataError}");
            }
            else if (element is TextFragment)
            {
                textCount++;
            }
            else
            {
                otherCount++;
            }
        }

        Console.WriteLine($"  Summary: {imageIndex} image(s), {textCount} text fragment(s), {otherCount} other element(s)");
        return results;
    }

    /// <summary>
    /// Extracts the largest image from the first page and saves it as JPEG.
    /// Handles FlateDecode (zlib), DCTDecode (JPEG) with DeviceGray, DeviceRGB, Indexed colorspaces.
    /// Returns (fullImagePath, thumbPath, width, height) or empty strings on failure.
    /// </summary>
    /// <param name="pdfPath">Path to the source PDF.</param>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="outputDir">
    /// Optional output directory for generated images.
    /// If null, images are saved alongside the PDF.
    /// </param>
    public (string FullPath, string ThumbPath, int Width, int Height) ExtractPageImage(string pdfPath, int pageIndex = 0, string? outputDir = null)
    {
        var provider = new PdfFormatProvider();
        RadFixedDocument doc;

        using (var stream = File.OpenRead(pdfPath))
        {
            doc = provider.Import(stream);
        }

        if (doc.Pages.Count == 0 || pageIndex >= doc.Pages.Count)
            return (string.Empty, string.Empty, 0, 0);

        var page = doc.Pages[pageIndex];

        // Find the largest image element on the page
        Telerik.Windows.Documents.Fixed.Model.Objects.Image? largestImage = null;
        double largestArea = 0;

        foreach (var element in page.Content)
        {
            if (element is Telerik.Windows.Documents.Fixed.Model.Objects.Image img)
            {
                double area = img.Width * img.Height;
                if (area > largestArea)
                {
                    largestArea = area;
                    largestImage = img;
                }
            }
        }

        if (largestImage?.ImageSource == null)
            return (string.Empty, string.Empty, 0, 0);

        // Get encoded image data
        EncodedImageData? encodedData = null;
        try
        {
            encodedData = largestImage.ImageSource.GetEncodedImageData();
        }
        catch
        {
            return (string.Empty, string.Empty, 0, 0);
        }

        if (encodedData?.Data == null || encodedData.Data.Length == 0)
            return (string.Empty, string.Empty, 0, 0);

        int imgWidth = (int)encodedData.Width;
        int imgHeight = (int)encodedData.Height;
        string filter = encodedData.Filters?.FirstOrDefault() ?? "";
        string colorSpace = encodedData.ColorSpace?.ToString() ?? "";
        int bpc = encodedData.BitsPerComponent;

        // Output paths
        string baseName = Path.GetFileNameWithoutExtension(pdfPath);
        string dir = outputDir ?? Path.GetDirectoryName(pdfPath) ?? ".";
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        string fullPath = Path.Combine(dir, $"{baseName}_page{pageIndex + 1}.jpg");
        string thumbPath = Path.Combine(dir, $"{baseName}_page{pageIndex + 1}_thumb.jpg");

        try
        {
            SixLabors.ImageSharp.Image? resultImage = null;

            if (filter == "DCTDecode" || filter == "DCT")
            {
                // Already JPEG — load directly
                using var ms = new MemoryStream(encodedData.Data);
                resultImage = SixLabors.ImageSharp.Image.Load(ms);
            }
            else if (filter == "FlateDecode" || filter == "Flate" || string.IsNullOrEmpty(filter))
            {
                // Decompress zlib/deflate data
                byte[] rawPixels = DecompressFlate(encodedData.Data);

                if (colorSpace.Contains("DeviceGray") || colorSpace.Contains("Gray"))
                {
                    // Grayscale: 1 byte per pixel (when bpc=8)
                    resultImage = BuildGrayscaleImage(rawPixels, imgWidth, imgHeight, bpc);
                }
                else if (colorSpace.Contains("DeviceRGB") || colorSpace.Contains("RGB"))
                {
                    // RGB: 3 bytes per pixel
                    resultImage = BuildRgbImage(rawPixels, imgWidth, imgHeight);
                }
                else if (colorSpace.Contains("Indexed"))
                {
                    // Indexed: 1 byte per pixel indexing into a palette
                    // The palette is embedded in the colorspace definition
                    // For scanned docs, usually grayscale palette
                    resultImage = BuildIndexedImage(rawPixels, imgWidth, imgHeight, encodedData);
                }
                else
                {
                    // Unknown colorspace — try as grayscale
                    Console.Error.WriteLine($"  [WARN] Unknown colorspace '{colorSpace}', trying grayscale");
                    resultImage = BuildGrayscaleImage(rawPixels, imgWidth, imgHeight, bpc);
                }
            }
            else
            {
                Console.Error.WriteLine($"  [WARN] Unsupported filter: {filter}");
                return (string.Empty, string.Empty, 0, 0);
            }

            if (resultImage == null)
                return (string.Empty, string.Empty, 0, 0);

            // Save full-size JPEG
            using (resultImage)
            {
                resultImage.SaveAsJpeg(fullPath, new JpegEncoder { Quality = 85 });

                // Create thumbnail
                int thumbWidth = 100;
                int thumbHeight = (int)Math.Round(resultImage.Height * (thumbWidth / (double)resultImage.Width));

                using var thumb = resultImage.Clone(ctx => ctx.Resize(thumbWidth, thumbHeight));
                thumb.SaveAsJpeg(thumbPath, new JpegEncoder { Quality = 85 });
            }

            return (fullPath, thumbPath, imgWidth, imgHeight);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [ERROR] Image extraction: {ex.Message}");
            return (string.Empty, string.Empty, 0, 0);
        }
    }

    /// <summary>
    /// Decompress zlib/FlateDecode data (PDF uses raw deflate with 2-byte zlib header)
    /// </summary>
    private static byte[] DecompressFlate(byte[] compressedData)
    {
        // Try zlib (deflate with header) first, then raw deflate
        try
        {
            using var input = new MemoryStream(compressedData);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch
        {
            // Fallback: try raw deflate (skip 2-byte zlib header)
            try
            {
                using var input = new MemoryStream(compressedData, 2, compressedData.Length - 2);
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                deflate.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                // Last resort: return raw data
                return compressedData;
            }
        }
    }

    /// <summary>
    /// Build grayscale image from raw pixel data
    /// </summary>
    private static SixLabors.ImageSharp.Image BuildGrayscaleImage(byte[] rawPixels, int width, int height, int bpc)
    {
        var image = new Image<L8>(width, height);

        if (bpc == 8)
        {
            // 1 byte per pixel
            int expectedSize = width * height;
            int stride = rawPixels.Length >= expectedSize ? width : (rawPixels.Length / height);

            for (int y = 0; y < height && y * stride < rawPixels.Length; y++)
            {
                for (int x = 0; x < width && (y * stride + x) < rawPixels.Length; x++)
                {
                    image[x, y] = new L8(rawPixels[y * stride + x]);
                }
            }
        }
        else if (bpc == 1)
        {
            // 1 bit per pixel (black & white)
            int bytesPerRow = (width + 7) / 8;
            for (int y = 0; y < height && y * bytesPerRow < rawPixels.Length; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int byteIdx = y * bytesPerRow + x / 8;
                    if (byteIdx >= rawPixels.Length) break;
                    int bitIdx = 7 - (x % 8);
                    byte val = (byte)(((rawPixels[byteIdx] >> bitIdx) & 1) == 1 ? 0 : 255);
                    image[x, y] = new L8(val);
                }
            }
        }
        else if (bpc == 4)
        {
            // 4 bits per pixel (16 shades)
            int bytesPerRow = (width + 1) / 2;
            for (int y = 0; y < height && y * bytesPerRow < rawPixels.Length; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int byteIdx = y * bytesPerRow + x / 2;
                    if (byteIdx >= rawPixels.Length) break;
                    int nibble = (x % 2 == 0) ? (rawPixels[byteIdx] >> 4) : (rawPixels[byteIdx] & 0x0F);
                    image[x, y] = new L8((byte)(nibble * 17)); // Scale 0-15 to 0-255
                }
            }
        }

        return image;
    }

    /// <summary>
    /// Build RGB image from raw pixel data (3 bytes per pixel)
    /// </summary>
    private static SixLabors.ImageSharp.Image BuildRgbImage(byte[] rawPixels, int width, int height)
    {
        var image = new Image<Rgb24>(width, height);
        int stride = width * 3;

        for (int y = 0; y < height && (y * stride + stride - 1) < rawPixels.Length; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * stride + x * 3;
                if (idx + 2 >= rawPixels.Length) break;
                image[x, y] = new Rgb24(rawPixels[idx], rawPixels[idx + 1], rawPixels[idx + 2]);
            }
        }

        return image;
    }

    /// <summary>
    /// Build image from indexed (palette) pixel data.
    /// For scanned documents, the palette is typically grayscale.
    /// We attempt to extract the palette from EncodedImageData; if unavailable, use linear grayscale.
    /// </summary>
    private static SixLabors.ImageSharp.Image BuildIndexedImage(byte[] rawPixels, int width, int height, EncodedImageData encodedData)
    {
        // Try to extract palette via reflection from the colorspace
        byte[]? palette = null;
        int paletteSize = 256; // Typical for 8bpc indexed

        try
        {
            // EncodedImageData.ColorSpace might have palette info
            var csType = encodedData.ColorSpace?.GetType();
            if (csType != null)
            {
                // Look for palette/lookup/colors property
                var props = csType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var p in props)
                {
                    if (p.PropertyType == typeof(byte[]) && p.Name.Contains("ookup", StringComparison.OrdinalIgnoreCase))
                    {
                        palette = p.GetValue(encodedData.ColorSpace) as byte[];
                        break;
                    }
                }
            }
        }
        catch { }

        var image = new Image<L8>(width, height);

        if (palette != null && palette.Length >= 3)
        {
            // Palette has RGB entries: each index maps to 3 bytes (R,G,B)
            int maxIdx = palette.Length / 3;
            for (int y = 0; y < height && y * width < rawPixels.Length; y++)
            {
                for (int x = 0; x < width && (y * width + x) < rawPixels.Length; x++)
                {
                    int idx = rawPixels[y * width + x];
                    if (idx < maxIdx)
                    {
                        byte r = palette[idx * 3];
                        byte g = palette[idx * 3 + 1];
                        byte b = palette[idx * 3 + 2];
                        // Convert to grayscale luminance
                        byte gray = (byte)(0.299 * r + 0.587 * g + 0.114 * b);
                        image[x, y] = new L8(gray);
                    }
                }
            }
        }
        else
        {
            // No palette found — assume linear grayscale mapping
            for (int y = 0; y < height && y * width < rawPixels.Length; y++)
            {
                for (int x = 0; x < width && (y * width + x) < rawPixels.Length; x++)
                {
                    image[x, y] = new L8(rawPixels[y * width + x]);
                }
            }
        }

        return image;
    }

}

/// <summary>
/// Info about an extracted image from PDF content
/// </summary>
public class ExtractedImageInfo
{
    public int Index { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public string ElementType { get; set; } = "";
    public bool HasEncodedData { get; set; }
    public int EncodedDataLength { get; set; }
    public string Filters { get; set; } = "";
    public int BitsPerComponent { get; set; }
    public string ColorSpace { get; set; } = "";
    public string? EncodedDataError { get; set; }
}
