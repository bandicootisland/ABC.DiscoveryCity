using System.Text;

namespace ABC.DiscoveryCity.Words.Common.Processing;

/// <summary>
/// High-performance text enhancement pipeline. Each pass is a span-based
/// transformation — no regex, minimal allocations. Passes run in order;
/// each receives the output of the previous pass.
///
/// Design: add new ITextPass implementations for future enhancements
/// (e.g., OCR ligature repair, smart quote normalization, whitespace collapse).
/// </summary>
public static class TextEnhancer
{
    private static readonly ITextPass[] _passes =
    [
        new MimeHexDecodePass(),
        new MimeSoftBreakPass(),
        new StrayEqualsPass(),
        new CollapseWhitespacePass(),
    ];

    /// <summary>
    /// Run all enhancement passes on the input text.
    /// Returns the original string if no changes were made (no allocation).
    /// </summary>
    public static string Enhance(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string current = text;
        foreach (var pass in _passes)
        {
            if (!pass.MayApply(current)) continue;
            string result = pass.Apply(current);
            current = result;
        }
        return current;
    }

    /// <summary>
    /// Run all enhancement passes on a list of sentences.
    /// Returns a new list with enhanced text.
    /// </summary>
    public static List<string> EnhanceAll(List<string> sentences)
    {
        var result = new List<string>(sentences.Count);
        foreach (var s in sentences)
            result.Add(Enhance(s));
        return result;
    }

    /// <summary>
    /// Connect the WordSplitter dictionary to StrayEqualsPass for intelligent
    /// letter recovery. Call once after dictionary is loaded.
    /// </summary>
    public static void SetDictionary(WordSplitter splitter) => StrayEqualsPass.SetDictionary(splitter);
}

/// <summary>
/// A single text transformation pass. Implementations must be stateless and thread-safe.
/// </summary>
public interface ITextPass
{
    /// <summary>
    /// Fast pre-check — return false to skip this pass entirely (avoids allocation).
    /// Should be O(1) or a quick scan.
    /// </summary>
    bool MayApply(string text);

    /// <summary>
    /// Apply the transformation. Return the original string if no changes were made.
    /// </summary>
    string Apply(string text);
}

// ═══════════════════════════════════════════════════════════════════════
//  Pass 1: MIME Quoted-Printable hex decode (=XX → character)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Decodes MIME quoted-printable sequences: =XX where XX is a hex byte.
/// =20 → space, =3D → '=', =0D/=0A → space (line endings).
/// Non-printable results (&lt;0x20 or &gt;0x7E) are stripped.
/// Single-pass, span-based, zero regex.
/// </summary>
internal sealed class MimeHexDecodePass : ITextPass
{
    public bool MayApply(string text) => text.Contains('=');

    public string Apply(string text)
    {
        var span = text.AsSpan();
        // Quick scan: is there actually a =XX pattern?
        bool hasPattern = false;
        for (int i = 0; i < span.Length - 2; i++)
        {
            if (span[i] == '=' && IsHex(span[i + 1]) && IsHex(span[i + 2]))
            {
                hasPattern = true;
                break;
            }
        }
        if (!hasPattern) return text;

        var sb = new StringBuilder(text.Length);
        int pos = 0;
        while (pos < span.Length)
        {
            if (pos + 2 < span.Length && span[pos] == '=' &&
                IsHex(span[pos + 1]) && IsHex(span[pos + 2]))
            {
                int val = (HexVal(span[pos + 1]) << 4) | HexVal(span[pos + 2]);
                if (val == 0x0D || val == 0x0A)
                    sb.Append(' ');
                else if (val >= 0x20 && val <= 0x7E)
                    sb.Append((char)val);
                // else: non-printable, strip
                pos += 3;
            }
            else
            {
                sb.Append(span[pos]);
                pos++;
            }
        }
        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => 0,
    };
}

// ═══════════════════════════════════════════════════════════════════════
//  Pass 2: MIME soft line break removal (= at end of line)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Removes MIME soft line breaks: '=' immediately before CR/LF or CRLF.
/// These are continuation markers in quoted-printable encoding.
/// </summary>
internal sealed class MimeSoftBreakPass : ITextPass
{
    public bool MayApply(string text) => text.Contains('=');

