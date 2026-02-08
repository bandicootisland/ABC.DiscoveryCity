using ABC.DiscoveryCity.Words.Common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;

namespace ABC.DiscoveryCity.Words.Common
{
    public static class LibraryExtensions
    {
        public static DefinitionEnumerator Query(this ImmutableArray<DictionaryEntry> entries, string prefix = "", string pos = null)
        {
            if (entries.IsDefault)
            {
                // Return an empty enumerator instead of crashing
                return new DefinitionEnumerator(ReadOnlySpan<DictionaryEntry>.Empty, ReadOnlySpan<char>.Empty, null);
            }
            return new DefinitionEnumerator(entries.AsSpan(), prefix.AsSpan(), pos);
        }
    }
    public ref struct DefinitionEnumerator
    {
        private readonly ReadOnlySpan<DictionaryEntry> _source;
        private int _index;

        // Filter Criteria
        private readonly ReadOnlySpan<char> _prefix; // e.g. "Ab"
        private readonly string? _pos;              // e.g. "noun"

        public DefinitionEnumerator(ReadOnlySpan<DictionaryEntry> source, ReadOnlySpan<char> prefix, string? pos)
        {
            _source = source;
            _prefix = prefix;
            _pos = pos;
            _index = -1;
            Current = null!;
        }

        public DictionaryEntry Current { get; private set; }

        public bool MoveNext()
        {
            while (++_index < _source.Length)
            {
                var entry = _source[_index];

                // 1. PREFIX CHECK (Zero-Alloc Span Comparison)
                if (_prefix.Length > 0)
                {
                    // Check if Headword starts with prefix
                    if (!entry.Headword.AsSpan().StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        // Optimization: Since LinearStore is sorted, if we passed the prefix alphabetically, 
                        // we could 'return false' immediately to stop the search!
                        // For now, simple scan:
                        continue;
                    }
                }

                // 2. POS CHECK (Does ANY sense match?)
                if (_pos != null)
                {
                    bool match = false;
                    // Avoid foreach/enumerator allocation on the List<Sense>
                    for (int i = 0; i < entry.Senses.Count; i++)
                    {
                        // Case-insensitive check
                        if (string.Equals(entry.Senses[i].PartOfSpeech, _pos, StringComparison.OrdinalIgnoreCase))
                        {
                            match = true;
                            break;
                        }
                    }
                    if (!match) continue;
                }

                // FOUND ONE!
                Current = entry;
                return true;
            }
            return false;
        }

        public DefinitionEnumerator GetEnumerator() => this;
    }
}
