using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common.Processing;

public struct WordInfo
{
    public int Ordinal;
    public ReadOnlyMemory<char> Text;
    public bool IsValidWord;
    public bool UseAlternates;
    public int SkipCount;
    public List<WordInfo> Alternates;
}

/// <summary>
/// A high-performance word splitter that uses a dictionary and Span-based heuristics
/// to fix joined words in extracted PDF text.
/// Ported from BookCity's ABC.PdfProcessing.Syncfusion.WordSplitter with full
/// scoring-based split logic (2-part, 3-part, end, middle).
/// </summary>
public class WordSplitter
{
    private static readonly HashSet<string> _dictionary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup = _dictionary.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly object _lock = new object();
    private static int _maxWordLength = 0;

    public static int DictionaryCount => _dictionary.Count;

    public WordSplitter(string dictionaryPath)
    {
        Initialize(dictionaryPath);
    }

    public WordSplitter(string[] words)
    {
        Initialize(words);
    }

    public void Initialize(string dictionaryPath)
    {
        if (_dictionary.Count > 0) return;

        lock (_lock)
        {
            if (_dictionary.Count > 0) return;

            if (File.Exists(dictionaryPath))
            {
                var words = File.ReadAllLines(dictionaryPath);
                foreach (var word in words)
                {
                    if (string.IsNullOrWhiteSpace(word)) continue;
                    string w = word.Trim().ToLowerInvariant();
                    if (w.Length < 2 && w != "a" && w != "i") continue;

                    _dictionary.Add(w);
                    if (w.Length > _maxWordLength) _maxWordLength = w.Length;
                }
            }

            AddLegalTerms();
        }
    }

    public void Initialize(string[] words)
    {
        if (_dictionary.Count > 0) return;

        lock (_lock)
        {
            if (_dictionary.Count > 0) return;

            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word)) continue;
                string w = word.Trim().ToLowerInvariant();

                if (w.Length < 2 && w != "a" && w != "i") continue;

