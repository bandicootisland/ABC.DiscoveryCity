using ABC.DiscoveryCity.Words.Common;
using System.Text;
using ABC.DiscoveryCity.Words.Common.Ontology;
using ABC.WordCity.Words.Common.Layers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
namespace ABC.DiscoveryCity.Words.Common
{
    //A Word is a sequence of characters. including letters, punctuation, and spaces, and "" (empty) is a valid word.p
    //A sentence is a sequence of 1 or more Words.
    //Both are structs for zero-allocation slicing, with the main element being:
    // Word data: ReadOnlyMemory<char> Text
    // Sentence.words: ReadOnlySpan<Word>
    //Readonly struct are almost zero allocation, but restrict how they can be passed; SentenceData exposes some items as class properties for this property

    //All of the other methods on Word and Sentence are designed to be memory efficient and the fastest way to load significant data, e.g from dictionaries.

    //Externally (from this class) extension methods are used so that this structure is relatively simple to use


    public class SentenceData //class (not struct) to allow reference semantics
    {
        public int Ordinal { get; set; }
        public Word[] Words { get; set; } = Array.Empty<Word>();

        public string EndChar { get; set; } = "";
        // Sparse Storage for Tags
        // Key: Word.Index (0 to N)
        // Value: The tags for that specific word
        //TODO structure, not string string
        public Dictionary<int, Dictionary<string, string>>? Tags { get; set; }
                
        public WordLayers Layers { get; set; } = WordLayers.Empty;
    }


