using System.Runtime.InteropServices;

namespace ABC.DiscoveryCity.Words.Common
{
    /// <summary>
    /// A deferred/lazy Sentence that stores only the source text reference.
    /// Tokenization happens on-demand when AsSentence() is called.
    ///
    /// Use this for rarely-accessed text fields (quotations, etymology, etc.)
    /// to avoid upfront parsing costs during bulk loading.
    ///
    /// Memory layout: Just a ReadOnlyMemory&lt;char&gt; (16 bytes on 64-bit).
    /// </summary>
    [StructLayout(LayoutKind.Auto)]
    public readonly struct SentenceDeferred
    {
        private readonly ReadOnlyMemory<char> _source;

        /// <summary>
        /// Creates a deferred sentence from a Memory reference.
        /// </summary>
        public SentenceDeferred(ReadOnlyMemory<char> source) => _source = source;

        /// <summary>
        /// Creates a deferred sentence from a string.
        /// The string is not copied - we just hold a reference.
        /// </summary>
        public SentenceDeferred(string? source) => _source = source?.AsMemory() ?? ReadOnlyMemory<char>.Empty;

        /// <summary>
        /// Gets the text with HTML/XML tags stripped. User-friendly default.
        /// </summary>
        public string text => _source.IsEmpty ? "" : StripTags(_source.Span);

        /// <summary>
        /// Gets the raw text including any HTML/XML tags. Zero cost.
        /// </summary>
        public string RawText => _source.IsEmpty ? "" : _source.ToString();

        /// <summary>
        /// Gets the raw text as a span. Zero cost, no allocation.
        /// </summary>
        public ReadOnlySpan<char> span => _source.Span;

        /// <summary>
        /// Gets the underlying memory.
        /// </summary>
        public ReadOnlyMemory<char> Memory => _source;

        /// <summary>
        /// Returns true if empty or null.
        /// </summary>
        public bool IsEmpty => _source.IsEmpty;

        /// <summary>
        /// Gets the character length.
        /// </summary>
        public int Length => _source.Length;

        /// <summary>
        /// Parses and returns a full Sentence with Word tokenization.
        /// Call this only when you need word-level access.
        /// Note: This parses fresh each time (no caching).
        /// </summary>
        public Sentence AsSentence()
        {
            if (_source.IsEmpty) return default;

            var words = new List<Word>();
            int ordinal = 1;
            WordTextParser.Tokenize(_source.Span, words, ref ordinal, context: null);
            return new Sentence(words, takeOwnership: true);
        }

        /// <summary>
        /// Implicit conversion from string.
        /// </summary>
        public static implicit operator SentenceDeferred(string? s) => new SentenceDeferred(s);

        /// <summary>
        /// Returns the raw text.
        /// </summary>
        public override string ToString() => text;

        /// <summary>
        /// Empty instance.
        /// </summary>
        public static readonly SentenceDeferred Empty = new SentenceDeferred(ReadOnlyMemory<char>.Empty);

        /// <summary>
        /// Strips HTML/XML tags from text. Optimized single-pass scan.
        /// </summary>
        private static string StripTags(ReadOnlySpan<char> source)
        {
            if (source.IsEmpty) return "";

            // Quick check: if no < or &, return as-is
            bool hasMarkup = false;
            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == '<' || source[i] == '&')
                {
                    hasMarkup = true;
                    break;
                }
            }
            if (!hasMarkup) return source.ToString();

            // Strip tags and entities
            var result = new System.Text.StringBuilder(source.Length);
            int pos = 0;

            while (pos < source.Length)
            {
                char c = source[pos];

                if (c == '<')
                {
                    // Skip until closing >
                    while (pos < source.Length && source[pos] != '>')
                        pos++;
                    pos++; // skip >
                }
                else if (c == '&')
                {
                    // Check for HTML entity (e.g., &amp; &nbsp;)
                    int start = pos;
                    pos++;
                    while (pos < source.Length && pos - start < 10 && source[pos] != ';' && source[pos] != ' ' && source[pos] != '<')
                        pos++;

                    if (pos < source.Length && source[pos] == ';')
                    {
                        // Decode common entities
                        var entity = source.Slice(start, pos - start + 1);
                        if (entity.SequenceEqual("&amp;")) result.Append('&');
                        else if (entity.SequenceEqual("&lt;")) result.Append('<');
                        else if (entity.SequenceEqual("&gt;")) result.Append('>');
                        else if (entity.SequenceEqual("&quot;")) result.Append('"');
                        else if (entity.SequenceEqual("&apos;")) result.Append('\'');
                        else if (entity.SequenceEqual("&nbsp;")) result.Append(' ');
                        // Punctuation entities (using Unicode escapes)
                        else if (entity.SequenceEqual("&mdash;")) result.Append('\u2014');  // —
                        else if (entity.SequenceEqual("&ndash;")) result.Append('\u2013');  // –
                        else if (entity.SequenceEqual("&hellip;")) result.Append('\u2026'); // …
                        else if (entity.SequenceEqual("&lsquo;")) result.Append('\u2018');  // '
                        else if (entity.SequenceEqual("&rsquo;")) result.Append('\u2019');  // '
                        else if (entity.SequenceEqual("&ldquo;")) result.Append('\u201C');  // "
                        else if (entity.SequenceEqual("&rdquo;")) result.Append('\u201D'); // "
                        else
                        {
                            // Unknown entity - preserve as-is
                            result.Append(entity);
                        }
                        pos++; // skip ;
                    }
                    else
                    {
                        // Not a valid entity, output the &
                        result.Append('&');
                        pos = start + 1;
                    }
                }
                else
                {
                    result.Append(c);
                    pos++;
                }
            }

            return result.ToString();
        }
    }
}
