using System.Text.RegularExpressions;

namespace ABC.DiscoveryCity.Services;

public static class TextFormatExtensions
{
    /// <summary>
    /// Formats text with line breaks before sentence numbers [1], [2], etc.
    /// Only matches numeric brackets, not other bracketed text.
    /// </summary>
    public static string FormatWithSentenceBreaks(this string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Add line break before sentence numbers like [1], [2], etc. (numbers only)
        var formatted = Regex.Replace(text, @"\[(\d+)\]", "<br/>[$1]");
        // Remove leading <br/> if text starts with a sentence number
        if (formatted.StartsWith("<br/>")) formatted = formatted.Substring(5);
        return formatted;
    }
}
