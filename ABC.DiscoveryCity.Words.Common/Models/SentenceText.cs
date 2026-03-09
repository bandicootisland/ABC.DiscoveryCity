namespace ABC.DiscoveryCity.Words.Common.Models;

/// <summary>
/// Core text extraction that filters DSL annotations from sentence text.
/// DSL pattern: .Identifier() — a dot, one or more letters, then ().
/// Examples: hello.Bold() → "hello", .Tag() → skipped entirely.
///
/// DSL annotations are transitory — they can be added/removed without
/// invalidating checksums or changing display text.
///
/// Used by both server (SentenceIdBuilder) and client (display rendering).
/// </summary>
public static class SentenceText
{
    /// <summary>
    /// Returns the content length of a word, excluding any trailing DSL suffix.
    /// Returns 0 if the entire word is a DSL token (e.g. ".Bold()").
    /// Returns the full word length if no DSL suffix is present.
    /// Zero-allocation, span-safe.
    /// </summary>
    public static int ContentLength(ReadOnlySpan<char> word)
    {
        if (word.Length < 4) return word.Length; // minimum DSL: .X()
        if (word[^1] != ')' || word[^2] != '(') return word.Length;

        // Walk back from '(' to find the '.'
        for (int i = word.Length - 3; i >= 0; i--)
        {
            if (word[i] == '.')
            {
                // Verify at least one letter between . and ()
                if (i < word.Length - 3)
                    return i; // content is everything before the dot
                return word.Length; // .() alone is not valid DSL
            }
            if (!char.IsLetter(word[i])) return word.Length; // not a valid identifier char
        }

        return word.Length;
    }

    /// <summary>
    /// Returns true if the entire word is a DSL-only token (no content portion).
    /// </summary>
    public static bool IsDslOnly(ReadOnlySpan<char> word) => ContentLength(word) == 0;

    /// <summary>
    /// Extracts display text from a sentence, stripping all DSL annotations.
    /// Words that are DSL-only are removed; words with DSL suffixes keep their content.
    /// </summary>
    public static string ExtractContent(ReadOnlySpan<char> sentence)
    {
        var sb = new System.Text.StringBuilder(sentence.Length);
        int wordStart = -1;
        bool needsSpace = false;

        for (int i = 0; i <= sentence.Length; i++)
        {
            bool isEnd = i == sentence.Length;
            bool isWs = !isEnd && (sentence[i] == ' ' || sentence[i] == '\t' ||
                                    sentence[i] == '\n' || sentence[i] == '\r');

            if (isEnd || isWs)
            {
                if (wordStart >= 0)
                {
                    var word = sentence[wordStart..i];
                    int contentLen = ContentLength(word);
                    if (contentLen > 0)
                    {
                        if (needsSpace) sb.Append(' ');
                        sb.Append(word[..contentLen]);
                        needsSpace = true;
                    }
                    wordStart = -1;
                }
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Computes positional checksum excluding DSL annotations.
    /// Same Adler-style algorithm as SentenceIdBuilder but DSL-aware.
    /// </summary>
    public static ushort ComputePositionalChecksum(ReadOnlySpan<char> sentence)
    {
        uint sum = 0;
        int wordIndex = 0; // only incremented for content words
        int wordStart = -1;

        for (int i = 0; i <= sentence.Length; i++)
        {
            bool isEnd = i == sentence.Length;
            bool isWs = !isEnd && (sentence[i] == ' ' || sentence[i] == '\t' ||
                                    sentence[i] == '\n' || sentence[i] == '\r');

            if (isEnd || isWs)
            {
                if (wordStart >= 0)
                {
                    var word = sentence[wordStart..i];
                    int contentLen = ContentLength(word);
                    if (contentLen > 0)
                    {
                        uint multiplier = (uint)(wordIndex + 1);
                        for (int j = 0; j < contentLen; j++)
                            sum += (uint)word[j] * multiplier;
                        wordIndex++;
                    }
                    wordStart = -1;
                }
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        return (ushort)(sum % 65521);
    }

    /// <summary>
    /// Counts content words, excluding DSL-only tokens.
    /// </summary>
    public static byte CountWords(ReadOnlySpan<char> sentence)
    {
        int count = 0;
        int wordStart = -1;

        for (int i = 0; i <= sentence.Length; i++)
        {
            bool isEnd = i == sentence.Length;
            bool isWs = !isEnd && (sentence[i] == ' ' || sentence[i] == '\t' ||
                                    sentence[i] == '\n' || sentence[i] == '\r');

            if (isEnd || isWs)
            {
                if (wordStart >= 0)
                {
                    var word = sentence[wordStart..i];
                    if (ContentLength(word) > 0) count++;
                    wordStart = -1;
                }
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        return (byte)Math.Min(count, 255);
    }

    /// <summary>
    /// Computes char mass of content text only (excluding DSL suffixes).
    /// </summary>
    public static ushort CharMass(ReadOnlySpan<char> sentence)
    {
        int total = 0;
        int wordStart = -1;
        bool hadContent = false;

        for (int i = 0; i <= sentence.Length; i++)
        {
            bool isEnd = i == sentence.Length;
            bool isWs = !isEnd && (sentence[i] == ' ' || sentence[i] == '\t' ||
                                    sentence[i] == '\n' || sentence[i] == '\r');

            if (isEnd || isWs)
            {
                if (wordStart >= 0)
                {
                    var word = sentence[wordStart..i];
                    int contentLen = ContentLength(word);
                    if (contentLen > 0)
                    {
                        if (hadContent) total++; // inter-word space
                        total += contentLen;
                        hadContent = true;
                    }
                    wordStart = -1;
                }
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        return (ushort)Math.Min(total, 4095);
    }
}
