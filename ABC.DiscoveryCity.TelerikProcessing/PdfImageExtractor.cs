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
    /// Diagnostic: dump ImageSource colorspace internals for a PDF image.
    /// Useful for identifying colorspace types and palette data.
    /// </summary>
    public void DiagnoseDump(string pdfPath, int pageIndex = 0)
    {
        var provider = new PdfFormatProvider();
        RadFixedDocument doc;
        using (var stream = File.OpenRead(pdfPath))
            doc = provider.Import(stream);

        var page = doc.Pages[pageIndex];
        Console.WriteLine($"Page {pageIndex + 1}: Size={page.Size.Width:F0}x{page.Size.Height:F0}, Content elements: {page.Content.Count}");

        foreach (var element in page.Content)
        {
            if (element is Telerik.Windows.Documents.Fixed.Model.Objects.Image img && img.ImageSource != null)
            {
                var src = img.ImageSource;
                var encodedData = src.GetEncodedImageData();
                Console.WriteLine($"  Image: {(int)encodedData.Width}x{(int)encodedData.Height}, Filter={encodedData.Filters?.FirstOrDefault()}, " +
                    $"ColorSpace={encodedData.ColorSpace}, BPC={encodedData.BitsPerComponent}, Data={encodedData.Data?.Length:N0} bytes");

                // Extract internal colorspace object via reflection
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var (csObj, _) = GetColorSpaceObject(src);
                if (csObj != null)
                {
                    Console.WriteLine($"  ColorSpace object: {csObj.GetType().FullName}");
                    var lookupProp = csObj.GetType().GetProperty("Lookup", flags);
                    var baseProp = csObj.GetType().GetProperty("Base", flags);
                    var hiValProp = csObj.GetType().GetProperty("HiVal", flags);
                    if (lookupProp != null)
                    {
                        var palette = lookupProp.GetValue(csObj) as byte[];
                        Console.WriteLine($"  Palette: {palette?.Length ?? 0} bytes, Base={baseProp?.GetValue(csObj)}, HiVal={hiValProp?.GetValue(csObj)}");
                        if (palette != null)
                        {
                            // Print hash + first/last few entries for comparison
                            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(palette))[..16];
                            Console.WriteLine($"  Palette SHA256 prefix: {hash}");
                            Console.WriteLine($"  First 5 entries: {string.Join(" | ", Enumerable.Range(0, Math.Min(5, palette.Length / 3)).Select(i => $"[{i}]=({palette[i*3]},{palette[i*3+1]},{palette[i*3+2]})"))}");
                            int maxIdx = palette.Length / 3;
                            Console.WriteLine($"  Last 5 entries: {string.Join(" | ", Enumerable.Range(Math.Max(0, maxIdx - 5), Math.Min(5, maxIdx)).Select(i => $"[{i}]=({palette[i*3]},{palette[i*3+1]},{palette[i*3+2]})"))}");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Extract the internal ColorSpace object from an ImageSource via reflection.
    /// Returns (colorSpaceObject, typeName) or (null, null) on failure.
    /// Path: ImageSource.colorSpace (PdfProperty&lt;ColorSpaceBase&gt;) → .Value
    /// </summary>
    private static (object? csObj, string? typeName) GetColorSpaceObject(ImageSource imageSource)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var csField = imageSource.GetType().GetField("colorSpace", flags);
            if (csField == null) return (null, null);

            var csWrapper = csField.GetValue(imageSource);
            if (csWrapper == null) return (null, null);

            var valueProp = csWrapper.GetType().GetProperty("Value", flags);
            var csObj = valueProp?.GetValue(csWrapper);
            return (csObj, csObj?.GetType().Name);
        }
        catch { return (null, null); }
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
    public (string FullPath, string ThumbPath, int Width, int Height, byte[]? PreviewData, byte[]? ThumbData) ExtractPageImage(string pdfPath, int pageIndex = 0, string? outputDir = null)
    {
        var provider = new PdfFormatProvider();
        RadFixedDocument doc;

        using (var stream = File.OpenRead(pdfPath))
        {
            doc = provider.Import(stream);
        }

        if (doc.Pages.Count == 0 || pageIndex >= doc.Pages.Count)
            return (string.Empty, string.Empty, 0, 0, null, null);

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
            return (string.Empty, string.Empty, 0, 0, null, null);

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
            return (string.Empty, string.Empty, 0, 0, null, null);

        int imgWidth = (int)encodedData.Width;
        int imgHeight = (int)encodedData.Height;
        string filter = encodedData.Filters?.FirstOrDefault() ?? "";
        string colorSpace = encodedData.ColorSpace?.ToString() ?? "";
        int bpc = encodedData.BitsPerComponent;

        Console.WriteLine($"  [IMG] {Path.GetFileName(pdfPath)}: {imgWidth}x{imgHeight}, Filter={filter}, ColorSpace={colorSpace} (Type={encodedData.ColorSpace?.GetType().Name}), BPC={bpc}");

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
                    // Extract palette from ImageSource's internal Indexed colorspace object
                    resultImage = BuildIndexedImage(rawPixels, imgWidth, imgHeight, largestImage.ImageSource);
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
                return (string.Empty, string.Empty, 0, 0, null, null);
            }

            if (resultImage == null)
                return (string.Empty, string.Empty, 0, 0, null, null);

            // Save preview JPEG — resized to max 400px wide for compact storage
            byte[]? previewBytes = null;
            byte[]? thumbBytes = null;
            int finalW = 0, finalH = 0;
            using (resultImage)
            {
                int previewMaxWidth = 400;
                if (resultImage.Width > previewMaxWidth)
                {
                    int previewHeight = (int)Math.Round(resultImage.Height * (previewMaxWidth / (double)resultImage.Width));
                    resultImage.Mutate(ctx => ctx.Resize(previewMaxWidth, previewHeight));
                }
                finalW = resultImage.Width;
                finalH = resultImage.Height;

                resultImage.SaveAsJpeg(fullPath, new JpegEncoder { Quality = 70 });

                // Capture preview bytes for DB storage
                using (var ms = new MemoryStream())
                {
                    resultImage.SaveAsJpeg(ms, new JpegEncoder { Quality = 70 });
                    previewBytes = ms.ToArray();
                }

                // Create thumbnail
                int thumbWidth = 100;
                int thumbHeight = (int)Math.Round(resultImage.Height * (thumbWidth / (double)resultImage.Width));

                using var thumb = resultImage.Clone(ctx => ctx.Resize(thumbWidth, thumbHeight));
                thumb.SaveAsJpeg(thumbPath, new JpegEncoder { Quality = 75 });

                // Capture thumb bytes for DB storage
                using (var ms = new MemoryStream())
                {
                    thumb.SaveAsJpeg(ms, new JpegEncoder { Quality = 75 });
                    thumbBytes = ms.ToArray();
                }
            }

            return (fullPath, thumbPath, finalW, finalH, previewBytes, thumbBytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  [ERROR] Image extraction: {ex.Message}");
            return (string.Empty, string.Empty, 0, 0, null, null);
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
    /// Extracts the palette from Telerik's internal Indexed colorspace object
    /// (via ImageSource → PdfProperty&lt;ColorSpaceBase&gt; → Indexed.Lookup).
    /// Preserves colour when the palette contains distinct RGB entries;
    /// falls back to grayscale only when the palette is absent or purely gray.
    /// </summary>
    private static SixLabors.ImageSharp.Image BuildIndexedImage(byte[] rawPixels, int width, int height, ImageSource imageSource)
    {
        byte[]? palette = null;

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var (csObj, _) = GetColorSpaceObject(imageSource);
            if (csObj != null)
            {
                var lookupProp = csObj.GetType().GetProperty("Lookup", flags);
                palette = lookupProp?.GetValue(csObj) as byte[];
                if (palette != null)
                {
                    var baseProp = csObj.GetType().GetProperty("Base", flags);
                    var hiValProp = csObj.GetType().GetProperty("HiVal", flags);
                    Console.WriteLine($"  [INDEXED] Palette extracted: {palette.Length} bytes, Base={baseProp?.GetValue(csObj)}, HiVal={hiValProp?.GetValue(csObj)}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"  [INDEXED] Palette extraction error: {ex.Message}"); }

        if (palette != null && palette.Length >= 3)
        {
            int maxIdx = palette.Length / 3;

            // Detect whether the palette is truly grayscale (every entry has R==G==B)
            bool isGrayPalette = true;
            for (int i = 0; i < maxIdx && isGrayPalette; i++)
            {
                byte r = palette[i * 3], g = palette[i * 3 + 1], b = palette[i * 3 + 2];
                if (r != g || g != b) isGrayPalette = false;
            }

            if (isGrayPalette)
            {
                // Pure grayscale palette — output L8
                var grayImage = new Image<L8>(width, height);
                for (int y = 0; y < height && y * width < rawPixels.Length; y++)
                    for (int x = 0; x < width && (y * width + x) < rawPixels.Length; x++)
                    {
                        int idx = rawPixels[y * width + x];
                        grayImage[x, y] = new L8(idx < maxIdx ? palette[idx * 3] : (byte)0);
                    }
                return grayImage;
            }
            else
            {
                // Colour palette — preserve full RGB
                var rgbImage = new Image<Rgb24>(width, height);
                for (int y = 0; y < height && y * width < rawPixels.Length; y++)
                    for (int x = 0; x < width && (y * width + x) < rawPixels.Length; x++)
                    {
                        int idx = rawPixels[y * width + x];
                        if (idx < maxIdx)
                            rgbImage[x, y] = new Rgb24(palette[idx * 3], palette[idx * 3 + 1], palette[idx * 3 + 2]);
                    }
                return rgbImage;
            }
        }
        else
        {
            // No palette found — assume linear grayscale mapping
            var grayImage = new Image<L8>(width, height);
            for (int y = 0; y < height && y * width < rawPixels.Length; y++)
                for (int x = 0; x < width && (y * width + x) < rawPixels.Length; x++)
                    grayImage[x, y] = new L8(rawPixels[y * width + x]);
            return grayImage;
        }
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
