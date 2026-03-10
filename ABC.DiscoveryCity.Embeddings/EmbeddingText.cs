using System.Text;
using System.Text.RegularExpressions;

namespace ABC.DiscoveryCity.Embeddings;

/// <summary>
/// Prepares text for embedding by stripping OCR noise down to words only.
/// Call before passing text to IEmbeddingService — keeps text processing
/// in the pipeline, not inside the embedding API call.
/// </summary>
public static class EmbeddingText
{
    private static readonly Regex WordPattern = new(@"[a-zA-Z]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Extracts only letter-based words (2+ chars), space-separated.
    /// Drops OCR artifacts, symbols, single junk chars, numeric noise.
    /// Names survive as letter sequences.
    /// </summary>
    public static string WordsOnly(string text)
    {
        var sb = new StringBuilder(text.Length / 2);
        foreach (Match m in WordPattern.Matches(text))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(m.Value);
        }
        return sb.ToString();
    }
}