    public string Apply(string text)
    {
        var span = text.AsSpan();
        bool hasPattern = false;
        for (int i = 0; i < span.Length - 1; i++)
        {
            if (span[i] == '=' && (span[i + 1] == '\n' || span[i + 1] == '\r'))
            {
                hasPattern = true;
                break;
            }
        }
        if (!hasPattern) return text;

        var sb = new StringBuilder(text.Length);
        int pos = 0;
        while (pos < span.Length)
        {
            if (span[pos] == '=' && pos + 1 < span.Length)
            {
                if (span[pos + 1] == '\n')
                {
                    pos += 2; // skip = and \n
                    continue;
                }
                if (span[pos + 1] == '\r')
                {
                    pos += (pos + 2 < span.Length && span[pos + 2] == '\n') ? 3 : 2;
                    continue;
                }
            }
            sb.Append(span[pos]);
            pos++;
        }
        return sb.ToString();
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  Pass 3: Stray '=' recovery (OCR/MIME artifact — missing letter)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Recovers letters corrupted by base64/MIME decoding that left '=' in place of a letter.
///
/// Strategy (when dictionary is available):
///   1. Extract the surrounding word containing the '='
///   2. Try replacing '=' with each letter a–z (26 lookups, avg ~13)
///   3. If exactly one letter produces a valid dictionary word → use it
///   4. If multiple matches → pick the most common letter (frequency-ranked)
///   5. If no match → fall back to simply removing the '='
///
/// Without a dictionary, falls back to just dropping stray '=' (original behaviour).
/// Does NOT touch '=' in operator contexts (==, !=, >=, <=).
/// </summary>
internal sealed class StrayEqualsPass : ITextPass
{
    // English letter frequency order (most → least common)
    private static readonly char[] LettersByFrequency =
        "etaoinsrhldcumfpgwybvkxjqz".ToCharArray();

    // Reference to the WordSplitter's dictionary — set once during pipeline init
    private static WordSplitter? _splitter;

    /// <summary>
    /// Connect the dictionary for intelligent letter recovery.
    /// Called once from WordSplitStep after the dictionary is loaded.
    /// </summary>
    public static void SetDictionary(WordSplitter splitter) => _splitter = splitter;

    public bool MayApply(string text) => text.Contains('=');

    public string Apply(string text)
    {
        var span = text.AsSpan();
        var sb = new StringBuilder(text.Length);
        bool changed = false;

        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] != '=')
            {
                sb.Append(span[i]);
                continue;
            }

            // Don't touch == or != or >= or <= (operator contexts)
            if (i + 1 < span.Length && span[i + 1] == '=') { sb.Append('='); continue; }
            if (i > 0 && (span[i - 1] == '!' || span[i - 1] == '>' || span[i - 1] == '<'))
            { sb.Append('='); continue; }

            // Case 1: letter = letter → try to recover the missing letter
            if (i > 0 && i + 1 < span.Length &&
                char.IsLetter(span[i - 1]) && char.IsLetter(span[i + 1]))
            {
                char? recovered = TryRecoverLetter(span, i);
                if (recovered.HasValue)
                    sb.Append(recovered.Value);
                else
                    sb.Append(' '); // no dictionary match — use space to preserve char length
                changed = true;
                continue;
            }

            // Case 2: (whitespace|start) = letter → drop the =
            if (i + 1 < span.Length && char.IsLetter(span[i + 1]) &&
                (i == 0 || char.IsWhiteSpace(span[i - 1])))
            {
                changed = true;
                continue;
            }

            // Case 3: letter = punctuation → drop the =
            if (i > 0 && i + 1 < span.Length &&
                char.IsLetter(span[i - 1]) && IsPunctuation(span[i + 1]))
            {
                changed = true;
                continue;
            }

            sb.Append('=');
        }

        return changed ? sb.ToString() : text;
    }

    /// <summary>
    /// Extract the word around position equalsPos, try each letter a-z in place of '=',
    /// and return the letter that produces a valid dictionary word.
    /// Returns null if no dictionary or no match (caller should just drop the =).
    /// </summary>
    private static char? TryRecoverLetter(ReadOnlySpan<char> span, int equalsPos)
    {
        if (_splitter == null) return null;

        // Find word boundaries around the '='
        int wordStart = equalsPos;
        while (wordStart > 0 && char.IsLetter(span[wordStart - 1]))
            wordStart--;

        int wordEnd = equalsPos;
        while (wordEnd + 1 < span.Length && char.IsLetter(span[wordEnd + 1]))
            wordEnd++;
        wordEnd++; // exclusive end

        // Build the word with a placeholder
        int wordLen = wordEnd - wordStart;
        if (wordLen < 3) return null; // too short to be meaningful

        Span<char> candidate = stackalloc char[wordLen];
        span.Slice(wordStart, wordLen).CopyTo(candidate);
        int posInWord = equalsPos - wordStart;

        // Try letters in frequency order — first match on a common letter is likely correct
        char? bestMatch = null;
        foreach (char c in LettersByFrequency)
        {
            candidate[posInWord] = c;
            if (_splitter.IsValidWord(candidate))
            {
                bestMatch = c;
                break; // frequency-ordered, first hit is best
            }

            // Also try uppercase if the position is at word start
            if (posInWord == 0)
            {
                candidate[posInWord] = char.ToUpper(c);
                if (_splitter.IsValidWord(candidate))
                {
                    bestMatch = char.ToUpper(c);
                    break;
                }
            }
        }

        // Preserve original case: if surrounding chars are uppercase, return uppercase
        if (bestMatch.HasValue && posInWord > 0 && char.IsUpper(span[equalsPos - 1]))
            bestMatch = char.ToUpper(bestMatch.Value);

        return bestMatch;
    }

    private static bool IsPunctuation(char c) =>
        c == ')' || c == ']' || c == '}' || c == '>' ||
        c == '.' || c == ',' || c == ';' || c == ':' ||
        c == '!' || c == '?' || c == '/';
}

// ═══════════════════════════════════════════════════════════════════════
//  Pass 4: Collapse runs of whitespace to single space
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Collapses runs of multiple spaces/tabs into a single space.
/// Preserves single newlines (paragraph structure).
/// Trims leading/trailing whitespace.
/// </summary>
internal sealed class CollapseWhitespacePass : ITextPass
{
    public bool MayApply(string text) => text.Contains("  ") || text.Contains('\t');

    public string Apply(string text)
    {
        var span = text.AsSpan();
        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = true; // trim leading

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\n' || c == '\r')
            {
                sb.Append(c);
                lastWasSpace = true;
                continue;
            }
            if (c == ' ' || c == '\t')
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }
            sb.Append(c);
            lastWasSpace = false;
        }

        // Trim trailing space
        if (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;

        return sb.ToString();
    }
}
