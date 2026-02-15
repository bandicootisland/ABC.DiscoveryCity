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
        // HTML-encode first to prevent stray tags (e.g. <i> from hidden text under redaction bars)
        text = System.Net.WebUtility.HtmlEncode(text);
        // Add line break before sentence numbers like [1], [2], etc. (numbers only)
        var formatted = Regex.Replace(text, @"\[(\d+)\]", "<br/>[$1]");
        // Remove leading <br/> if text starts with a sentence number
        if (formatted.StartsWith("<br/>")) formatted = formatted.Substring(5);
        return formatted;
    }

    /// <summary>
    /// Highlights search terms in pre-formatted HTML text.
    /// Exact word matches get blue background, partial/substring matches get yellow.
    /// Call AFTER FormatWithSentenceBreaks — operates on HTML-encoded text.
    /// Single-pass regex skips HTML tags so inserted spans are never re-matched.
    /// </summary>
    public static string HighlightSearchTerms(this string html, string? searchQuery)
    {
        if (string.IsNullOrEmpty(html) || string.IsNullOrWhiteSpace(searchQuery))
            return html;

        var terms = searchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .Select(t => System.Net.WebUtility.HtmlEncode(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(t => t.Length) // longest first to avoid partial inside longer
            .ToList();

        if (terms.Count == 0) return html;

        // Single-pass: group 1 matches HTML tags (skip), group 2 matches any search term
        var escapedTerms = string.Join("|", terms.Select(Regex.Escape));
        var pattern = $@"(<[^>]+>)|({escapedTerms})";

        return Regex.Replace(html, pattern, match =>
        {
            if (match.Groups[1].Success)
                return match.Value; // HTML tag — leave untouched

            var matched = match.Groups[2].Value;
            var before = match.Index > 0 ? html[match.Index - 1] : ' ';
            var after = match.Index + match.Length < html.Length ? html[match.Index + match.Length] : ' ';
            var isWholeWord = !char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after);
            var bg = isWholeWord ? "#cce5ff" : "#fff3cd";
            return $"<span style=\"background-color:{bg};padding:0 2px;border-radius:2px;\">{matched}</span>";
        }, RegexOptions.IgnoreCase);
    }
}
