using System;
using System.Collections.Generic;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common.Grammar
{
    

    
        public static class GrammarRules
        {
            public static bool IsAbbreviation(ReadOnlySpan<char> text)
            {
                // Fast exit for lengths that can't be abbreviations
                if (text.Length < 2 || text.Length > 4) return false;

                // Switch on length for speed
                switch (text.Length)
                {
                    case 2:
                        char c0 = char.ToLowerInvariant(text[0]);
                        char c1 = char.ToLowerInvariant(text[1]);

                        // Mr, Ms, Dr, St, Jr, Sr, Vs, No, Co, Ph
                        if (c0 == 'm' && (c1 == 'r' || c1 == 's')) return true;
                        if (c0 == 'd' && c1 == 'r') return true;
                        if (c0 == 's' && (c1 == 'r' || c1 == 't')) return true;
                        if (c0 == 'j' && c1 == 'r') return true;
                        if (c0 == 'v' && c1 == 's') return true;
                        if (c0 == 'n' && c1 == 'o') return true;
                        if (c0 == 'c' && c1 == 'o') return true;
                        if (c0 == 'p' && (c1 == 'h' || c1 == 'p')) return true; // Ph. (Phil), Pp. (Pages)
                        break;

                    case 3:
                        c0 = char.ToLowerInvariant(text[0]);
                        c1 = char.ToLowerInvariant(text[1]);
                        char c2 = char.ToLowerInvariant(text[2]);

                        // Mrs, Fig, Vol, Inc, Ltd, Rev, Gov, Col, Gen, Rep, Sen
                        if (c0 == 'm' && c1 == 'r' && c2 == 's') return true;
                        if (c0 == 'f' && c1 == 'i' && c2 == 'g') return true;
                        if (c0 == 'v' && c1 == 'o' && c2 == 'l') return true;
                        if (c0 == 'i' && c1 == 'n' && c2 == 'c') return true;
                        if (c0 == 'l' && c1 == 't' && c2 == 'd') return true;
                        if (c0 == 'r' && c1 == 'e' && c2 == 'v') return true;
                        if (c0 == 'g' && c1 == 'o' && c2 == 'v') return true;
                        if (c0 == 'c' && c1 == 'o' && c2 == 'l') return true;
                        if (c0 == 'e' && c1 == 's' && c2 == 't') return true; // Est.
                        break;

                    case 4:
                        c0 = char.ToLowerInvariant(text[0]);
                        c1 = char.ToLowerInvariant(text[1]);
                        c2 = char.ToLowerInvariant(text[2]);
                        char c3 = char.ToLowerInvariant(text[3]);

                        // Prof, Corp, Univ, Dept
                        if (c0 == 'p' && c1 == 'r' && c2 == 'o' && c3 == 'f') return true;
                        if (c0 == 'c' && c1 == 'o' && c2 == 'r' && c3 == 'p') return true;
                        if (c0 == 'u' && c1 == 'n' && c2 == 'i' && c3 == 'v') return true;
                        if (c0 == 'd' && c1 == 'e' && c2 == 'p' && c3 == 't') return true;
                        if (c0 == 'b' && c1 == 'l' && c2 == 'v' && c3 == 'd') return true; // Blvd
                        break;
                }

                return false;
            }
        }
    }

