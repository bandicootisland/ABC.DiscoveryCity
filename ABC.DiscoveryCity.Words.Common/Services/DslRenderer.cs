using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Domain;
using System;
using System.Collections.Generic;
using System.Text;

namespace ABC.DiscoveryCity.Services
{
    public static class DslRenderer
    {
        public static string RenderToHtml(List<string> dslTokens)
        {
            if (dslTokens == null || dslTokens.Count == 0) return string.Empty;

            // Heuristic allocation: Avg 5 chars per word + 1 space
            var sb = new StringBuilder(dslTokens.Count * 6);

            for (int i = 0; i < dslTokens.Count; i++)
            {
                // 1. PARSE
                Word w = DslProtocol.Parse(dslTokens[i].AsSpan(), null, i);
                ReadOnlySpan<char> text = w.Text.Span;

                // 2. SPACING LOGIC (MUST BE FIRST)
                // Default: Add a space
                bool needsSpace = (i > 0);

                // Exception: If the DSL explicitly says "No Space" (.ns), cancel it.
                if ((w.Flags & WFlags.NoSpace) != 0)
                {
                    needsSpace = false;
                }

                // *** THE CRITICAL LINE ***
                // This MUST happen before any 'if (IsRaw)' or 'if (IsTag)' checks.
                if (needsSpace) sb.Append(' ');

                // 3. RENDER CONTENT

                // A. Opening Quote
                if ((w.Flags & WFlags.QuoteOpen) != 0) sb.Append('"');

                // B. The Word Body
                if ((w.Flags & WFlags.IsRaw) != 0)
                {
                    // Print Tags/Raw exactly as is
                    sb.Append(text);
                }
                else
                {
                    // Print Text with Casing
                    if ((w.Flags & WFlags.UpperAll) != 0)
                        sb.Append(text.ToString().ToUpperInvariant());
                    else if ((w.Flags & WFlags.UpperFirst) != 0 && text.Length > 0)
                    {
                        sb.Append(char.ToUpperInvariant(text[0]));
                        if (text.Length > 1) sb.Append(text.Slice(1));
                    }
                    else
                        sb.Append(text);
                }

                // C. Punctuation
                if (w.Punctuation != '\0') sb.Append(w.Punctuation);

                // D. Closing Quote
                if ((w.Flags & WFlags.QuoteClose) != 0) sb.Append('"');
            }

            return sb.ToString();
        }
    }
}