    public readonly struct Sentence
    {
        // --- HYBRID STORAGE ---

        // Mode A: Parser View (Fast, Zero Alloc)
        public readonly SentenceData? _context;
        internal readonly int _offset;
        internal readonly int _count;

        // Mode B: DSL Builder (Flexible, Heap Alloc)
        internal readonly List<Word>? _manualWords; //word1 + word2 + ...

        // --- CONSTRUCTORS ---

        // 1. Parser Constructor (View Mode)
        public Sentence(SentenceData context, int offset = 0, int count = -1)
        {
            _context = context;
            _offset = offset;
            _count = count == -1 ? context.Words.Length : count;
            _manualWords = null;
        }

        // 2. DSL Constructor (Builder Mode)
        public Sentence(Word initial)
        {
            _context = null;
            _offset = 0;
            _count = 0;
            _manualWords = new List<Word> { initial };
        }
        // Place inside Sentence struct

        // Public Constructor for importing words (e.g. from Parser buffers)
        public Sentence(IEnumerable<Word> words)
        {
            _context = null;
            _offset = 0;
            _count = 0;

            // OPTIMIZATION:
            // If 'words' is already a List, we can optionally wrap it directly (Zero Alloc).
            // HOWEVER: If 'defWords' is a shared buffer you Clear() later, this is dangerous.
            // SAFE DEFAULT: Create a new List (One Allocation, perfect for storage).

            if (words is List<Word> list)
            {
                // Safe Copy (Preserves the words even if original list is cleared)
                _manualWords = new List<Word>(list);
            }
            else
            {
                // Copy from Array/Enumerable
                _manualWords = new List<Word>(words);
            }
        }
        
        // 'words': The source list
        // 'takeOwnership': 
        //      TRUE  = Zero Allocation (We steal the list reference. DO NOT reuse 'words' after this).
        //      FALSE = Safety Copy (We allocate a new list and copy items. You can reuse 'words').
        public Sentence(List<Word> words, bool takeOwnership)
        {
            _context = null;
            _offset = 0;
            _count = 0;

            if (takeOwnership)
            {
                // 1. Zero Allocation Mode
                // We assume 'words' is now ours. The caller promises not to modify it.
                _manualWords = words;
            }
            else
            {
                // 2. Safety Copy Mode
                // Create a new independent list with the exact size needed.
                _manualWords = new List<Word>(words.Count);

                // Fast copy of references
                _manualWords.AddRange(words);
            }
        }

        // a way to create a new Sentence from a list        
        internal Sentence(List<Word> list)
        {
            _context = null; _offset = 0; _count = 0;
            _manualWords = list;
        }

        public ReadOnlySpan<Word> words
        {
            get
            {
                if (IsBuilder)
                {
                    // Efficiently get Span from List<T> without copying
                    return CollectionsMarshal.AsSpan(_manualWords);
                }
                else if (_context != null)
                {
                    // Return slice of the master array
                    return new ReadOnlySpan<Word>(_context.Words, _offset, _count);
                }

                return ReadOnlySpan<Word>.Empty;
            }
        }

        // 2. Convenience Indexer (Optional but recommended)
        // Allows: sentence[0] instead of sentence.words[0]
        public Word this[int i] => words[i];

        // 3. Convenience Length (Optional)
        // Allows: sentence.Length instead of sentence.words.Length
        public int Length => words.Length;

        // 4. Convenience IsEmpty
        public bool IsEmpty => Length == 0;

        public bool IsBuilder => _manualWords != null;

        public string text => ToString();
        // The "Append" logic handles the transition
        // In Sentence.cs (or SentenceExtensions.cs)

        public Sentence Append(Word newWord, bool autoSpace = true)
        {
            // ---------------------------------------------------------
            // 1. GET BUILDER (Transition Logic)
            // ---------------------------------------------------------
            List<Word> list;

            if (IsBuilder)
            {
                // Optimization: We are already a mutable list. Reuse it.
                list = _manualWords!;
            }
            else
            {
                // Upgrade: We are currently a Read-Only Parser View.
                // We must copy the existing words into a new List to modify them.
                list = new List<Word>();

                if (_context != null)
                {
                    // Efficiently copy the slice from the master array
                    for (int i = 0; i < _count; i++)
                    {
                        list.Add(_context.Words[_offset + i]);
                    }
                }
            }

            // ---------------------------------------------------------
            // 2. AUTO-SPACING LOGIC
            // ---------------------------------------------------------
            if (autoSpace && list.Count > 0)
            {
                var lastWord = list[list.Count - 1];

                // Use Spans for Zero-Allocation checks
                var newSpan = newWord.span;
                var lastSpan = lastWord.span;

                bool needsSpace = true;

                // RULE A: CHECK THE NEW WORD
                // Don't add space if the new word is Punctuation (.,!) or starts with a Space
                // Note: We check the text span. If you attached punctuation via .e/.c, 
                // that's internal to the word, so we usually still want a space *before* the word itself.
                if (newSpan.Length > 0 && (newSpan[0] == ' ' || char.IsPunctuation(newSpan[0])))
                {
                    needsSpace = false;
                }

                // RULE B: CHECK THE PREVIOUS WORD
                if (needsSpace)
                {
                    // 1. Does the text itself end in a space? ("Hello ")
                    if (lastSpan.Length > 0 && lastSpan[lastSpan.Length - 1] == ' ')
                    {
                        needsSpace = false;
                    }
                    // 2. NEW: Did the last word explicitly forbid a space? (.ns)
                    // (e.g. "Anti-" or a word inside a quote)
                    else if ((lastWord.Flags & WFlags.NoSpace) != 0)
                    {
                        needsSpace = false;
                    }
                }

                // INJECT SPACE
                if (needsSpace)
                {
                    list.Add(Word.SpaceMark);
                }
            }

            // ---------------------------------------------------------
            // 3. APPEND & RETURN
            // ---------------------------------------------------------
            list.Add(newWord);

            // Return a new struct wrapper pointing to this list
            return new Sentence(list);
        }

        // --- OPERATORS ---

        public static Sentence operator +(Sentence s, Word w) => s.Append(w);
        //RAW APPEND ( Sentence | Word )
        public static Sentence operator |(Sentence s, Word w)=> s.Append(w, autoSpace: false);         // Use the Append method but disable auto-spacing

        // ---------------------------------------------------------
        // 1. SMART JOIN ( + )
        // Logic: Ensure End Punctuation -> Ensure Space -> Capitalize Next
        // ---------------------------------------------------------
        public static Sentence operator +(Sentence s1, Sentence s2)
        {
            // A. Prepare Builder
            List<Word> list;
            if (s1.IsBuilder) list = s1._manualWords!;
            else
            {
                list = new List<Word>();
                if (s1._context != null)
                {
                    for (int k = 0; k < s1._count; k++) list.Add(s1._context.Words[s1._offset + k]);
                }
            }

            // B. Get s2 words
            var s2Words = s2.words;
            if (s2Words.IsEmpty) return new Sentence(list);

            // C. GRAMMAR STITCHING
            if (list.Count > 0)
            {
                var lastWord = list[list.Count - 1];
                var lastSpan = lastWord.span;

                // --- THE FIX ---
                // Determine if the last word effectively ends with punctuation.
                bool hasPunctuation = false;

                // 1. Check the Text (e.g. "word.")
                if (lastSpan.Length > 0 && char.IsPunctuation(lastSpan[lastSpan.Length - 1]))
                {
                    hasPunctuation = true;
                }

                // 2. Check the Mini-Grammar field (e.g. word.e -> Punctuation = '.')
                if (lastWord.Punctuation != '\0')
                {
                    hasPunctuation = true;
                }

                // Only add a full stop if missing
                if (!hasPunctuation)
                {
                    list.Add(Word.FullStopMark);
                }

                // Always ensure a space between sentences
                list.Add(Word.SpaceMark);
            }

            // D. Append s2 (Capitalizing first word)
            for (int i = 0; i < s2Words.Length; i++)
            {
                var w = s2Words[i];
                if (i == 0) w = w.u;
                list.Add(w);
            }

            return new Sentence(list);
        }

        // ---------------------------------------------------------
        // 2. RAW JOIN ( | )
        // Logic: Glue them together. No spaces, no checks.
        // ---------------------------------------------------------
        public static Sentence operator |(Sentence s1, Sentence s2)
        {
            // A. Upgrade s1
            List<Word> list;
            if (s1.IsBuilder) list = s1._manualWords!;
            else
            {
                list = new List<Word>();
                if (s1._context != null)
                {
                    for (int k = 0; k < s1._count; k++) list.Add(s1._context.Words[s1._offset + k]);
                }
            }

            // B. Bulk Add s2 (As-is)
            var s2Words = s2.words;
            foreach (var w in s2Words)
            {
                list.Add(w);
            }

            return new Sentence(list);
        }
        // --- LOGIC BUILDER OPERATORS ---
        public static LogicBuilder operator |(Sentence s, LogicOp op)
            => new LogicBuilder(s.text, op);

        // --- OUTPUT ---

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();

            if (IsBuilder)
            {
                foreach (var w in _manualWords!) sb.Append(w.ToString());
            }
            else if (_context != null)
            {
                for (int i = 0; i < _count; i++)
                    sb.Append(_context.Words[_offset + i].ToString());
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// A stack-only view over a span of words, for iteration scenarios
    /// where full Sentence allocation is not needed.
    /// Cannot be stored in fields or returned from methods.
    /// </summary>
    public readonly ref struct SentenceView
    {
        private readonly ReadOnlySpan<Word> _words;

        public SentenceView(ReadOnlySpan<Word> words) => _words = words;

        /// <summary>
        /// Creates a SentenceView from a Word array slice.
        /// </summary>
        public SentenceView(Word[] words, int offset, int count)
            => _words = new ReadOnlySpan<Word>(words, offset, count);

        /// <summary>
        /// Creates a SentenceView from an entire Word array.
        /// </summary>
        public SentenceView(Word[] words)
            => _words = words;

        public ReadOnlySpan<Word> words => _words;
        public Word this[int i] => _words[i];
        public int Length => _words.Length;
        public bool IsEmpty => _words.IsEmpty;

        /// <summary>
        /// Renders the view to a string.
        /// </summary>
        public override string ToString()
        {
            if (_words.IsEmpty) return string.Empty;

            var sb = new StringBuilder();
            foreach (var w in _words)
            {
                sb.Append(w.ToString());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Converts this view to a full Sentence (allocates).
        /// </summary>
        public Sentence ToSentence()
        {
            if (_words.IsEmpty) return default;

            var list = new List<Word>(_words.Length);
            foreach (var w in _words)
            {
                list.Add(w);
            }
            return new Sentence(list, takeOwnership: true);
        }

        public Enumerator GetEnumerator() => new Enumerator(_words);

        public ref struct Enumerator
        {
            private ReadOnlySpan<Word> _span;
            private int _index;

            internal Enumerator(ReadOnlySpan<Word> span)
            {
                _span = span;
                _index = -1;
            }

            public bool MoveNext() => ++_index < _span.Length;
            public readonly Word Current => _span[_index];
        }
    }
}
    

