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
    public static class MarkExtensions
    {
        // 1. The Extension Method (The Entry Point)
        public static FilteredWordEnumerator AsContent(this List<Mark> marks)
        {
            return new FilteredWordEnumerator(marks);
        }

        // 2. The Enumerator (Must be ref struct for zero-allocation safety)
        public ref struct FilteredWordEnumerator
        {
            private readonly List<Mark> _marks;
            private int _index;

            public FilteredWordEnumerator(List<Mark> marks)
            {
                _marks = marks;
                _index = -1;
                Current = default;
            }

            // --- The 3 Requirements for 'foreach' Duck Typing ---

            // A. The Property
            public Mark Current { get; private set; }

            // B. The Mover
            public bool MoveNext()
            {
                while (++_index < _marks.Count)
                {
                    var m = _marks[_index];
                    // Skip Whitespace (and Punctuation if you want)
                    if (!m.IsWhitespace)
                    {
                        Current = m;
                        return true;
                    }
                }
                return false;
            }

            // C. The Missing Link (What caused your error)
            // 'foreach' asks the struct: "Give me the thing that iterates."
            // The struct replies: "I am the thing. Here is myself."
            public FilteredWordEnumerator GetEnumerator() => this;
        }
    }
}
