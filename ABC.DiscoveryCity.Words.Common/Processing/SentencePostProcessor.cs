using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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
            RemoveJunkSentences(sentences);
            MergeEllipsisFragments(sentences);
            MergeUrlFragments(sentences);
            // Future: MergeEmailFragments(sentences);
            // Future: MergeShortQuoteContinuations(sentences);
        }

        // =====================================================================
        //  Pass 0: Remove junk sentences (base64, binary blobs, repeated chars)
        // =====================================================================

        /// <summary>
        /// Remove sentences that are OCR/extraction artifacts rather than real text.
        /// Catches: base64 blobs, hex dumps, repeated character runs, and binary noise.
        /// Runs BEFORE merge passes so junk doesn't get merged into real sentences.
        /// </summary>
        private static void RemoveJunkSentences(List<Sentence> sentences)
        {
            for (int i = sentences.Count - 1; i >= 0; i--)
            {
                string text = GetSentenceText(sentences[i]);
                if (IsJunkText(text))
                {
                    sentences.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Determines if a sentence text is junk that should be filtered out.
        /// Returns true for: base64 blobs, hex dumps, repeated-char runs,
        /// binary gibberish, and very long strings with no spaces (data, not prose).
        /// 
        /// PRESERVES: [redact.char(N)] patterns (legitimate redaction markers),
        /// short sentences (≤10 chars), and normal OCR text even if slightly garbled.
        /// </summary>
        internal static bool IsJunkText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            // Short text is never junk — could be a page number, date, etc.
            if (text.Length <= 10) return false;

            // --- 1. Repeated character runs ---
            // e.g. "lllllllllllllllll" or "********************" or "___________"
            if (HasExcessiveRepeats(text)) return true;

            // --- 2. Base64 blobs ---
            // Long runs of alphanumeric + /+= with no spaces
            if (LooksLikeBase64(text)) return true;

            // --- 3. Hex dump artifacts ---
            // e.g. "0x7fffdcc88130 sqlite3_step 0x7fffdcc88308"
            if (LooksLikeHexDump(text)) return true;

            // --- 4. Binary/encoding noise ---
            // Very high ratio of non-letter, non-space chars
            if (IsBinaryNoise(text)) return true;

            // --- 5. No-space data blobs ---
            // Very long strings (>200 chars) with almost no whitespace = data, not prose
            if (IsDataBlob(text)) return true;

            return false;
        }

        /// <summary>
        /// Detects strings dominated by repeated characters.
        /// e.g. "lllllllllllllll", "***************", "───────────────"
        /// Threshold: any single char repeated 8+ times consecutively,
        /// OR >60% of the string is the same character.
        /// </summary>
        private static bool HasExcessiveRepeats(string text)
        {
            if (text.Length < 12) return false;

            // Check for consecutive runs of 8+
            int runLen = 1;
            char prev = text[0];
            for (int i = 1; i < text.Length; i++)
            {
                if (text[i] == prev && !char.IsWhiteSpace(prev))
                {
                    runLen++;
                    if (runLen >= 8) return true;
                }
                else
                {
                    prev = text[i];
                    runLen = 1;
                }
            }

            // Check for dominant character (>60% of non-whitespace)
            if (text.Length >= 20)
            {
                Span<int> freq = stackalloc int[128]; // ASCII range
                int nonSpace = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (!char.IsWhiteSpace(c))
                    {
                        nonSpace++;
                        if (c < 128) freq[c]++;
                    }
                }
                if (nonSpace > 0)
                {
                    int maxFreq = 0;
                    for (int i = 0; i < 128; i++)
                        if (freq[i] > maxFreq) maxFreq = freq[i];
                    if ((double)maxFreq / nonSpace > 0.60) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Detects base64-encoded content.
        /// Base64 characteristics: long alphanumeric+/+= runs, no spaces,
        /// very even character distribution. Minimum 40 chars.
        /// </summary>
        private static bool LooksLikeBase64(string text)
        {
            if (text.Length < 40) return false;

            // Count characters that are valid base64 (A-Z, a-z, 0-9, +, /, =)
            int b64Chars = 0;
            int spaces = 0;
            int totalNonSpace = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { spaces++; continue; }
                totalNonSpace++;
                if (char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=')
                    b64Chars++;
            }

            if (totalNonSpace == 0) return false;

            double b64Ratio = (double)b64Chars / totalNonSpace;
            double spaceRatio = (double)spaces / text.Length;

            // High base64 char ratio + very few spaces + reasonably long = base64 blob
            // Threshold: >90% base64 chars, <5% spaces, >60 non-space chars
            if (b64Ratio > 0.90 && spaceRatio < 0.05 && totalNonSpace > 60)
            {
                // Extra check: must have mixed case (base64 always has both)
                bool hasUpper = false, hasLower = false, hasDigit = false;
                for (int i = 0; i < Math.Min(text.Length, 100); i++)
                {
                    if (char.IsUpper(text[i])) hasUpper = true;
                    else if (char.IsLower(text[i])) hasLower = true;
                    else if (char.IsDigit(text[i])) hasDigit = true;
                }
                if (hasUpper && hasLower && hasDigit) return true;
            }

            return false;
        }

        /// <summary>
        /// Detects hex dump or memory address artifacts from device extractions.
        /// Pattern: multiple "0x" prefixed hex values, or strings dominated by hex digits.
        /// </summary>
        private static bool LooksLikeHexDump(string text)
        {
            if (text.Length < 20) return false;

            // Count "0x" occurrences — 3+ in one sentence = hex dump
            int hexPrefixes = 0;
            for (int i = 0; i < text.Length - 1; i++)
            {
                if (text[i] == '0' && text[i + 1] == 'x')
                    hexPrefixes++;
            }
            if (hexPrefixes >= 3) return true;

            // Dominated by hex chars (0-9, a-f, A-F) + whitespace — e.g. "4F 2A 7B 89 CC DD"
            if (text.Length >= 30)
            {
                int hexChars = 0, nonSpace = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.IsWhiteSpace(c)) continue;
                    nonSpace++;
                    if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                        hexChars++;
                }
                if (nonSpace > 0 && (double)hexChars / nonSpace > 0.85)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Detects binary/encoding noise — text with very high ratio of
        /// non-letter, non-digit, non-common-punctuation characters.
        /// Normal OCR garble has letters; binary extraction has control chars and symbols.
        /// </summary>
        private static bool IsBinaryNoise(string text)
        {
            if (text.Length < 30) return false;

            int garbage = 0;
            int total = 0;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) continue;
                total++;

                // Normal text characters: letters, digits, common punctuation
                if (char.IsLetterOrDigit(c)) continue;
                if (c == '.' || c == ',' || c == ';' || c == ':' || c == '!' || c == '?') continue;
                if (c == '\'' || c == '"' || c == '-' || c == '(' || c == ')') continue;
                if (c == '[' || c == ']' || c == '/' || c == '@' || c == '#') continue;
                if (c == '$' || c == '&' || c == '*' || c == '_') continue;

                garbage++;
            }

            if (total == 0) return false;

            // >50% non-standard characters in a 30+ char string = binary noise
            return (double)garbage / total > 0.50;
        }

        /// <summary>
        /// Detects long data blobs — strings over 200 chars with almost no whitespace.
        /// Real prose always has spaces between words. Data blobs (URLs, encoded content,
        /// concatenated identifiers) don't.
        /// 
        /// Exception: preserves [redact.char(N)] patterns which can be long but legitimate.
        /// </summary>
        private static bool IsDataBlob(string text)
        {
            if (text.Length < 200) return false;

            // Don't flag redaction patterns — strip them before measuring
            string stripped = text;
            if (text.Contains("[redact.", StringComparison.Ordinal))
            {
                stripped = Regex.Replace(text, @"\[redact\.\w+\(\d+\)\]", "");
                if (stripped.Length < 200) return false; // Was mostly redaction markers — that's fine
            }

            int spaces = 0;
            for (int i = 0; i < stripped.Length; i++)
                if (stripped[i] == ' ') spaces++;

            double spaceRatio = (double)spaces / stripped.Length;

            // Normal English prose: ~15-20% spaces. Data blob: <3%
            return spaceRatio < 0.03;
        }

        // =====================================================================
        //  Pass 0.5: Merge ellipsis fragments
        // =====================================================================

        /// <summary>
        /// When "..." appears in source text, the sentence builder splits each "."
        /// into its own sentence. This pass merges consecutive dot-only sentences
        /// back into the preceding sentence, restoring the ellipsis.
        /// e.g. ["Something.", ".", "."] → ["Something..."]
        /// </summary>
        private static void MergeEllipsisFragments(List<Sentence> sentences)
        {
            if (sentences.Count < 2) return;

            for (int i = 1; i < sentences.Count; /* conditional increment */)
            {
                string text = GetSentenceText(sentences[i]).Trim();
                if (text.Length > 0 && text.Length <= 3 && IsAllDots(text))
                {
                    MergeRange(sentences, i - 1, i);
                    // Don't increment — re-check same index in case next is also a dot
                }
                else
                {
                    i++;
                }
            }
        }

        private static bool IsAllDots(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (s[i] != '.') return false;
            return true;
        }

        // =====================================================================
        //  Pass 1: Merge URL fragment sentences
        // =====================================================================

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

        // =====================================================================
        //  Public string-level filter (for use in RunCleanUp / ingestion pipeline)
        // =====================================================================

        /// <summary>
        /// Filters a list of sentence strings, removing junk entries.
        /// Use this as a safety net in the ingestion pipeline AFTER sentence building,
        /// in case junk text slips through (e.g. from non-PDF sources or pre-built text).
        ///
        /// Strips the [N] prefix before testing, so it works on both raw and formatted sentences.
        /// </summary>
        public static List<string> FilterJunkStrings(List<string> sentences)
        {
            var result = new List<string>(sentences.Count);
            foreach (var s in sentences)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;

                // Strip leading \n[123] prefix that RunCleanUp adds, to test the actual content
                string testText = s.TrimStart('\n');
                if (testText.Length > 0 && testText[0] == '[')
                {
                    int closeBracket = testText.IndexOf(']');
                    if (closeBracket > 0 && closeBracket < 12)
                        testText = testText.Substring(closeBracket + 1).TrimStart();
                }

                if (!IsJunkText(testText))
                    result.Add(s);
            }
            return result;
        }

        // =====================================================================
        //  String-level text artifact cleanup (called from RunCleanUp pipeline)
        // =====================================================================

        /// <summary>
        /// Clean common OCR/extraction text artifacts in a single sentence string:
        /// - Space after opening parenthesis: "( M" → "(M", "( fax" → "(fax"
        /// - Spaces injected into URLs: "https:// www. example. com" → "https://www.example.com"
        /// </summary>
        public static string CleanTextArtifacts(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 3) return text;

            // 1. Remove space(s) after opening parenthesis: "( " → "("
            if (text.Contains("( "))
                text = Regex.Replace(text, @"\(\s+", "(");

            // 2. Collapse spaces in URLs
            text = CollapseUrlSpaces(text);

            return text;
        }

        /// <summary>
        /// Merge consecutive dot-only strings into the previous string.
        /// String-level safety net for ellipsis splitting (complements the
        /// Sentence-level MergeEllipsisFragments pass).
        /// </summary>
        public static List<string> MergeEllipsisSentences(List<string> sentences)
        {
            if (sentences.Count < 2) return sentences;

            for (int i = sentences.Count - 1; i >= 1; i--)
            {
                string trimmed = sentences[i].Trim();

                // Case 1: entire sentence is just dots — merge whole thing with previous
                if (trimmed.Length > 0 && trimmed.Length <= 3 && IsAllDots(trimmed))
                {
                    sentences[i - 1] = sentences[i - 1] + trimmed;
                    sentences.RemoveAt(i);
                }
                // Case 2: sentence STARTS with dots then real content — ". New text"
                // The leading dot(s) belong to the previous sentence (split ellipsis),
                // move them back and keep the content as this sentence.
                else if (trimmed.Length > 1 && trimmed[0] == '.')
                {
                    int dotCount = 0;
                    while (dotCount < trimmed.Length && trimmed[dotCount] == '.') dotCount++;
                    if (dotCount < trimmed.Length && dotCount <= 3)
                    {
                        sentences[i - 1] = sentences[i - 1] + trimmed.Substring(0, dotCount);
                        sentences[i] = trimmed.Substring(dotCount).TrimStart();
                    }
                }
            }
            return sentences;
        }

        /// <summary>
        /// Collapse spaces injected by OCR into URLs.
        /// Handles: "https :// www . example . com" → "https://www.example.com"
        /// Only activates when text contains a URL indicator (http/www).
        /// </summary>
        private static string CollapseUrlSpaces(string text)
        {
            if (text.IndexOf("http", StringComparison.OrdinalIgnoreCase) < 0 &&
                text.IndexOf("www.", StringComparison.OrdinalIgnoreCase) < 0 &&
                text.IndexOf("www ", StringComparison.OrdinalIgnoreCase) < 0)
                return text;

            // Step 1: Fix protocol spacing: "https :// " or "https:// " → "https://"
            text = Regex.Replace(text, @"(https?)\s*:\s*//\s*", "$1://", RegexOptions.IgnoreCase);

            // Step 2: Fix www spacing: "www . " or "www. " → "www."
            text = Regex.Replace(text, @"\bwww\s*\.\s*", "www.", RegexOptions.IgnoreCase);

            // Step 3: After a URL start (https:// or www.), collapse ". <lowercase>"
            // patterns that are URL domain fragments. Loop because each pass fixes one dot.
            // Guard: only collapses space before lowercase chars (not uppercase = new sentence).
            if (text.Contains("://") || text.Contains("www."))
            {
                string prev;
                do
                {
                    prev = text;
                    text = Regex.Replace(text,
                        @"((?:https?://|www\.)\S*?)\.\s+([a-z0-9])",
                        "$1.$2");
                } while (text != prev);
            }

            return text;
        }
    }
}
