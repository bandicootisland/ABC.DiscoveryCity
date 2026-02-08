using System;
using ABC.DiscoveryCity.Words.Common;

namespace ABC.DiscoveryCity.Words.Common.Domain
{
    public static class TextAnalyzer
    {
        public static (string text, WFlags flags, char punct) Analyze(ReadOnlySpan<char> rawToken)
        {
            if (rawToken.IsEmpty) return ("", WFlags.None, '\0');

            // --- FIX START: Ellipsis Guard ---
            // If the token is "...", return it as-is. Don't try to find punctuation.
            if (rawToken.Length == 3 && rawToken[0] == '.' && rawToken[1] == '.' && rawToken[2] == '.')
            {
                return ("...", WFlags.None, '\0');
            }
            // --- FIX END ---

            WFlags flags = WFlags.None;
            char punct = '\0';
            int start = 0;
            int end = rawToken.Length;

            // A. Check for Tags (Raw Mode)
            if (rawToken[0] == '<')
            {
                return (rawToken.ToString(), WFlags.IsRaw, '\0');
            }

            // B. Detect Opening Quote
            if (end > start)
            {
                char first = rawToken[start];
                if (first == '"' || first == '“')
                {
                    flags |= WFlags.QuoteOpen;
                    start++;
                }
            }

            // C. Detect Closing Quote
            if (end > start)
            {
                char last = rawToken[end - 1];
                if (last == '"' || last == '”')
                {
                    flags |= WFlags.QuoteClose;
                    end--;
                }
            }

            // D. Detect Trailing Punctuation
            if (end > start)
            {
                char last = rawToken[end - 1];
                if (last == '.' || last == '?' || last == '!' || last == ',')
                {
                    punct = last;
                    end--;
                }
            }

            // Edge Case: If we stripped everything (e.g. input "."), return empty.
            if (end <= start)
            {
                // If we captured punct (e.g. "."), return ("", None, '.')
                if (punct != '\0') return ("", flags, punct);
                return ("", WFlags.None, '\0');
            }

            ReadOnlySpan<char> coreWord = rawToken.Slice(start, end - start);

            // F. Detect Casing
            if (IsAllUpper(coreWord))
            {
                flags |= WFlags.UpperAll;
            }
            else if (char.IsUpper(coreWord[0]))
            {
                flags |= WFlags.UpperFirst;
            }

            // G. Lowercase for Storage
            string finalWord = coreWord.ToString();

            if ((flags & (WFlags.UpperFirst | WFlags.UpperAll)) != 0)
            {
                finalWord = finalWord.ToLowerInvariant();
            }

            return (finalWord, flags, punct);
        }

        private static bool IsAllUpper(ReadOnlySpan<char> span)
        {
            bool hasLetter = false;
            foreach (char c in span)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                    if (!char.IsUpper(c)) return false;
                }
            }
            return hasLetter;
        }
    }
}