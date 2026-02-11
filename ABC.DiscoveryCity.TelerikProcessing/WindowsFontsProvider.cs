using Telerik.Windows.Documents.Core.Fonts;
using Telerik.Windows.Documents.Extensibility;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Provides font data from system fonts folder for Telerik RadPdfProcessing.
/// Required for cross-platform PDF import/rendering.
/// </summary>
public class WindowsFontsProvider : FontsProviderBase
{
    private static readonly string FontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    private static readonly Dictionary<string, byte[]> FontCache = new();

    public override byte[]? GetFontData(FontProperties fontProperties)
    {
        string fontFamily = fontProperties.FontFamilyName?.ToLowerInvariant() ?? "arial";

        // Try to find a matching font file
        string[] possibleNames = fontFamily switch
        {
            "arial" => new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" },
            "times new roman" => new[] { "times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf" },
            "courier new" => new[] { "cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf" },
            "calibri" => new[] { "calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf" },
            _ => new[] { "arial.ttf" }
        };

        foreach (var fontFile in possibleNames)
        {
            string fontPath = Path.Combine(FontsFolder, fontFile);
            if (File.Exists(fontPath))
            {
                if (!FontCache.TryGetValue(fontPath, out var data))
                {
                    data = File.ReadAllBytes(fontPath);
                    FontCache[fontPath] = data;
                }
                return data;
            }
        }

        // Ultimate fallback to Arial
        string fallbackPath = Path.Combine(FontsFolder, "arial.ttf");
        if (File.Exists(fallbackPath))
        {
            if (!FontCache.TryGetValue(fallbackPath, out var data))
            {
                data = File.ReadAllBytes(fallbackPath);
                FontCache[fallbackPath] = data;
            }
            return data;
        }

        return null;
    }
}
