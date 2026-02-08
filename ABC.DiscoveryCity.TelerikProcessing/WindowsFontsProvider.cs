using Telerik.Windows.Documents.Extensibility;
using Telerik.Windows.Documents.Core.Fonts;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// FontsProvider implementation that loads fonts from Windows system fonts directory.
/// Required for SkiaImageFormatProvider to render text in .NET Standard.
/// </summary>
public class WindowsFontsProvider : FontsProviderBase
{
    private static readonly string FontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    public override byte[] GetFontData(FontProperties fontProperties)
    {
        // Try to find the requested font, fall back to Arial
        string familyName = fontProperties.FontFamilyName?.ToLowerInvariant() ?? "arial";
        
        // Map common font families to Windows font files
        var fontMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "arial", "arial.ttf" },
            { "helvetica", "arial.ttf" },
            { "times new roman", "times.ttf" },
            { "times", "times.ttf" },
            { "courier", "cour.ttf" },
            { "courier new", "cour.ttf" },
            { "calibri", "calibri.ttf" },
            { "verdana", "verdana.ttf" },
            { "tahoma", "tahoma.ttf" },
            { "segoe ui", "segoeui.ttf" },
            { "georgia", "georgia.ttf" },
            { "trebuchet ms", "trebuc.ttf" }
        };

        // Try exact match first
        if (fontMappings.TryGetValue(familyName, out var fontFile))
        {
            var path = Path.Combine(FontsFolder, fontFile);
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }

        // Try partial match
        foreach (var mapping in fontMappings)
        {
            if (familyName.Contains(mapping.Key))
            {
                var path = Path.Combine(FontsFolder, mapping.Value);
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
        }

        // Default to Arial
        var arialPath = Path.Combine(FontsFolder, "arial.ttf");
        if (File.Exists(arialPath))
            return File.ReadAllBytes(arialPath);

        return Array.Empty<byte>();
    }

    /// <summary>
    /// Initialize the fonts provider. Call once at application startup.
    /// </summary>
    public static void Initialize()
    {
        FixedExtensibilityManager.FontsProvider = new WindowsFontsProvider();
        Console.WriteLine("Fonts Provider initialized.");
    }
}
