using System;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common.Domain
{
    
    public static class DslProtocol
    {
        // --- 1. PARSE (The Reader) ---
        public static Word Parse(ReadOnlySpan<char> dslInput, SentenceData? context, int index)
        {
            // A. Find the Instruction Block (Suffix)
            int dotIndex = dslInput.LastIndexOf('.');

            // B. Handle Plain Text (No Dot or Dot at start/end)
            if (dotIndex <= 0 || dotIndex == dslInput.Length - 1)
            {
                var t = new string(dslInput);
                WFlags implicitFlags = WFlags.None;

                // IMPLICIT RAW RULE (Optimization):
                // If it looks like a tag (<...), treat as Raw automatically.
                if (dslInput.Length > 0 && dslInput[0] == '<')
                {
                    implicitFlags |= WFlags.IsRaw;
                }

                return new Word(t.AsMemory(), implicitFlags, '\0', '\0', '\0', context, index, 0);
            }

            // C. Parse the Suffix Block (Greedy Lexer)
            WFlags flags = WFlags.None;
            char punctuation = '\0';

            // Isolate the Suffix Span (e.g. "ucqo")
            ReadOnlySpan<char> suffix = dslInput.Slice(dotIndex + 1);

            bool isValidBlock = true;
            int cursor = 0;

            while (cursor < suffix.Length)
            {
                bool matched = false;
                int remaining = suffix.Length - cursor;

                // Try 2-Letter Codes First (Atomic Priority)
                if (remaining >= 2)
                {
                    char c1 = suffix[cursor];
                    char c2 = suffix[cursor + 1];

                    // Spacing
                    if (c1 == 'n' && c2 == 's') { flags |= WFlags.NoSpace; cursor += 2; matched = true; }

                    // Quotes
                    else if (c1 == 'q' && c2 == 'o') { flags |= WFlags.QuoteOpen; cursor += 2; matched = true; }
                    else if (c1 == 'q' && c2 == 'c') { flags |= WFlags.QuoteClose; cursor += 2; matched = true; }

                    if (matched) continue;
                }

                // Try 1-Letter Codes
                if (!matched)
                {
                    char c = suffix[cursor];
                    switch (c)
                    {
                        case 'u': flags |= WFlags.UpperFirst; break;
                        case 'U': flags |= WFlags.UpperAll; break;
                        case 'r': flags |= WFlags.IsRaw; break;

                        // Punctuation Mapping
                        case 'e': punctuation = '.'; break;
                        case 'q': punctuation = '?'; break;
                        case 'x': punctuation = '!'; break;
                        case 'c': punctuation = ','; break;

                        default:
                            isValidBlock = false; // Invalid char found
                            break;
                    }

                    if (isValidBlock)
                    {
                        cursor += 1;
                        matched = true;
                    }
                }

                if (!matched || !isValidBlock)
                {
                    isValidBlock = false;
                    break;
                }
            }

            // D. Validation Check
            if (!isValidBlock)
            {
                // The suffix was actually text (e.g. "google.com")
                var t = new string(dslInput);
                return new Word(t.AsMemory(), WFlags.None, '\0', '\0', '\0', context, index, 0);
            }

            // E. Success
            string cleanText = new string(dslInput.Slice(0, dotIndex));
            return new Word(cleanText.AsMemory(), flags, punctuation, '\0', '\0', context, index, 0);
        }

        // --- 2. COMPILE (The Writer) ---
        public static string Compile(ReadOnlySpan<char> text, WFlags flags, char punctuation)
        {
            var sb = new StringBuilder();

            // A. Write Text
            if ((flags & WFlags.IsRaw) != 0)
            {
                sb.Append(text);
            }
            else if ((flags & (WFlags.UpperFirst | WFlags.UpperAll)) != 0)
            {
                // Convert to lower for storage (Reader reconstructs casing)
                sb.Append(text.ToString().ToLowerInvariant());
            }
            else
            {
                sb.Append(text);
            }

            // B. Filter Runtime Flags
            WFlags visualFlags = flags & ~(WFlags.IsTag | WFlags.IsContent);

            // C. Optimization Check: Do we need suffixes?
            if (visualFlags == WFlags.None && punctuation == '\0') return sb.ToString();

            bool hasWrittenDot = false;
            void EnsureDot() { if (!hasWrittenDot) { sb.Append('.'); hasWrittenDot = true; } }

            // D. Append Instructions (Order: Quotes -> Casing -> Punct -> Spacing -> Raw)

            // Quotes
            if ((flags & WFlags.QuoteOpen) != 0) { EnsureDot(); sb.Append("qo"); }

            // Casing
            if ((flags & WFlags.UpperAll) != 0) { EnsureDot(); sb.Append('U'); }
            else if ((flags & WFlags.UpperFirst) != 0) { EnsureDot(); sb.Append('u'); }

            // Punctuation
            if (punctuation != '\0')
            {
                EnsureDot();
                switch (punctuation)
                {
                    case '.': sb.Append('e'); break;
                    case '?': sb.Append('q'); break;
                    case '!': sb.Append('x'); break;
                    case ',': sb.Append('c'); break;
                }
            }

            // Closing Quote
            if ((flags & WFlags.QuoteClose) != 0) { EnsureDot(); sb.Append("qc"); }

            // Spacing
            if ((flags & WFlags.NoSpace) != 0) { EnsureDot(); sb.Append("ns"); }

            // Raw (.r)
            if ((flags & WFlags.IsRaw) != 0)
            {
                // OPTIMIZATION: Quote Safety Rule
                // Implicit Raw: Starts with '<' AND has NO double quotes.
                // <br> -> Implicit (Skip .r)
                // <a href="..."> -> Explicit (Keep .r)

                bool startsWithAngle = (text.Length > 0 && text[0] == '<');
                bool hasQuotes = false;

                // Fast Scan for quotes
                foreach (char c in text) { if (c == '"') { hasQuotes = true; break; } }

                bool isImplicit = startsWithAngle && !hasQuotes;

                if (!isImplicit)
                {
                    EnsureDot();
                    sb.Append('r');
                }
            }

            return sb.ToString();
        }

        // Backward Compatibility Overload
        public static string Compile(string text, WFlags flags, char punctuation)
        {
            return Compile(text.AsSpan(), flags, punctuation);
        }
    }
}