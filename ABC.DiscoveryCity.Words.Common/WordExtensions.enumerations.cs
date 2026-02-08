using ABC.DiscoveryCity.Words.Common.Grammar;
using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices;
using System.Text;
using static System.Net.Mime.MediaTypeNames;
using static System.Object;

namespace ABC.DiscoveryCity.Words.Common
{
    public static partial class WordExtensions
    {
        extension(ImmutableArray<Word> words)
        {
            public IEnumerable<Word> Range(int start, int end)
            {
                if (words.IsDefaultOrEmpty) yield break;
                if (start < 0) start = 0;
                if (end > words.Length) end = words.Length;//clamp

                for (int i = start; i < end; i++)
                {
                    yield return words[i];
                }
            }
            public IEnumerable<Word> Filter(Func<Word, bool> predicate)
            {
                if (words.IsDefaultOrEmpty) yield break;

                for (int i = 0; i < words.Length; i++)
                {
                    if (predicate(words[i]))
                    {
                        yield return words[i];
                    }
                }
            }
            public IEnumerable<Word> Filter(int start, int end, Func<Word, bool> predicate)
            {
                if (words.IsDefaultOrEmpty) yield break;
                if (start < 0) start = 0;
                if (end > words.Length) end = words.Length;//clamp

                for (int i = start; i < end; i++)
                {
                    if (predicate(words[i]))
                    {
                        yield return words[i];
                    }
                }
            }

            public void ScanWords(int start, int end, Action<Word> action)
            {
                // 1. Safety: Handle null/default arrays immediately
                if (words.IsDefaultOrEmpty) return;

                // 2. Implicit Bounds Checking (Clamping)
                // If start is negative, treat it as 0
                if (start < 0) start = 0;

                // If end is beyond the array, clamp it to the actual length
                if (end > words.Length) end = words.Length;

                // 3. The Loop

                for (int i = start; i < end; i++)
                {
                    action(words[i]);
                }
            }
        
        


        //enumerations
        // 1. Extension Methods (The Factory)

        // Filter for "Real Words"
        public FilteredWordEnumerator AsContent()
                => new FilteredWordEnumerator(words.AsSpan(), WFlags.IsContent);

            // Filter for "Tags"
            public FilteredWordEnumerator AsTags()
                => new FilteredWordEnumerator(words.AsSpan(), WFlags.IsTag);

            // Filter for "Punctuation"
            // (Assuming you add IsPunctuation to your WFlags enum)
            // public static FilteredWordEnumerator AsPunctuation(this ImmutableArray<Word> words)
            //    => new FilteredWordEnumerator(words.AsSpan(), WFlags.IsPunctuation);

            // GENERIC: Filter by anything
            public FilteredWordEnumerator Filter(WFlags mask)
                => new FilteredWordEnumerator(words.AsSpan(), mask);

        }
        extension<T>(IEnumerable<T> source)
        {
            // Injects the 'separator' between every item in the stream
            public IEnumerable<T> Intersperse(T separator)
            {
                bool isFirst = true;
                foreach (var item in source)
                {
                    if (!isFirst) yield return separator;
                    yield return item;
                    isFirst = false;
                }
            }
        }
            // 2. The Universal Enumerator
         
        public ref struct FilteredWordEnumerator
        {
            private readonly ReadOnlySpan<Word> _source;
            private readonly WFlags _mask; // What we are looking for
            private int _index;
            private Word _current;

            public FilteredWordEnumerator(ReadOnlySpan<Word> source, WFlags mask)
            {
                _source = source;
                _mask = mask;
                _index = -1;
                _current = default;
            }
            public FilteredWordEnumerator GetEnumerator() => this;
            public Word Current => _current;

            public bool MoveNext()
            {
                int i = _index + 1;
                int len = _source.Length;

                while (i < len)
                {
                    // Optimization: Copy struct to local stack to avoid multiple array bounds checks
                    var w = _source[i];

                    // THE CHECK: Does the word have the flag we want?
                    if ((w.Flags & _mask) != 0)
                    {
                        _current = w;
                        _index = i;
                        return true;
                    }
                    i++;
                }

                _index = len;
                return false;
            }
        }
    }
        
}
