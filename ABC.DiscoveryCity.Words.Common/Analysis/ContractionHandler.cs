namespace ABC.DiscoveryCity.Words.Common.Analysis
{
    /// <summary>
    /// Handles detection and rejoining of contractions during tokenization.
    /// Keeps "didn't" as a single word instead of "didn" + "'" + "t".
    /// </summary>
    public static class ContractionHandler
    {
        // Common contraction suffixes (after apostrophe)
        private static readonly HashSet<string> ContractionSuffixes = new(StringComparer.OrdinalIgnoreCase)
        {
            // Negation
            "t",      // n't: didn't, couldn't, won't, don't, isn't, aren't, etc.
            "nt",     // alternate: haven't (if apostrophe consumed differently)

            // Pronouns + verb
            "m",      // 'm: I'm
            "re",     // 're: you're, we're, they're
            "ve",     // 've: I've, you've, we've, they've, could've, would've
            "ll",     // 'll: I'll, you'll, he'll, she'll, we'll, they'll
            "d",      // 'd: I'd, you'd, he'd, she'd (would/had)

            // is/has
            "s",      // 's: he's, she's, it's, that's, what's, who's, let's
        };

        // Words that commonly precede contractions (helps with 's ambiguity)
        private static readonly HashSet<string> ContractionRoots = new(StringComparer.OrdinalIgnoreCase)
        {
            // Pronouns
            "i", "you", "he", "she", "it", "we", "they",
            "who", "what", "that", "there", "here",

            // Common verbs for n't
            "do", "does", "did",
            "is", "are", "was", "were",
            "have", "has", "had",
            "will", "would", "could", "should", "might", "must",
            "can", "need", "dare", "ought",
            "wo",    // won't
            "sha",   // shan't
            "ai",    // ain't

            // let's
            "let",
        };

        /// <summary>
        /// Checks if a sequence of tokens forms a contraction.
        /// </summary>
        /// <param name="word">The word before the apostrophe (e.g., "didn")</param>
        /// <param name="apostrophe">The apostrophe character</param>
        /// <param name="suffix">The suffix after apostrophe (e.g., "t")</param>
        /// <returns>True if this is a contraction that should be joined.</returns>
        public static bool IsContraction(ReadOnlySpan<char> word, char apostrophe, ReadOnlySpan<char> suffix)
        {
            if (apostrophe != '\'' && apostrophe != '\u2019') return false;
            if (word.IsEmpty || suffix.IsEmpty) return false;

            // Check if suffix is a known contraction ending
            string suffixStr = suffix.ToString();
            if (!ContractionSuffixes.Contains(suffixStr)) return false;

            // For 't (n't contractions), check the word ends appropriately
            if (suffixStr.Equals("t", StringComparison.OrdinalIgnoreCase))
            {
                // Word should end with 'n' for n't
                return word.Length > 0 && (word[^1] == 'n' || word[^1] == 'N');
            }

            // For other suffixes, accept if word is a known root or looks like a word
            string wordStr = word.ToString();
            if (ContractionRoots.Contains(wordStr)) return true;

            // Also accept if word looks like a reasonable contraction root
            // (at least 1 char, all letters)
            if (word.Length >= 1)
            {
                foreach (char c in word)
                {
                    if (!char.IsLetter(c)) return false;
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Joins contraction parts into a single string.
        /// </summary>
        public static string JoinContraction(ReadOnlySpan<char> word, char apostrophe, ReadOnlySpan<char> suffix)
        {
            return $"{word}{apostrophe}{suffix}";
        }

        /// <summary>
        /// Checks if a character is an apostrophe (straight or curly).
        /// </summary>
        public static bool IsApostrophe(char c)
        {
            return c == '\'' || c == '\u2019' || c == '\u2018';
        }
    }
}
