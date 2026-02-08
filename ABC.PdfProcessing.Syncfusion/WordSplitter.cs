using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ABC.PdfProcessing.Syncfusion
{
    public class WordSplitter
    {
        private static HashSet<string> _dictionary = null!;
        private static HashSet<string> _commonWords = null!;
        private static int _maxWordLength = 0;
        private static readonly object _lock = new object();
        
        // Words shorter than this are candidates for merging with neighbors
        private const int MinSubstantialWordLength = 5;

        public WordSplitter(string dictionaryPath)
        {
            Initialize(dictionaryPath);
        }

        private void Initialize(string dictionaryPath)
        {
            if (_dictionary != null) return;

            lock (_lock)
            {
                if (_dictionary != null) return;

                // Common short words that are likely correct (not OCR errors)
                _commonWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "the", "and", "for", "are", "but", "not", "you", "all", "can", "had",
                    "her", "was", "one", "our", "out", "has", "his", "how", "its", "may",
                    "new", "now", "old", "see", "two", "way", "who", "did", "get", "let",
                    "put", "say", "she", "too", "use", "of", "to", "in", "is", "it", "be",
                    "as", "at", "so", "we", "he", "by", "or", "on", "do", "if", "me", "my",
                    "up", "an", "go", "no", "us", "am", "a", "i", "with", "that", "this",
                    "from", "they", "been", "have", "were", "said", "each", "which", "their",
                    "will", "other", "about", "into", "than", "them", "then", "some", "when"
                };

                if (File.Exists(dictionaryPath))
                {
                    var words = File.ReadAllLines(dictionaryPath);
                    _dictionary = new HashSet<string>(words.Length, StringComparer.OrdinalIgnoreCase);

                    foreach (var word in words)
                    {
                        if (string.IsNullOrWhiteSpace(word) || (word.Length < 2 && word != "a" && word != "i")) continue;

                        _dictionary.Add(word);
                        if (word.Length > _maxWordLength)
                            _maxWordLength = word.Length;
                    }
                }
                else
                {
                    _dictionary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public string Process(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _dictionary.Count == 0)
                return text;

            // First pass: collect all tokens (words and whitespace sequences)
            var tokens = TokenizePreservingWhitespace(text);
            
            // Second pass: process each word token through capital-letter splitting and word splitting
            for (int t = 0; t < tokens.Count; t++)
            {
                if (tokens[t].IsWhitespace || tokens[t].Text.Length < 4)
                    continue;

                if (!IsLettersOnly(tokens[t].Text))
                    continue;

                var block = tokens[t].Text.AsSpan();
                
                // Split by capital letters first
                var capitalSplitParts = SplitByCapitals(block);
                
                if (capitalSplitParts.Count > 1)
                {
                    // Replace this token with multiple tokens
                    tokens.RemoveAt(t);
                    int insertIndex = t;
                    for (int p = 0; p < capitalSplitParts.Count; p++)
                    {
                        if (p > 0)
                        {
                            tokens.Insert(insertIndex, new Token(" ", true));
                            insertIndex++;
                        }
                        tokens.Insert(insertIndex, new Token(ProcessSingleBlock(capitalSplitParts[p]), false));
                        insertIndex++;
                    }
                    t = insertIndex - 1;
                    continue;
                }
                
                // Process single block
                tokens[t] = new Token(ProcessSingleBlock(tokens[t].Text), false);
            }

            // Third pass: try to merge adjacent short word tokens across spaces
            tokens = MergeAcrossSpaces(tokens);

            // Fourth pass: normalize multiple spaces to single space
            tokens = NormalizeSpaces(tokens);

            // Build result
            var result = new StringBuilder(text.Length + text.Length / 10);
            foreach (var token in tokens)
            {
                result.Append(token.Text);
            }

            return result.ToString();
        }

        private List<Token> NormalizeSpaces(List<Token> tokens)
        {
            var result = new List<Token>(tokens.Count);
            foreach (var token in tokens)
            {
                if (token.IsWhitespace)
                {
                    // Replace multiple spaces with single, but preserve newlines
                    if (token.Text.Contains('\n') || token.Text.Contains('\r'))
                    {
                        result.Add(token);
                    }
                    else if (token.Text.Length > 1)
                    {
                        result.Add(new Token(" ", true));
                    }
                    else
                    {
                        result.Add(token);
                    }
                }
                else
                {
                    result.Add(token);
                }
            }
            return result;
        }

        private string ProcessSingleBlock(string block)
        {
            if (block.Length < 4) return block;
            if (!IsLettersOnly(block)) return block;
            
            // Check if the whole block is a valid word
            if (IsWord(block.AsSpan()))
                return block;
            
            // Try to find the best split using dynamic programming / scoring
            var bestSplit = FindBestSplit(block);
            
            return string.Join(" ", bestSplit);
        }

        /// <summary>
        /// Find the best way to split a block into words using scoring
        /// </summary>
        private List<string> FindBestSplit(string block)
        {
            int n = block.Length;
            
            // For very long blocks, fall back to greedy to avoid performance issues
            if (n > 50)
            {
                return SplitBlockGreedy(block.AsSpan());
            }

            // dp[i] = best score for splitting block[0..i]
            // parent[i] = the start position of the word ending at i for the best split
            var dp = new int[n + 1];
            var parent = new int[n + 1];
            
            for (int i = 0; i <= n; i++)
            {
                dp[i] = int.MinValue;
                parent[i] = -1;
            }
            dp[0] = 0;

            for (int i = 1; i <= n; i++)
            {
                // Try all possible last words ending at position i
                int maxWordLen = Math.Min(i, _maxWordLength);
                
                for (int len = 1; len <= maxWordLen; len++)
                {
                    int start = i - len;
                    if (dp[start] == int.MinValue) continue;
                    
                    string word = block.Substring(start, len);
                    
                    // Only consider valid words (or single chars as fallback)
                    if (len == 1 || IsWord(word.AsSpan()))
                    {
                        int wordScore = ScoreSingleWord(word);
                        int totalScore = dp[start] + wordScore;
                        
                        if (totalScore > dp[i])
                        {
                            dp[i] = totalScore;
                            parent[i] = start;
                        }
                    }
                }
            }

            // Reconstruct the best split
            var result = new List<string>();
            int pos = n;
            while (pos > 0)
            {
                int start = parent[pos];
                if (start == -1)
                {
                    // Fallback: take single char
                    result.Add(block.Substring(pos - 1, 1));
                    pos--;
                }
                else
                {
                    result.Add(block.Substring(start, pos - start));
                    pos = start;
                }
            }
            
            result.Reverse();
            return result;
        }

        /// <summary>
        /// Score a single word for the DP algorithm
        /// </summary>
        private int ScoreSingleWord(string word)
        {
            if (!IsWord(word.AsSpan()))
            {
                // Single character fallback - heavy penalty
                return -50;
            }

            int score = 0;
            
            // Big bonus for common words
            if (IsCommonWord(word))
            {
                score += 25;
            }
            
            // Bonus for word length
            score += word.Length * 4;
            
            // Penalty for very short non-common words
            if (word.Length <= 2 && !IsCommonWord(word)) score -= 20;
            if (word.Length == 3 && !IsCommonWord(word)) score -= 10;
            
            // Bonus for substantial words
            if (word.Length >= MinSubstantialWordLength) score += 15;
            
            // Bonus for longer words (prefer "determination" over "deter" + "mination")
            if (word.Length >= 8) score += 10;
            if (word.Length >= 10) score += 10;
            
            return score;
        }

        /// <summary>
        /// Greedy split for very long blocks
        /// </summary>
        private List<string> SplitBlockGreedy(ReadOnlySpan<char> block)
        {
            var words = new List<string>();
            int currentPos = 0;

            while (currentPos < block.Length)
            {
                int remaining = block.Length - currentPos;
                if (remaining < 2)
                {
                    words.Add(block.Slice(currentPos).ToString());
                    break;
                }

                int bestLen = -1;
                int maxLen = Math.Min(remaining, _maxWordLength);

                for (int len = maxLen; len >= 2; len--)
                {
                    if (IsWord(block.Slice(currentPos, len)))
                    {
                        bestLen = len;
                        break;
                    }
                }

                if (bestLen != -1)
                {
                    words.Add(block.Slice(currentPos, bestLen).ToString());
                    currentPos += bestLen;
                }
                else
                {
                    words.Add(block[currentPos].ToString());
                    currentPos++;
                }
            }

            return words;
        }

        private List<Token> TokenizePreservingWhitespace(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    int start = i;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    tokens.Add(new Token(text[start..i], true));
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                    tokens.Add(new Token(text[start..i], false));
                }
            }
            
            return tokens;
        }

        private List<Token> MergeAcrossSpaces(List<Token> tokens)
        {
            var result = new List<Token>(tokens.Count);
            int i = 0;

            while (i < tokens.Count)
            {
                // Look for pattern: short_word + single_space + short_word
                // Only process letter-only tokens that are short and NOT common words
                if (!tokens[i].IsWhitespace && 
                    tokens[i].Text.Length > 0 && 
                    tokens[i].Text.Length < MinSubstantialWordLength &&
                    IsLettersOnly(tokens[i].Text) &&
                    !IsCommonWord(tokens[i].Text))
                {
                    // Try to look ahead and merge with next tokens
                    var mergeResult = TryMergeTokenSequence(tokens, i);
                    if (mergeResult != null)
                    {
                        result.AddRange(mergeResult.NewTokens);
                        i += mergeResult.ConsumedCount;
                        continue;
                    }
                }

                result.Add(tokens[i]);
                i++;
            }

            return result;
        }

        private bool IsCommonWord(string word)
        {
            return _commonWords.Contains(word);
        }

        private record MergeResult(List<Token> NewTokens, int ConsumedCount);

        private MergeResult? TryMergeTokenSequence(List<Token> tokens, int startIndex)
        {
            var wordTokens = new List<string>();
            int tokenCount = 0;
            
            int i = startIndex;
            while (i < tokens.Count && wordTokens.Count < 5)
            {
                if (tokens[i].IsWhitespace)
                {
                    // Only consider single spaces as merge candidates
                    if (tokens[i].Text == " ")
                    {
                        tokenCount++;
                        i++;
                    }
                    else
                    {
                        break; // Multiple spaces or newlines - stop
                    }
                }
                else
                {
                    if (!IsLettersOnly(tokens[i].Text))
                        break; // Non-letter content - stop
                    
                    wordTokens.Add(tokens[i].Text);
                    tokenCount++;
                    i++;
                }
            }

            if (wordTokens.Count < 2) return null;

            // Calculate how many tokens we'd consume for each merge count
            // Try merging 2, 3, 4, or 5 consecutive words
            for (int mergeCount = Math.Min(5, wordTokens.Count); mergeCount >= 2; mergeCount--)
            {
                var wordsToMerge = wordTokens.GetRange(0, mergeCount);
                string combined = string.Concat(wordsToMerge);

                // Skip if combined is too long
                if (combined.Length > _maxWordLength * 2) continue;

                // Use the same DP-based splitting on the combined text
                int originalScore = ScoreWordList(wordsToMerge);
                var newSplit = FindBestSplit(combined);
                int newScore = ScoreWordList(newSplit);

                if (newScore > originalScore)
                {
                    var newTokens = new List<Token>();
                    for (int r = 0; r < newSplit.Count; r++)
                    {
                        if (r > 0) newTokens.Add(new Token(" ", true));
                        newTokens.Add(new Token(newSplit[r], false));
                    }
                    int consumed = mergeCount * 2 - 1;
                    return new MergeResult(newTokens, consumed);
                }
            }

            return null;
        }

        private static bool IsLettersOnly(string text)
        {
            foreach (char c in text)
            {
                if (!char.IsLetter(c)) return false;
            }
            return text.Length > 0;
        }

        private List<string> SplitByCapitals(ReadOnlySpan<char> block)
        {
            var parts = new List<string>();
            int start = 0;
            
            for (int i = 1; i < block.Length; i++)
            {
                // Capital letter in middle indicates new word
                if (char.IsUpper(block[i]) && char.IsLetter(block[i - 1]))
                {
                    parts.Add(block.Slice(start, i - start).ToString());
                    start = i;
                }
            }
            
            // Add the remaining part
            if (start < block.Length)
            {
                parts.Add(block.Slice(start).ToString());
            }
            
            return parts;
        }

        private int ScoreWordList(List<string> words)
        {
            int score = 0;
            
            foreach (var word in words)
            {
                score += ScoreSingleWord(word);
            }
            
            // Penalty for having many words
            score -= words.Count * 3;
            
            return score;
        }

        private bool IsWord(ReadOnlySpan<char> candidate)
        {
            if (candidate.Length == 0) return false;
            
            if (candidate.Length <= 64)
            {
                Span<char> buffer = stackalloc char[candidate.Length];
                candidate.ToLowerInvariant(buffer);
                return _dictionary.Contains(new string(buffer));
            }
            else
            {
                return _dictionary.Contains(new string(candidate).ToLowerInvariant());
            }
        }

        private record struct Token(string Text, bool IsWhitespace);
    }
}
