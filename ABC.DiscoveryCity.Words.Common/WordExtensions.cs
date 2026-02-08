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
        extension(Word word)
        {
            public Word fx(FunctionalWord fxw, params object[] args) //fx helper; returns word to allow word to continue being part of sentence
            {
                var newArgs = new object[Math.Max(1, args.Length+1)];
                
                if (args.Length > 0)
                {                    
                    newArgs[0] = word;
                    Array.Copy(args, 0, newArgs, 1, args.Length);                 
                }
                else
                {
                    newArgs[0] = word;
                }

                fxw.Invoke(newArgs);
                return word;

            }

            public bool IsPunctuation
            {
                get
                {
                    // Use span for zero-allocation check
                    var span = word.span;
                    if (span.IsEmpty) return false;
                    // Check if all characters are punctuation
                    foreach (char c in span)
                    {
                        if (!char.IsPunctuation(c))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }

            public bool IsContent() // 'IsContent' implies it contains meaningful data, not just structure/whitespace
            {
                // 1. Convert to Span (Zero Allocation view of the memory)
                ReadOnlySpan<char> span = word.span;

                // 2. Fast empty check (zero allocation)
                if (span.IsEmpty) return false;

                // 3. Trim whitespace (This creates a 'Slice', NO new string allocation)
                ReadOnlySpan<char> trimmed = span.Trim();

                // 4. Check: Is it empty after trimming? (Handles " ", "\t", etc.)
                if (trimmed.Length == 0) return false;

                // 5. Check: Is it a Tag? (Starts with '<' AND Ends with '>')
                // Handles "<div>" (True) vs "1 is > 2" (False)
                // Note: We check Length >= 2 so simply "<" isn't flagged as a tag
                if (trimmed.Length >= 2 && trimmed[0] == '<' && trimmed[trimmed.Length - 1] == '>')
                {
                    return false;
                }

                // 6. Check: Single specific junk characters
                // User requested exclusion of just "{" 
                if (trimmed.Length == 1)
                {
                    char c = trimmed[0];
                    if (!c.IsEnglishLetter()) return false;
                    // Add other single-char noise here if needed (e.g., '}')
                }

                return true;
            }
            public bool IsSingleWord()
            {
                if (!word.IsContent()) return false;
                // 2. Scan for ANY whitespace (Space, Tab, Newline, Non-Breaking Space)
                // Use span directly for zero allocation.
                foreach (char c in word.span)
                {
                    if (char.IsWhiteSpace(c)) return false;
                }

                return true;
            }
            public bool IsTag()
            {
                // Check Flag OR Text heuristic
                if ((word.Flags & WFlags.IsTag) != 0) return true;

                // Fast Span check for <...>
                var span = word.Text.Span;
                return span.Length >= 3 && span[0] == '<' && span[span.Length - 1] == '>';
            }

            public Sentence ToSentence() => new Sentence(word);
            
            public Sentence _s(string text) => new Sentence(text);

            //public Word u
            //{
            //    get
            //    {
            //        string original = word.text;

            //        // 1. Safety: Handle empty strings
            //        if (string.IsNullOrEmpty(original)) return new Word(original);

            //        // 2. Optimization: If it's ALREADY capitalized, don't create a new string                    
            //        char firstChar = original[0];
            //        char upperChar = char.ToUpper(firstChar);

            //        if (firstChar == upperChar)
            //        {
            //            // Reuse the existing string reference (Zero Allocation)
            //            return new Word(original);
            //        }

            //        // 3. (Zero Temporary Allocation)
            //        // We create the new string directly with the exact length needed.
            //        string capitalized = string.Create(original.Length, (original, upperChar), (span, state) =>
            //        {
            //            // A. Copy the entire original string to the new memory span
            //            state.original.AsSpan().CopyTo(span);

            //            // B. Overwrite only the first character
            //            span[0] = state.upperChar;
            //        });

            //        return new Word(capitalized);
            //    }
            //}
            //an.adifferentwaytopayattention + aardvark.payattentiontointernallogic
            public Word adifferentwaytopayattention //lower case, to distunguish when reading (word.lowercase is never seen reading; dont adopt programming convention here)
            {
                get
                {
                    //store a tag, or a repeateable fact about the word; 'pay attention' to say the dictionary meaning to assist in the word prediction
                    //give the model and training more to pay attention to, e.g dictionary lookup for context
                    //feed that back through the neuron??
                    string original = word.text;


                    //linked array...

                    return word; //word written out properly, but the extension method has caused something to occur
                }
            }

            public Word payattentiontointernallogic
            {
                get
                {
                    //store a tag, or a repeateable fact about the word; 'pay attention' to say the dictionary meaning to assist in the word prediction
                    //feed that back through the neuron??
                    string original = word.text;


                    //linked array...

                    return word;
                }
            }
            public Word next
            {
                get
                {
                    // 1. Context-Aware Navigation (The "Sentence" view)
                    // Note: We use 'this.' to access the struct's fields
                    if (word.Data != null && word.Index >= 0 && word.Index < word.Data.Words.Length - 1)
                    {
                        return word.Data.Words[word.Index + 1];
                    }

                    
                    return default;
                }
            }

            public Word previous
            {
                get
                {
                    // 1. Context-Aware Navigation
                    if (word.Data != null && word.Index > 0)
                    {
                        return word.Data.Words[word.Index - 1];
                    }


                    return default;
                }
            }


            public bool IsNumeric()
            {
                // 1. Safety: Empty/Null is not a number
                if (string.IsNullOrEmpty(word.text)) return false;

                // 2. Scan using Span (Zero Allocation)
                foreach (char c in word.span)
                {
                    // 3. Strict ASCII Check (Fastest possible CPU instruction)
                    // If it is OUTSIDE the range '0' (48) to '9' (57), it's not a digit.
                    if (c < '0' || c > '9')
                    {
                        return false;
                    }
                }

                return true;
            }
            public bool IsDecimalNumeric()
            {
                if (string.IsNullOrEmpty(word.text)) return false;

                // Track decimal point to ensure only one exists
                bool hasDot = false;

                foreach (char c in word.span)
                {
                    if (c == '.')
                    {
                        if (hasDot) return false; // Two dots "12.3.4" is invalid
                        hasDot = true;
                        continue;
                    }

                    if (c < '0' || c > '9') return false;
                }

                // Edge case: "." is not a number
                return word.text.Length > 1 || !hasDot;
            }
            

            // 2. The Universal Enumerator
            
        }
        extension(char c)
        {
            public bool IsEnglishLetter()
            {
                return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            }
        }


        extension(HashSet<Word> words)
        {
            public Word AddWord(Word word)
            {
                var w = word with { Ordinal = words.Count + 1 };
                words.Add(w);
                return word;
            }
        }

        extension(FrozenSet<Word> words)
        {
            public FrozenSet<Word> AppendWords(HashSet<Word> hashset)
            {
                var appendedset = words.ToList();
                int count = appendedset.Count;
                foreach (var item in hashset)
                {
                    var i = item with { Ordinal = item.Ordinal + count };
                    appendedset.Add(i);
                }
                return appendedset.ToFrozenSet(WordSpanComparer.Instance);
            }
        }

        extension(IEnumerable<Word> words)
        {

            public IEnumerable<Word> Filter(Func<Word, bool> predicate)
            {
                if (words == null) yield break;

                foreach (var word in words)
                {
                    if (predicate(word))
                    {
                        yield return word;
                    }
                }
            }
        }
        
        extension(string s)
        {
            /// <summary>
            /// Helper: Determines if a tag implies a sentence/paragraph break.
            /// </summary>
            public bool IsSentenceBreakTag()
            {
                return s.AsSpan().IsSentenceBreakTag();
            }
            public Word ToWord()
            {
                return new Word(s);
            }
            public Sentence ToSentence()
            {
                return new Sentence().Append(s.ToWord());
            }
        }
        
        
    }

    

    // Ensure WordSpanComparer is available in this namespace or referenced correctly
    public sealed class WordSpanComparer : IEqualityComparer<Word>,IAlternateEqualityComparer<ReadOnlySpan<char>, Word>
    {
        public static readonly WordSpanComparer Instance = new();

        // 1. Word vs Word
        // Fix: Use .span.Equals (Memory comparison, not String reference comparison)
        public bool Equals(Word x, Word y) =>
            x.span.Equals(y.span, StringComparison.OrdinalIgnoreCase);

        // Fix: Pass the span directly to string.GetHashCode (available in .NET Core/5+)
        public int GetHashCode(Word obj) =>
            string.GetHashCode(obj.span, StringComparison.OrdinalIgnoreCase);

        // 2. Span vs Word
        // Fix: Use .span on the 'other' Word
        public bool Equals(ReadOnlySpan<char> alternate, Word other) =>
            alternate.Equals(other.span, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ReadOnlySpan<char> alternate) =>
            string.GetHashCode(alternate, StringComparison.OrdinalIgnoreCase);

        // 3. Create (Materialize)
        // This is the ONLY place that allocates (converts Span -> String -> Word)
        public Word Create(ReadOnlySpan<char> alternate) =>
            new Word(alternate.ToString());
    }
}
