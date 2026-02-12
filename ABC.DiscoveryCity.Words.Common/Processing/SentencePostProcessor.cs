using System;
using System.Collections.Generic;

namespace ABC.DiscoveryCity.Words.Common.Processing
{
    /// <summary>
    /// Post-processing passes on built sentences. 
    /// The core sentence builder stays fast and untouched.
    /// Each pass is a simple static method that mutates the list in-place.
    /// </summary>
    public static class SentencePostProcessor
    {
        /// <summary>
        /// Run all registered post-processing passes.
        /// Called after sentence building, before freezing to ImmutableArray.
        /// </summary>
        public static void Process(List<Sentence> sentences)
        {
            MergeUrlFragments(sentences);
            // Future: MergeEmailFragments(sentences);
            // Future: MergeShortQuoteContinuations(sentences);
        }

        /// <summary>
        /// Pass 1: Merge URL fragment sentences.
        /// 
        /// Problem: "Visit www.nytimes.com for news" becomes 3+ sentences because
        /// the dot triggers sentence-end detection. Similarly, hyphens in query strings
        /// like "?s=the-has-hyphens" can cause breaks.
        /// 
        /// Heuristic: Look for clusters of short sentences. If combining them produces
        /// text containing a URL pattern (multiple dots, http://, www., etc.), merge them.
        /// 
        /// A URL will always have more than one period, and the text around the dots
        /// will be lowercase (pasted URLs have consistent casing).
        /// </summary>
        private static void MergeUrlFragments(List<Sentence> sentences)
        {
            if (sentences.Count < 2) return;

            // We scan left to right, looking for merge opportunities
            for (int i = 0; i < sentences.Count - 1; i++)
            {
                // Quick check: does the current sentence text contain a URL indicator?
                string currentText = GetSentenceText(sentences[i]);

                bool currentHasUrlHint = HasUrlIndicator(currentText);
                bool currentIsShort = CountContentWords(sentences[i]) <= 6;

                if (!currentHasUrlHint && !currentIsShort) continue;

                // Look ahead for a merge cluster
                int mergeEnd = i; // inclusive end of merge range

                for (int j = i + 1; j < sentences.Count; j++)
                {
                    string nextText = GetSentenceText(sentences[j]);
                    bool nextIsShort = CountContentWords(sentences[j]) <= 6;
                    bool nextHasUrlHint = HasUrlIndicator(nextText);

                    // Stop if the next sentence is long and has no URL hint
                    if (!nextIsShort && !nextHasUrlHint) break;

                    // Build the combined text to test
                    string combined = CombineRange(sentences, i, j);

                    if (LooksLikeUrlText(combined))
                    {
                        mergeEnd = j;
                    }
                    else if (!nextIsShort)
                    {
                        break; // No point looking further
                    }

                    // Safety: don't merge huge spans
                    if (j - i >= 8) break;
                }

                // If we found a range to merge, do it
                if (mergeEnd > i)
                {
                    MergeRange(sentences, i, mergeEnd);
                    // After merge, sentences[i] is the merged sentence.
                    // The count has shrunk. Don't increment i — re-evaluate from same position
                    // in case more merging is possible (unlikely but safe).
                    i--; // Will be incremented by the for loop
                }
            }
        }

        // --- Helpers ---

        /// <summary>
        /// Get plain text of a sentence (words joined, no extra allocations for detection).
        /// </summary>
        private static string GetSentenceText(Sentence s)
        {
            var words = s.words;
            if (words.IsEmpty) return "";

            // Fast path for single word
            if (words.Length == 1) return words[0].span.ToString();

            var sb = new System.Text.StringBuilder(words.Length * 6);
            for (int i = 0; i < words.Length; i++)
            {
                sb.Append(words[i].span);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Count non-whitespace, non-punctuation, non-tag words in a sentence.
        /// </summary>
        private static int CountContentWords(Sentence s)
        {
            int count = 0;
            var words = s.words;
            for (int i = 0; i < words.Length; i++)
            {
                var span = words[i].span;
                if (span.IsEmpty) continue;
                char first = span[0];
                if (first == '<' || char.IsWhiteSpace(first) || (span.Length == 1 && char.IsPunctuation(first)))
                    continue;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Does this text contain obvious URL indicators?
        /// </summary>
        private static bool HasUrlIndicator(string text)
        {
            if (text.Length < 4) return false;

            // Fast checks — no allocations, just IndexOf on the existing string
            if (text.Contains("://", StringComparison.Ordinal)) return true;
            if (text.Contains("www", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("http", StringComparison.OrdinalIgnoreCase)) return true;

            // Common TLDs immediately after a dot (url fragment like "nytimes.com")
            if (text.Contains(".com", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains(".org", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains(".net", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains(".gov", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains(".edu", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains(".co", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains(".io", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Does the combined text look like it contains a URL?
        /// Key insight: a URL always has more than one period, and the text
        /// around the dots is consistently lowercase.
        /// </summary>
        private static bool LooksLikeUrlText(string text)
        {
            // Must have a URL indicator
            if (!HasUrlIndicator(text)) return false;

            // Count dots in non-whitespace context (i.e., dots with letters on both sides)
            int urlDotCount = 0;
            for (int i = 1; i < text.Length - 1; i++)
            {
                if (text[i] == '.')
                {
                    char before = text[i - 1];
                    char after = text[i + 1];

                    // URL dot: letter/digit on both sides
                    if ((char.IsLetterOrDigit(before) || before == '/') &&
                        (char.IsLetterOrDigit(after) || after == '/'))
                    {
                        urlDotCount++;
                    }
                }
            }

            // A URL has at least one dot with letters on both sides (e.g. "nytimes.com")
            // Combined with the URL indicator check above, this is sufficient
            return urlDotCount >= 1;
        }

        /// <summary>
        /// Build combined text for a range of sentences.
        /// Sentences are joined with their natural content (words already include spacing).
        /// </summary>
        private static string CombineRange(List<Sentence> sentences, int start, int end)
        {
            var sb = new System.Text.StringBuilder(256);
            for (int i = start; i <= end; i++)
            {
                var words = sentences[i].words;
                for (int w = 0; w < words.Length; w++)
                {
                    sb.Append(words[w].span);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Merge sentences[start..end] into a single sentence at position start.
        /// Removes the merged entries from the list.
        /// </summary>
        private static void MergeRange(List<Sentence> sentences, int start, int end)
        {
            // Collect all words from the range
            var allWords = new List<Word>();

            for (int i = start; i <= end; i++)
            {
                var words = sentences[i].words;
                for (int w = 0; w < words.Length; w++)
                {
                    allWords.Add(words[w]);
                }
            }

            // Build merged SentenceData
            var mergedData = new SentenceData
            {
                Ordinal = sentences[start]._context?.Ordinal ?? 0,
                Words = allWords.ToArray(),
                EndChar = sentences[end]._context?.EndChar ?? ""
            };

            // Replace start with merged sentence, remove the rest
            sentences[start] = new Sentence(mergedData);
            sentences.RemoveRange(start + 1, end - start);
        }
    }
}
