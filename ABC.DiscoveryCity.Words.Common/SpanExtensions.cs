using ABC.DiscoveryCity.Words.Common.Grammar;
using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices;
using System.Text;
using static ABC.DiscoveryCity.Words.Common.Structure.WordWebExtensions;
using static System.Net.Mime.MediaTypeNames;
using static System.Object;

namespace ABC.DiscoveryCity.Words.Common
{
    public static partial class WordExtensions
    {

        extension(ReadOnlySpan<char> span)
        {
            // 1. Check for " " (Fastest possible check)
            public bool IsSingleSpace()
            {
                return span.Length == 1 && span[0] == ' ';
            }

            // 2. Check for "<...>"
            public bool IsTagLike()
            {
                // Must be at least 2 chars "<>"
                return span.Length >= 2 && span[0] == '<' && span[span.Length - 1] == '>';
            }

            // 3. Replacement for string.IsNullOrWhiteSpace
            public bool IsAllWhitespace()
            {
                if (span.IsEmpty) return true;
                foreach (char c in span)
                {
                    if (!char.IsWhiteSpace(c)) return false;
                }
                return true;
            }

            // 4. Sequence Equal (Safe "==" replacement)
            // Usage: w.Text.Span.IsEqualTo("someString")
            public bool IsEqualTo(string other)
            {
                return span.Equals(other.AsSpan(), StringComparison.Ordinal);
            }
            /// <summary>
            /// Helper: Determines if a tag span implies a sentence/paragraph break.
            /// Zero-allocation version for use during parsing.
            /// </summary>
            public bool IsSentenceBreakTag()
            {
                // Optimization: Check length first to fail fast
                if (span.Length < 4) return false;

                // Check common block tags using span comparison
                // Note: Using manual char checks for case-insensitivity without allocation
                char c0 = span[0];
                if (c0 != '<') return false;

                char c1 = char.ToLowerInvariant(span[1]);

                // <br...
                if (c1 == 'b' && span.Length >= 4)
                {
                    char c2 = char.ToLowerInvariant(span[2]);
                    if (c2 == 'r') return true;
                }

                // Closing tags </...
                if (c1 == '/')
                {
                    if (span.Length < 4) return false;
                    char c2 = char.ToLowerInvariant(span[2]);

                    // </p, </h (h1-h6)
                    if (c2 == 'p' || c2 == 'h') return true;

                    // </div, </li, </tr, </bl (blockquote), </art (article), </sec (section)
                    if (span.Length >= 5)
                    {
                        char c3 = char.ToLowerInvariant(span[3]);
                        if (c2 == 'd' && c3 == 'i') return true;  // </div
                        if (c2 == 'l' && c3 == 'i') return true;  // </li
                        if (c2 == 't' && c3 == 'r') return true;  // </tr
                        if (c2 == 'b' && c3 == 'l') return true;  // </bl (blockquote)
                        if (c2 == 'a' && c3 == 'r') return true;  // </art (article)
                        if (c2 == 's' && c3 == 'e') return true;  // </sec (section)
                    }
                }

                return false;
            }

            public bool IsNumeric()
            {
                if (span.IsEmpty) return false;

                foreach (char c in span)
                {
                    // Strict ASCII check (0-9 only)
                    if (c < '0' || c > '9') return false;
                }
                return true;
            }
            
            public bool Contains( char c)
            {
                return span.IndexOf(c) >= 0;
            }
        }
    }
        
}
