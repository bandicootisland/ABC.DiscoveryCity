using System;
using System.Collections.Generic;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common.Domain
{
    public static class SlidingWindowChunker
    {
        private const int TargetWordsPerChunk = 350; // Tuned for 512-dim models
        private const int OverlapSentences = 1;      // Context glue

        // Input: "The whole book text..."
        // Output: Enumerable of strings (each ~350 words)
        public static IEnumerable<string> CreateChunks(string fullText)
        {
            if (string.IsNullOrWhiteSpace(fullText)) yield break;

            // 1. Split into Sentences (Heuristic)
            // A "true" NLP sentence splitter is better, but this works for 99% of cases.
            var sentences = SplitSentences(fullText);

            var currentChunk = new List<string>();
            int currentWordCount = 0;

            foreach (var sentence in sentences)
            {
                int sLen = CountWords(sentence);

                // Add sentence to buffer
                currentChunk.Add(sentence);
                currentWordCount += sLen;

                // 2. Check Capacity
                if (currentWordCount >= TargetWordsPerChunk)
                {
                    // Yield the complete chunk string
                    yield return string.Join(" ", currentChunk);

                    // 3. Handle Overlap (Prepare next chunk)
                    var nextChunk = new List<string>();
                    int nextCount = 0;

                    // Keep the last 'N' sentences from the current chunk
                    int startOverlapIndex = Math.Max(0, currentChunk.Count - OverlapSentences);

                    for (int i = startOverlapIndex; i < currentChunk.Count; i++)
                    {
                        nextChunk.Add(currentChunk[i]);
                        nextCount += CountWords(currentChunk[i]);
                    }

                    // Reset buffer
                    currentChunk = nextChunk;
                    currentWordCount = nextCount;
                }
            }

            // Yield whatever is left at the end
            if (currentChunk.Count > 0)
            {
                yield return string.Join(" ", currentChunk);
            }
        }

        // Helper: Rough Word Count (Fast)
        private static int CountWords(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int count = 0;
            bool inWord = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i])) inWord = false;
                else if (!inWord) { count++; inWord = true; }
            }
            return count;
        }

        // Helper: Sentence Splitter
        // Splits on '.', '?', '!' but keeps the delimiter attached to the sentence.
        private static IEnumerable<string> SplitSentences(string text)
        {
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // Check for sentence terminators
                // Logic: Must be followed by space or end of string to count (avoids "Mr. Smith")
                bool isTerminator = (c == '.' || c == '?' || c == '!');
                bool isEnd = (i == text.Length - 1) || char.IsWhiteSpace(text[i + 1]);

                if (isTerminator && isEnd)
                {
                    // Yield sentence including the punctuation
                    int length = i - start + 1;
                    yield return text.Substring(start, length).Trim();
                    start = i + 1;
                }
            }

            // Yield remainder
            if (start < text.Length)
            {
                string rem = text.Substring(start).Trim();
                if (rem.Length > 0) yield return rem;
            }
        }
    }
}