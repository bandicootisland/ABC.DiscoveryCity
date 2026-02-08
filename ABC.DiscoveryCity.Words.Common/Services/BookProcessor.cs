using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Domain;
using System;
using System.Collections.Generic;

namespace ABC.DiscoveryCity.Services
{
    public static class BookProcessor
    {
        private const int TargetWordsPerChunk = 350;
        private const int OverlapSentences = 1;

        public static List<List<string>> ParseBook(BookContent content)
        {
            var result = new List<List<string>>();
            var chunkSentences = new List<Sentence>();
            int chunkWordCount = 0;
            int currentChunkStartIdx = 0;

            for (int i = 0; i < content.Sentences.Length; i++)
            {
                var sentence = content.Sentences[i];
                if (chunkSentences.Count == 0) currentChunkStartIdx = i;

                chunkSentences.Add(sentence);
                chunkWordCount += sentence.words.Length;

                if (chunkWordCount >= TargetWordsPerChunk)
                {
                    result.Add(CompileSentencesToDsl(chunkSentences, content, currentChunkStartIdx));

                    int keepCount = Math.Min(OverlapSentences, chunkSentences.Count);
                    int removeCount = chunkSentences.Count - keepCount;

                    var nextWindow = new List<Sentence>(keepCount + 10);
                    int nextWindowWordCount = 0;
                    for (int k = 0; k < keepCount; k++)
                    {
                        var s = chunkSentences[removeCount + k];
                        nextWindow.Add(s);
                        nextWindowWordCount += s.words.Length;
                    }

                    chunkSentences = nextWindow;
                    chunkWordCount = nextWindowWordCount;
                    currentChunkStartIdx += removeCount;
                }
            }

            if (chunkSentences.Count > 0)
            {
                result.Add(CompileSentencesToDsl(chunkSentences, content, currentChunkStartIdx));
            }

            return result;
        }

        private static List<string> CompileSentencesToDsl(List<Sentence> chunk, BookContent content, int chunkStartOrdinal)
        {
            var dslTokens = new List<string>(chunk.Count * 15);

            for (int i = 0; i < chunk.Count; i++)
            {
                var s = chunk[i];
                int sOrdinal = chunkStartOrdinal + i;
                var words = s.words;

                for (int j = 0; j < words.Length; j++)
                {
                    var w = words[j];
                    ReadOnlySpan<char> span = w.Text.Span;

                    // 1. SKIP SINGLE SPACES
                    if (span.IsSingleSpace()) continue;

                    // 2. WHITESPACE / SANDWICH LOGIC
                    if (span.IsAllWhitespace())
                    {
                        bool hasNewline = span.IndexOf('\n') >= 0 || span.IndexOf('\r') >= 0;
                        if (hasNewline)
                        {
                            // A. Find Prev (Simple step back)
                            Word prev = (j > 0) ? words[j - 1] : content.Sentences.prev(sOrdinal).lastword;

                            // B. Find Next (SKIP SPACES to find the real neighbor)
                            Word next = default;
                            int peek = j + 1;

                            // Scan forward within sentence to skip spaces
                            while (peek < words.Length && words[peek].Text.Span.IsSingleSpace()) peek++;

                            if (peek < words.Length)
                            {
                                next = words[peek];
                            }
                            else
                            {
                                // We hit end of sentence, check next sentence first word
                                next = content.Sentences.next(sOrdinal).firstword;
                            }

                            // C. The Check
                            if (prev.IsTag() && next.IsTag()) continue; // Drop Sandwich
                        }

                        dslTokens.Add(DslProtocol.Compile(span, WFlags.NoSpace, '\0'));
                        continue;
                    }

                    // 3. TAGS (With Quote Normalization)
                    if (w.IsTag())
                    {
                        // OPTIMIZATION: Swap " for ' to avoid JSON escaping and .r suffix
                        // Result: <p class='para'> (Clean, Implicit Raw)
                        // Note: We allocate a string here, but only for tags (low volume)
                        string cleanTag = span.ToString().Replace('"', '\'');

                        dslTokens.Add(DslProtocol.Compile(cleanTag, WFlags.IsRaw, '\0'));
                        continue;
                    }

                    // 4. PUNCTUATION SQUEEZE (Space-Aware)
                    char punctToUse = w.Punctuation;

                    // Look ahead (Skipping Spaces)
                    int pPeek = j + 1;
                    while (pPeek < words.Length && words[pPeek].Text.Span.IsSingleSpace()) pPeek++;

                    if (pPeek < words.Length)
                    {
                        var nextW = words[pPeek];
                        var nextSpan = nextW.Text.Span;

                        if (nextSpan.Length == 1)
                        {
                            char p = nextSpan[0];
                            if (p == '.' || p == ',' || p == '?' || p == '!')
                            {
                                punctToUse = p; // Steal it
                                j = pPeek;      // SKIP the punctuation token (and the spaces)
                            }
                        }
                    }

                    // 5. TEXT CASING
                    WFlags finalFlags = w.Flags;
                    if (char.IsUpper(span[0]) && (finalFlags & WFlags.UpperAll) == 0 && (finalFlags & WFlags.UpperFirst) == 0)
                    {
                        finalFlags |= WFlags.UpperFirst;
                    }

                    dslTokens.Add(DslProtocol.Compile(span, finalFlags, punctToUse));
                }
            }
            return dslTokens;
        }
    }
}