                _dictionary.Add(w);
                if (w.Length > _maxWordLength) _maxWordLength = w.Length;
            }

            AddLegalTerms();
        }
    }

    /// <summary>
    /// Adds domain-specific legal terms that may be missing from general dictionaries.
    /// </summary>
    private static void AddLegalTerms()
    {
        string[] legalTerms =
        {
            "defendant", "defendants", "plaintiff", "plaintiffs",
            "correctional", "institutional", "surveillance",
            "incarcerated", "subpoenaed", "trafficking",
            "superseding", "indictment", "arraignment",
            "deposition", "prosecutorial", "adjudicated",
            "jurisdictional", "extradition", "exculpatory",
            "corroborated", "obstruction", "conspiracy"
        };
        foreach (var term in legalTerms)
            _dictionary.Add(term);
    }

    public bool IsValidWord(string word) => IsValidWord(word.AsSpan());

    public bool IsValidWord(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty) return false;

        // 1. Trim trailing spaces (IdentifyWords includes them)
        var trimmed = word.Trim();
        if (trimmed.IsEmpty) return false;

        // 2. Fast path: check direct lookup
        if (_lookup.Contains(trimmed)) return true;

        // 3. Slow path: Normalize ligatures and strip non-letters for dictionary check
        string s = trimmed.ToString()
            .Replace("ﬀ", "ff")
            .Replace("ﬁ", "fi")
            .Replace("ﬂ", "fl")
            .Replace("ﬃ", "ffi")
            .Replace("ﬄ", "ffl")
            .Replace("ﬅ", "st")
            .Replace("ﬆ", "st");

        // Build a string of only letters for the dictionary check
        StringBuilder sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsLetter(c)) sb.Append(c);
        }

        if (sb.Length == 0) return false;
        return _lookup.Contains(sb.ToString());
    }

    public string Process(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        List<WordInfo> words = IdentifyWords(text);

        RejoinWords(words);

        FindValidWordsSplitFromWordStart(words);
        FindValidWordsSplitFromWordEnd(words);
        FindValidWordsSplitFromMiddleWord(words);

        return CombineAll(words);
    }

    private static string CombineAll(List<WordInfo> words)
    {
        StringBuilder sb = new StringBuilder(words.Count * 5);
        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (word.UseAlternates && word.Alternates != null && word.Alternates.Count > 0)
            {
                foreach (var alt in word.Alternates)
                {
                    sb.Append(alt.Text.Span);
                }
                i += word.SkipCount;
            }
            else
            {
                sb.Append(word.Text.Span);
            }
        }
        return sb.ToString();
    }

    private List<WordInfo> IdentifyWords(string text)
    {
        ReadOnlyMemory<char> memory = text.AsMemory();
        ReadOnlySpan<char> span = memory.Span;
        List<WordInfo> words = new List<WordInfo>(text.Length / 5);
        int ordinal = 0;
        int i = 0;

        while (i < span.Length)
        {
            // Skip control characters
            while (i < span.Length && char.IsControl(span[i]))
            {
                i++;
            }

            if (i >= span.Length) break;

            int start = i;

            // 1. Collect non-whitespace, non-punctuation characters (the "core" of the word)
            while (i < span.Length && !char.IsWhiteSpace(span[i]) && !char.IsControl(span[i]) && !char.IsPunctuation(span[i]))
            {
                i++;
            }

            // 2. Collect trailing whitespace and punctuation (they "belong" to this word)
            while (i < span.Length && (char.IsWhiteSpace(span[i]) || char.IsPunctuation(span[i])))
            {
                i++;
            }

            if (i > start)
            {
                ReadOnlyMemory<char> wordMemory = memory.Slice(start, i - start);
                words.Add(new WordInfo
                {
                    Ordinal = ordinal++,
                    Text = wordMemory,
                    IsValidWord = IsValidWord(wordMemory.Span),
                    Alternates = new List<WordInfo>()
                });
            }
        }
        return words;
    }

    private void RejoinWords(List<WordInfo> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            // If it's already a long valid word, don't merge it into something else
            if (words[i].IsValidWord && words[i].Text.Length > 3) continue;

            StringBuilder sb = new StringBuilder();
            sb.Append(words[i].Text.Span);

            int bestSkip = 0;
            string? bestCombined = null;

            // Look ahead up to 8 words to find a valid combination
            for (int j = 1; j <= 8 && (i + j) < words.Count; j++)
            {
                sb.Append(words[i + j].Text.Span);
                string combined = sb.ToString();

                if (IsValidWord(combined))
                {
                    bestSkip = j;
                    bestCombined = combined;
                }
            }

            if (bestCombined != null)
            {
                var word = words[i];
                word.UseAlternates = true;
                word.SkipCount = bestSkip;
                word.Alternates = new List<WordInfo>
                {
                    new WordInfo { Ordinal = word.Ordinal, Text = bestCombined.AsMemory(), IsValidWord = true }
                };
                words[i] = word;
                i += bestSkip;
            }
        }
    }

    private void FindValidWordsSplitFromWordStart(List<WordInfo> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].IsValidWord) continue;
            FindValidWordsSplitFromStart(words, i, words[i]);
        }
    }

    private void FindValidWordsSplitFromStart(List<WordInfo> words, int i, WordInfo word)
    {
        ReadOnlySpan<char> span = word.Text.Span;

        // Heuristic: If starts with UpperCase letter, ends with space/punctuation,
        // AND is already a valid word — it's a proper noun, accept as-is.
        // Unlike BookCity (where Syncfusion adds spaces), DevExpress joins words
        // so we must NOT skip invalid uppercase tokens like "OverviewofInvestigation".
        if (span.Length > 1 && char.IsUpper(span[0]) && (char.IsWhiteSpace(span[span.Length - 1]) || char.IsPunctuation(span[span.Length - 1])))
        {
            if (IsValidWord(word.Text.Span))
            {
                words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, Alternates = new List<WordInfo>() };
                return;
            }
            // Fall through to split analysis for invalid uppercase tokens
        }

        // Strip trailing whitespace/punctuation for split analysis
        var trimmedSpan = span;
        while (trimmedSpan.Length > 0 && (char.IsWhiteSpace(trimmedSpan[trimmedSpan.Length - 1]) || char.IsPunctuation(trimmedSpan[trimmedSpan.Length - 1])))
            trimmedSpan = trimmedSpan.Slice(0, trimmedSpan.Length - 1);
        int coreLen = trimmedSpan.Length;

        if (coreLen <= 1) return;

        // Check if the whole core is already valid
        if (IsValidWord(trimmedSpan))
        {
            words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, Alternates = new List<WordInfo>() };
            return;
        }

        // --- Try 2-part splits ---
        // Score by the longest minimum-part-length — prevents greedy errors like
        // "ofthe" → "oft"+"he" (wrong) instead of "of"+"the" (correct).
        int bestScore2 = -1;
        int bestK2 = -1;

        for (int k = 2; k <= coreLen - 2; k++)
        {
            if (!IsValidWord(trimmedSpan.Slice(0, k))) continue;
            if (!IsValidWord(trimmedSpan.Slice(k))) continue;

            int score = Math.Min(k, coreLen - k);
            if (score > bestScore2) { bestScore2 = score; bestK2 = k; }
        }

        // --- Try 3-part splits (only for tokens >= 6 chars) ---
        int bestScore3 = -1;
        int bestJ3 = -1, bestK3 = -1;

        if (coreLen >= 6)
        {
            for (int j = 2; j <= coreLen - 4; j++)
            {
                if (!IsValidWord(trimmedSpan.Slice(0, j))) continue;
                for (int k = j + 2; k <= coreLen - 2; k++)
                {
                    if (!IsValidWord(trimmedSpan.Slice(j, k - j))) continue;
                    if (!IsValidWord(trimmedSpan.Slice(k))) continue;

                    int minPart = Math.Min(j, Math.Min(k - j, coreLen - k));
                    if (minPart > bestScore3) { bestScore3 = minPart; bestJ3 = j; bestK3 = k; }
                }
            }
        }

        // Prefer 3-part if it has a better or equal score (more words recovered)
        if (bestScore3 >= bestScore2 && bestJ3 > 0)
        {
            string p1 = trimmedSpan.Slice(0, bestJ3).ToString();
            string p2 = trimmedSpan.Slice(bestJ3, bestK3 - bestJ3).ToString();
            string trailing = span.Length > coreLen ? span.Slice(coreLen).ToString() : "";
            string p3 = trimmedSpan.Slice(bestK3).ToString() + trailing;

            var w1 = new WordInfo { Ordinal = word.Ordinal, Text = (p1 + " ").AsMemory(), IsValidWord = true, Alternates = new List<WordInfo>() };
            var w2 = new WordInfo { Ordinal = word.Ordinal, Text = (p2 + " ").AsMemory(), IsValidWord = true, Alternates = new List<WordInfo>() };
            var w3 = new WordInfo { Ordinal = word.Ordinal, Text = p3.AsMemory(), IsValidWord = true, Alternates = new List<WordInfo>() };

            words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, UseAlternates = true, Alternates = [w1, w2, w3] };
        }
        else if (bestK2 > 0)
        {
            string trailing = span.Length > coreLen ? span.Slice(coreLen).ToString() : "";
            ReadOnlyMemory<char> firstWithSpace = (trimmedSpan.Slice(0, bestK2).ToString() + " ").AsMemory();
            ReadOnlyMemory<char> second = (trimmedSpan.Slice(bestK2).ToString() + trailing).AsMemory();

            var w = new WordInfo { Ordinal = word.Ordinal, Text = firstWithSpace, IsValidWord = true, Alternates = new List<WordInfo>() };
            var w2 = new WordInfo { Ordinal = word.Ordinal, Text = second, IsValidWord = true, Alternates = new List<WordInfo>() };

            words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, UseAlternates = true, Alternates = [w, w2] };
        }
    }

    private void FindValidWordsSplitFromWordEnd(List<WordInfo> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].IsValidWord) continue;
            FindValidWordsSplitFromWordEnd(words, i, words[i]);
        }
    }

    private void FindValidWordsSplitFromWordEnd(List<WordInfo> words, int i, WordInfo word)
    {
        ReadOnlySpan<char> span = word.Text.Span;

        // Try to find the LONGEST valid suffix
        for (int k = 0; k < span.Length; k++)
        {
            ReadOnlyMemory<char> second = word.Text.Slice(k);
            if (IsValidWord(second.Span))
            {
                if (k == 0)
                {
                    words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, Alternates = new List<WordInfo>() };
                    break;
                }

                ReadOnlyMemory<char> first = (word.Text.Slice(0, k).ToString()).AsMemory();
                ReadOnlyMemory<char> secondPrependSpace = (" " + second.ToString()).AsMemory();
                var w1 = new WordInfo { Ordinal = word.Ordinal, Text = first, IsValidWord = IsValidWord(first.Span), Alternates = new List<WordInfo>() };
                var w2 = new WordInfo { Ordinal = word.Ordinal, Text = secondPrependSpace, IsValidWord = IsValidWord(secondPrependSpace.Span), Alternates = new List<WordInfo>() };

                words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, UseAlternates = true, Alternates = [w1, w2] };
                break;
            }
        }
    }

    private void FindValidWordsSplitFromMiddleWord(List<WordInfo> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].IsValidWord) continue;
            FindValidWordsSplitFromMiddleWord(words, i, words[i]);
        }
    }

    private void FindValidWordsSplitFromMiddleWord(List<WordInfo> words, int i, WordInfo word)
    {
        ReadOnlySpan<char> span = word.Text.Span;

        bool found = false;
        // Try to find the LONGEST valid word anywhere inside
        for (int len = span.Length; len >= 1; len--)
        {
            for (int start = 0; start <= span.Length - len; start++)
            {
                if (len == span.Length) continue;

                ReadOnlyMemory<char> middle = word.Text.Slice(start, len);
                if (IsValidWord(middle.Span))
                {
                    ReadOnlyMemory<char> prefix = word.Text.Slice(0, start);
                    ReadOnlyMemory<char> suffix = word.Text.Slice(start + len);

                    if (start == 0)
                    {
                        ReadOnlyMemory<char> middleWithSpace = (middle.ToString() + " ").AsMemory();
                        var w1 = new WordInfo { Ordinal = word.Ordinal, Text = middleWithSpace, IsValidWord = true, Alternates = new List<WordInfo>() };
                        var w2 = new WordInfo { Ordinal = word.Ordinal, Text = suffix, IsValidWord = IsValidWord(suffix.Span), Alternates = new List<WordInfo>() };
                        words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, UseAlternates = true, Alternates = [w1, w2] };
                    }
                    else if (start + len == span.Length)
                    {
                        ReadOnlyMemory<char> prefixWithSpace = (prefix.ToString() + " ").AsMemory();
                        var w1 = new WordInfo { Ordinal = word.Ordinal, Text = prefixWithSpace, IsValidWord = IsValidWord(prefixWithSpace.Span), Alternates = new List<WordInfo>() };
                        var w2 = new WordInfo { Ordinal = word.Ordinal, Text = middle, IsValidWord = true, Alternates = new List<WordInfo>() };
                        words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, UseAlternates = true, Alternates = [w1, w2] };
                    }
                    else
                    {
                        ReadOnlyMemory<char> prefixWithSpace = (prefix.ToString() + " ").AsMemory();
                        ReadOnlyMemory<char> middleWithSpace = (middle.ToString() + " ").AsMemory();

                        var w1 = new WordInfo { Ordinal = word.Ordinal, Text = prefixWithSpace, IsValidWord = IsValidWord(prefixWithSpace.Span), Alternates = new List<WordInfo>() };
                        var w2 = new WordInfo { Ordinal = word.Ordinal, Text = middleWithSpace, IsValidWord = true, Alternates = new List<WordInfo>() };
                        var w3 = new WordInfo { Ordinal = word.Ordinal, Text = suffix, IsValidWord = IsValidWord(suffix.Span), Alternates = new List<WordInfo>() };
                        words[i] = new WordInfo { Ordinal = word.Ordinal, Text = word.Text, IsValidWord = true, UseAlternates = true, Alternates = [w1, w2, w3] };
                    }

                    found = true;
                    break;
                }
            }
            if (found) break;
        }
    }
}
