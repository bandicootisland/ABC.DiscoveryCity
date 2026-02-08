using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Structure;
using System;

namespace ABC.DiscoveryCity.Words.Common.Structure
{
    public static class WordWebExtensions
    {
        extension(Word word)
        {
            public Concept ToConcept()
            {
                // Zero allocation lookup using Span
                return WordWeb.Lookup(word.span);
            }
        }
        extension(Sentence sentence)
        {
            public ConceptEnumerator GetConcepts()
            {
                return new ConceptEnumerator(sentence.words);
            }


            public IEnumerable<Concept> GetLinks()
            {
                // 1. Get length once. 
                // (s.Length is fast, but accessing it repeatedly might create ephemeral spans depending on JIT)
                int count = sentence.Length;

                for (int i = 0; i < count; i++)
                {
                    // 2. Access by index. 
                    // This is safe because 'Word' is a regular struct, not a ref struct.
                    Word w = sentence[i];

                    // 3. Perform Lookup.
                    // 'w.span' creates a temporary stack-only span. 
                    // We use it immediately and don't hold it across the 'yield', so this is valid.
                    var c = WordWeb.Lookup(w.span);

                    if (c.IsValid) yield return c;
                }
            }
        }
        extension(ReadOnlySpan<Word> words)
        {
            public ConceptEnumerator GetConcepts()
            {
                return new ConceptEnumerator(words);
            }
        }
        extension(string text)
        {
            public Concept ToConcept()
            {
                return WordWeb.Lookup(text.AsSpan());
            }
        }
        
        


        public ref struct ConceptEnumerator
        {
            private readonly ReadOnlySpan<Word> _words;
            private int _index;
            private Concept _current;

            public ConceptEnumerator(ReadOnlySpan<Word> words)
            {
                _words = words;
                _index = -1;
                _current = default;
            }

            // 1. Current Item
            public Concept Current => _current;

            // 2. The Engine
            public bool MoveNext()
            {
                // Scan forward from the current position
                while (++_index < _words.Length)
                {
                    // Zero-Alloc access to the span
                    var span = _words[_index].span;

                    // Perform the lookup
                    var c = WordWeb.Lookup(span);

                    // If valid, pause here and return true
                    if (c.IsValid)
                    {
                        _current = c;
                        return true;
                    }
                }

                // End of list
                return false;
            }

            // 3. Duck-Typing for 'foreach'
            public ConceptEnumerator GetEnumerator() => this;
        }
    }
}