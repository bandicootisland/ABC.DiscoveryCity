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
        extension(Sentence sentence)
        {
            // --- Sentence Navigation ---
            public Word firstword
            {
                get
                {
                    if (sentence.words.IsEmpty) return default;
                    return sentence.words[0];
                }
                
            }

            public Word lastword
            {
                get
                {
                    if (sentence.words.IsEmpty) return default;
                    return sentence.words[sentence.words.Length - 1];
                }
            }

            
            public Sentence fx(FunctionalWord fxw, params object[] args) //fx helper; returns word to allow word to continue being part of sentence
            {
                var newArgs = new object[Math.Max(1, args.Length + 1)];

                if (args.Length > 0)
                {
                    newArgs[0] = sentence;
                    Array.Copy(args, 0, newArgs, 1, args.Length);
                }
                else
                {
                    newArgs[0] = sentence;
                }

                fxw.Invoke(newArgs);
                return (Sentence)newArgs[0];

            }


            public Sentence Append(Word newWord, bool autoSpace = true)
            {
                // 1. GET BUILDER (The List)
                List<Word> list;

                // ACCESSING INTERNAL FIELD: _manualWords
                if (sentence.IsBuilder)
                {
                    list = sentence._manualWords!;
                }
                else
                {
                    // Upgrade Logic: Copy from View to List
                    list = new List<Word>();

                    // ACCESSING INTERNAL FIELDS: _context, _count, _offset
                    if (sentence._context != null)
                    {
                        for (int i = 0; i < sentence._count; i++)
                        {
                            list.Add(sentence._context.Words[sentence._offset + i]);
                        }
                    }
                }

                // 2. AUTO-SPACING LOGIC (Zero Alloc)
                if (autoSpace && list.Count > 0)
                {
                    var lastWord = list[list.Count - 1];

                    // Optimization: Use span to check chars without string alloc
                    var newSpan = newWord.span;
                    bool needsSpace = true;

                    // Don't space before punctuation or if new word is empty/space
                    if (newSpan.Length > 0 && (newSpan[0] == ' ' || char.IsPunctuation(newSpan[0])))
                    {
                        needsSpace = false;
                    }

                    // Don't space if last word already has one
                    if (needsSpace)
                    {
                        var lastSpan = lastWord.span;
                        if (lastSpan.Length > 0 && lastSpan[lastSpan.Length - 1] == ' ')
                        {
                            needsSpace = false;
                        }
                    }

                    if (needsSpace)
                    {
                        list.Add(Word.SpaceMark);
                    }
                }

                // 3. APPEND
                list.Add(newWord);

                // 4. RETURN NEW WRAPPER
                // Using the internal constructor we exposed
                return new Sentence(list);
            }
        }
        extension(ImmutableArray<Sentence> sentences)
        {
            
            // --- Collection Navigation (The "Graph" Logic) ---

            public Sentence prev(int currentOrdinal)
            {
                if (currentOrdinal <= 0) return default;
                return sentences[currentOrdinal - 1];
            }

            public Sentence next(int currentOrdinal)
            {
                if (currentOrdinal >= sentences.Length - 1) return default;
                return sentences[currentOrdinal + 1];
            }

        }
        public static ConceptEnumerator IdentifyConcepts(this Sentence sentence)
        {
            // It is perfectly safe to pass 'sentence.words' (Span) 
            // to 'ConceptEnumerator' (Ref Struct) because both live on the stack.
            return new ConceptEnumerator(sentence.words);
        }

    }
}
