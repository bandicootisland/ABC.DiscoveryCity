using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Ontology;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common
{
    [Flags]
    public enum WFlags : byte
    {
        None = 0,
        UpperFirst = 1 << 0,
        UpperAll = 1 << 1,
        NoSpace = 1 << 2, // ns
        IsTag = 1 << 3,
        IsContent = 1 << 4,
        QuoteOpen = 1 << 5, // qo
        QuoteClose = 1 << 6, // qc
        IsRaw = 1 << 7  // r
    }


    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Word
    {
        // --- STATIC HELPERS ---
        public static readonly Word SpaceMark = new Word(" ");
        public static readonly Word None = new Word("");
        public static readonly Word FullStopMark = new Word(".");

        // --- CORE DATA ---
        // 1. Reference Data (16 bytes)
        internal readonly ReadOnlyMemory<char> Text;

        // 2. Context References (8 + 4 + 4 = 16 bytes)
        public readonly SentenceData Data;
        public int Index { get; init; }   // LOCAL: Position in SentenceData
        public int Ordinal { get; init; } // GLOBAL: "Raw Read" position

        // 3. BIT-PACKED METADATA (8 bytes)
        // [Flags: 8 bits] | [Punct: 16 bits] | [Start: 16 bits] | [End: 16 bits] | [Unused: 8 bits]
        internal readonly ulong _meta;

        // --- BITMASKS ---
        private const ulong MASK_FLAGS = 0xFF;
        private const ulong MASK_PUNCT = 0xFFFF;
        private const ulong MASK_START = 0xFFFF;
        private const ulong MASK_END = 0xFFFF;

        // --- PROPERTIES (Computed from _meta) ---
        public WFlags Flags => (WFlags)(_meta & MASK_FLAGS);
        public char Punctuation => (char)((_meta >> 8) & MASK_PUNCT);
        public char EncStart => (char)((_meta >> 24) & MASK_START);
        public char EncEnd => (char)((_meta >> 40) & MASK_END);

        // --- CONSTRUCTORS ---

        // 1. Simple Constructors (DSL/Standalone Mode)
        public Word(string text) : this(text.AsMemory()) { }
        public Word(ReadOnlyMemory<char> text)
        {
            Text = text;
            _meta = 0; // No flags, no punctuation

            
            // Create a dedicated "Backpack" for this single word.            
            Data = new SentenceData
            {
                Ordinal = -1, // Mark as DSL/Transient
                Words = Array.Empty<Word>() // Placeholder
            };
            Index = 0;
            Ordinal = -1;
            
        }

        // 2. String-based Parser Constructor
        public Word(string text, SentenceData context, int index, int ordinal)
            : this(text.AsMemory(), WFlags.None, '\0', '\0', '\0', context, index, ordinal) { }

        // 3. Memory-based Parser Constructor
        public Word(ReadOnlyMemory<char> text, SentenceData context, int index, int ordinal)
            : this(text, WFlags.None, '\0', '\0', '\0', context, index, ordinal) { }

        // 4. The Master Constructor (Bit-Packing Happens Here)
        public Word(ReadOnlyMemory<char> text, WFlags flags, char punc, char eStart, char eEnd, SentenceData? context, int index, int ordinal)
        {
            Text = text;
            Data = context;
            Index = index;
            Ordinal = ordinal;

            // Pack fields into _meta
            _meta = ((ulong)flags & MASK_FLAGS)
                  | (((ulong)punc & MASK_PUNCT) << 8)
                  | (((ulong)eStart & MASK_START) << 24)
                  | (((ulong)eEnd & MASK_END) << 40);
        }

        // 5. Internal Raw Constructor (Fast Cloning)
        internal Word(ReadOnlyMemory<char> text, ulong meta, SentenceData? context, int index, int ordinal)
        {
            Text = text;
            _meta = meta;
            Data = context;
            Index = index;
            Ordinal = ordinal;
        }

        // --- MUTATORS (Bitwise Logic) ---

        internal Word WithFlags(WFlags f)
        {
            // Clear old flags (lowest 8 bits) and set new ones
            ulong newMeta = (_meta & ~MASK_FLAGS) | ((ulong)f & MASK_FLAGS);
            return new Word(Text, newMeta, Data, Index, Ordinal);
        }

        internal Word WithPunctuation(char p)
        {
            // Clear old punctuation bits and set new ones
            ulong mask = MASK_PUNCT << 8;
            ulong newMeta = (_meta & ~mask) | (((ulong)p & MASK_PUNCT) << 8);
            return new Word(Text, newMeta, Data, Index, Ordinal);
        }

        // --- SMART START (ToSentence) ---
        public Sentence ToSentence()
        {
            // 1. Parser Mode:
            // If this word is part of a scanned sentence, return that whole sentence AS-IS.
            if (Data != null && Data.Words.Length > 0)
            {
                return new Sentence(Data);
            }

            // 2. DSL Mode (Standalone):
            // Capitalize (.u) AND Ensure Period (.e)
            return new Sentence(this.u.e);
        }

        // --- OPERATORS ---
        // Result: "WordWord"
        public static Sentence operator |(Word a, Word b)
        {
            var s = new Sentence(a);
            return s.Append(b, autoSpace: false);
        }
        public static LogicBuilder operator |(Word w, LogicOp op)
            => new LogicBuilder(w.text, op);

        public static Sentence operator +(Word a, Word b)
        {
            var s = new Sentence(a);
            return s.Append(b);
        }

        // --- BASIC PROPERTIES ---
        public string text => Text.ToString();
        public ReadOnlySpan<char> span => Text.Span;
        public int SentenceOrdinal => Data?.Ordinal ?? 0;

        // Implicit conversion
        public static implicit operator Word(string s) => new Word(s);

        public Sentence Sentence
        {
            get
            {
                // 1. If parsed context, return entire sentence.
                if (Data != null)
                {
                    return new Sentence(Data);
                }
                // 2. If standalone, create new Sentence.
                return new Sentence(this);
            }
        }

        public override string ToString()
        {
            // Fast path: No flags (check low 8 bits) and no punctuation (check next 16 bits)
            // We can check the first 24 bits of _meta at once.
            if ((_meta & 0xFFFFFF) == 0)
            {
                return Text.ToString();
            }

            // Slow path: Build string
            var sb = new StringBuilder();

            // A. Handle Text & Casing
            string raw = Text.ToString();
            WFlags f = Flags; // Unpack once

            if ((f & WFlags.UpperAll) != 0) raw = raw.ToUpperInvariant();
            else if ((f & WFlags.UpperFirst) != 0)
            {
                if (raw.Length > 0)
                    raw = char.ToUpperInvariant(raw[0]) + raw.Substring(1);
            }
            

            // B. Handle Start Enclosure (if stored in struct)
            if (EncStart != '\0') sb.Append(EncStart);

            sb.Append(raw);

            // C. Handle Punctuation
            if (Punctuation != '\0')
            {
                sb.Append(Punctuation);
            }

            // D. Handle End Enclosure            
            if (EncEnd != '\0')
            {
                sb.Append(EncEnd);
            }

            // E. Handle Spacing (Updated Logic)            
            // NEW: Always add space, UNLESS NoSpace flag is present.
            if ((Flags & WFlags.NoSpace) == 0)
            {
                sb.Append(' ');
            }

            return sb.ToString();
        }
        public void Tag(string key, string value)
        {
            // 1. Check if we have a context (Parser Word)
            if (Data == null)
            {
                // Handle DSL/Standalone words (Optional: Throw or Ignore)
                return;
            }

            // 2. Ensure the Sentence has a Tag Store
            if (Data.Tags == null)
            {
                Data.Tags = new Dictionary<int, Dictionary<string, string>>();
            }

            // 3. Get/Create the tag bag for THIS word using INDEX
            if (!Data.Tags.TryGetValue(Index, out var myTags))
            {
                myTags = new Dictionary<string, string>();
                Data.Tags[Index] = myTags;
            }

            // 4. Set the tag
            myTags[key] = value;
        }

        public string? GetTag(string key)
        {
            if (Data?.Tags != null &&
                Data.Tags.TryGetValue(Index, out var myTags))
            {
                return myTags.TryGetValue(key, out var val) ? val : null;
            }
            return null;
        }
    }
    
    
    public static partial class WordStructs
    {
        public static ImmutableArray<Word> words { get; private set; } = ImmutableArray<Word>.Empty;
        public static FrozenDictionary<string, WordStats> vocabulary { get; private set; } = FrozenDictionary<string, WordStats>.Empty;
        public static ImmutableArray<Sentence> sentences { get; private set; } = ImmutableArray<Sentence>.Empty;
        
        public static FrozenDictionary<string, WordStats>.AlternateLookup<ReadOnlySpan<char>> Lookup => vocabulary.GetAlternateLookup<ReadOnlySpan<char>>();

        // Layer storage
        public static List<Annotation> annotations { get; private set; } = new List<Annotation>();

        public static void AddAnnotation(Annotation annotation)
        {
            annotations.Add(annotation);
        }

        public static IEnumerable<Annotation> GetAnnotationsForWord(int ordinal)
        {
            return annotations.Where(a => ordinal >= a.Locator.StartOrdinal && ordinal < a.Locator.StartOrdinal + a.Locator.Length);
        }

        public readonly record struct WordStats(ImmutableArray<int> Occurrences)
        {
            public int Count => Occurrences.IsDefault ? 0 : Occurrences.Length;
            public int FirstOrdinal => Occurrences.IsDefaultOrEmpty ? 0 : Occurrences[0];
            public int LastOrdinal => Occurrences.IsDefaultOrEmpty ? 0 : Occurrences[^1];
        }
            

        public static void Load(string content)
        {
            // Clear existing data
            annotations.Clear();
            
            var words = new List<Word>();
            var sentences = new List<Sentence>();
            // Use SentenceData directly for construction
            var currentSentenceData = new SentenceData { Ordinal = 1 };
            // We can't put words in SentenceData until they are created, 
            // and we can't create Words until SentenceData exists (passed by ref).
            // But we can mutate SentenceData.Words later.
            
            var currentSentenceWords = new List<Word>();
            var vocabOrdinals = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            
            int ordinal = 1;
            int sentenceOrdinal = 1;
            var sb = new StringBuilder();
            bool collectingWhitespace = false;

            void AddWord(string text, bool isPunctuation = false)
            {
                // Create Word with reference to the shared context object
                var w = new Word(text, currentSentenceData, currentSentenceWords.Count, ordinal); 
                
                words.Add(w);
                currentSentenceWords.Add(w);

                if (!string.IsNullOrWhiteSpace(text) && !isPunctuation)
                {
                    if (!vocabOrdinals.TryGetValue(text, out var list))
                    {
                        list = new List<int>();
                        vocabOrdinals[text] = list;
                    }
                    list.Add(ordinal);
                }
                ordinal++;
            }

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                bool isPunct = char.IsPunctuation(c) || char.IsSymbol(c);
                bool isSpace = char.IsWhiteSpace(c);

                if (isPunct)
                {
                    if (sb.Length > 0)
                    {
                        AddWord(sb.ToString());
                        sb.Clear();
                    }
                    
                    collectingWhitespace = false;

                    AddWord(c.ToString(), true);

                    // Check for sentence end
                    bool isSentenceEnd = (c == '!' || c == '?');
                    if (c == '.')
                    {
                        if (i + 1 >= content.Length || char.IsWhiteSpace(content[i + 1]) || content[i + 1] == '<')
                        {
                            isSentenceEnd = true;
                        }
                    }

                    if (isSentenceEnd)
                    {
                        // Finalize the current sentence data
                        currentSentenceData.EndChar = c.ToString();
                        currentSentenceData.Words = currentSentenceWords.ToArray();
                        
                        var sentence = new Sentence(currentSentenceData);
                        sentences.Add(sentence);
                        
                        currentSentenceWords.Clear();
                        sentenceOrdinal++;
                        // Start new context
                        currentSentenceData = new SentenceData { Ordinal = sentenceOrdinal };
                    }
                }
                else if (isSpace)
                {
                    if (sb.Length > 0 && !collectingWhitespace)
                    {
                        AddWord(sb.ToString());
                        sb.Clear();
                    }
                    
                    collectingWhitespace = true;
                    sb.Append(c);
                }
                else
                {
                    if (sb.Length > 0 && collectingWhitespace)
                    {
                        AddWord(sb.ToString());
                        sb.Clear();
                    }

                    collectingWhitespace = false;
                    sb.Append(c);
                }
            }

            if (sb.Length > 0)
            {
                AddWord(sb.ToString());
            }
            
            if (currentSentenceWords.Count > 0)
            {
                 currentSentenceData.Words = currentSentenceWords.ToArray();
                 sentences.Add(new Sentence(currentSentenceData));
            }

            WordStructs.words = words.ToImmutableArray();
            vocabulary = vocabOrdinals.ToFrozenDictionary(
                kvp => kvp.Key, 
                kvp => new WordStats(kvp.Value.ToImmutableArray()), 
                StringComparer.OrdinalIgnoreCase);
            WordStructs.sentences = sentences.ToImmutableArray();
        }

        public static BookContent ParseHtml(string content)
        {
            var annotations = new List<Annotation>();
            var words = new List<Word>();
            var sentences = new List<Sentence>();

            var currentSentenceData = new SentenceData { Ordinal = 1 };
            var currentSentenceWords = new List<Word>();

            var vocabOrdinals = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            int ordinal = 1;
            int sentenceOrdinal = 1;
            int wordStart = -1;  // Track word start position instead of StringBuilder
            bool collectingWhitespace = false;

            // Pending sentence close to attach trailing spaces
            bool pendingSentenceEnd = false;
            string? pendingEndChar = null;

            // Maximum sentence length to prevent runaway sentences
            const int MaxSentenceWordLength = 1000;

            void AddWord(string text, bool isPunctuation = false, bool isTag = false)
            {
                // Tuning: "Abstract" is a title, so we ensure it's a distinct sentence.
                bool isAbstract = !isPunctuation && !isTag && text.Equals("Abstract", StringComparison.OrdinalIgnoreCase);

                if (isAbstract)
                {
                    EndSentence();
                }

                var w = new Word(text.AsMemory(), currentSentenceData, currentSentenceWords.Count, ordinal);
                words.Add(w);
                currentSentenceWords.Add(w);

                if (!string.IsNullOrWhiteSpace(text) && !isPunctuation && !isTag)
                {
                    if (!vocabOrdinals.TryGetValue(text, out var list))
                    {
                        list = new List<int>();
                        vocabOrdinals[text] = list;
                    }
                    list.Add(ordinal);
                }
                ordinal++;

                if (isAbstract)
                {
                    EndSentence();
                }
            }

            void EndSentence(string? endChar = null)
            {
                if (currentSentenceWords.Count > 0)
                {
                    if (endChar != null) currentSentenceData.EndChar = endChar;
                    currentSentenceData.Words = currentSentenceWords.ToArray();

                    var sentence = new Sentence(currentSentenceData);
                    sentences.Add(sentence);

                    currentSentenceWords.Clear();
                    sentenceOrdinal++;
                    currentSentenceData = new SentenceData { Ordinal = sentenceOrdinal };
                }
            }

            // Work with span for zero-allocation slicing
            ReadOnlySpan<char> span = content.AsSpan();
            int n = span.Length;

            for (int i = 0; i < n; i++)
            {
                char c = span[i];

                if (c == '<')
                {
                    // Flush any pending text
                    if (wordStart >= 0)
                    {
                        var wordSpan = span.Slice(wordStart, i - wordStart);
                        string text = collectingWhitespace && wordSpan.Length == 1 && wordSpan[0] == ' '
                            ? StringCache.Space
                            : StringCache.Intern(wordSpan);
                        AddWord(text, false, false);
                        wordStart = -1;
                    }
                    collectingWhitespace = false;

                    // Check pending sentence end
                    if (pendingSentenceEnd)
                    {
                        EndSentence(pendingEndChar);
                        pendingSentenceEnd = false;
                        pendingEndChar = null;
                    }

                    // Read tag using span
                    int tagStart = i;
                    while (i < n && span[i] != '>')
                    {
                        i++;
                    }
                    int length = (i < n) ? (i - tagStart + 1) : (n - tagStart);

                    // Use span for sentence break detection (zero alloc)
                    var tagSpan = span.Slice(tagStart, length);
                    bool isSentenceBreakTag = tagSpan.IsSentenceBreakTag();

                    // Intern the tag (reuses common tags)
                    string tag = StringCache.Intern(tagSpan);
                    AddWord(tag, isTag: true);

                    if (isSentenceBreakTag)
                    {
                        EndSentence();
                    }

                    continue;
                }

                bool isPunct = char.IsPunctuation(c) || char.IsSymbol(c);
                bool isSpace = char.IsWhiteSpace(c);

                if (isPunct)
                {
                    // Flush pending word
                    if (wordStart >= 0)
                    {
                        var wordSpan = span.Slice(wordStart, i - wordStart);
                        string text = collectingWhitespace && wordSpan.Length == 1 && wordSpan[0] == ' '
                            ? StringCache.Space
                            : StringCache.Intern(wordSpan);
                        AddWord(text, false, false);
                        wordStart = -1;
                    }
                    collectingWhitespace = false;

                    if (pendingSentenceEnd)
                    {
                        EndSentence(pendingEndChar);
                        pendingSentenceEnd = false;
                        pendingEndChar = null;
                    }

                    // Use cached punctuation string
                    string punctString = StringCache.GetChar(c);
                    AddWord(punctString, true);

                    // Check for sentence end
                    bool isSentenceEnd = (c == '!' || c == '?');
                    if (c == '.')
                    {
                        if (i + 1 >= n || char.IsWhiteSpace(span[i + 1]) || span[i + 1] == '<')
                        {
                            isSentenceEnd = true;
                        }
                    }

                    if (isSentenceEnd)
                    {
                        pendingSentenceEnd = true;
                        pendingEndChar = punctString;
                    }
                }
                else if (isSpace)
                {
                    if (wordStart >= 0 && !collectingWhitespace)
                    {
                        var wordSpan = span.Slice(wordStart, i - wordStart);
                        AddWord(StringCache.Intern(wordSpan), false, false);
                        wordStart = -1;
                    }

                    if (wordStart < 0)
                    {
                        wordStart = i;
                    }
                    collectingWhitespace = true;
                }
                else
                {
                    // Content char
                    if (wordStart >= 0 && collectingWhitespace)
                    {
                        var wordSpan = span.Slice(wordStart, i - wordStart);
                        string text = wordSpan.Length == 1 && wordSpan[0] == ' '
                            ? StringCache.Space
                            : StringCache.Intern(wordSpan);
                        AddWord(text, false, false);
                        wordStart = -1;
                    }

                    if (pendingSentenceEnd)
                    {
                        EndSentence(pendingEndChar);
                        pendingSentenceEnd = false;
                        pendingEndChar = null;
                    }

                    if (wordStart < 0)
                    {
                        wordStart = i;
                    }
                    collectingWhitespace = false;
                }

                // Safety check for runaway sentences
                if (currentSentenceWords.Count >= MaxSentenceWordLength)
                {
                    var lastWord = currentSentenceWords[currentSentenceWords.Count - 1];
                    var lastSpan = lastWord.span;
                    if (!lastWord.IsPunctuation && (lastSpan.IsEmpty || lastSpan[0] != '<'))
                    {
                        EndSentence();
                    }
                }
            }

            // Flush final word
            if (wordStart >= 0)
            {
                var wordSpan = span.Slice(wordStart, n - wordStart);
                string text = collectingWhitespace && wordSpan.Length == 1 && wordSpan[0] == ' '
                    ? StringCache.Space
                    : StringCache.Intern(wordSpan);
                AddWord(text, false, false);
            }

            if (pendingSentenceEnd)
            {
                EndSentence(pendingEndChar);
            }

            if (currentSentenceWords.Count > 0)
            {
                currentSentenceData.Words = currentSentenceWords.ToArray();
                sentences.Add(new Sentence(currentSentenceData));
            }

            return new BookContent
            {
                Words = words.ToImmutableArray(),
                Sentences = sentences.ToImmutableArray(),
                Vocabulary = vocabOrdinals.ToFrozenDictionary(
                    kvp => kvp.Key,
                    kvp => new WordStats(kvp.Value.ToImmutableArray()),
                    StringComparer.OrdinalIgnoreCase),
                Annotations = annotations
            };
        }

        public static void LoadHtml(string content)
        {
            var book = ParseHtml(content);
            annotations = book.Annotations;
            words = book.Words;
            sentences = book.Sentences;
            vocabulary = book.Vocabulary;
        }

        

public sealed class WordSpanComparer :
    IEqualityComparer<Word>,
    IAlternateEqualityComparer<ReadOnlySpan<char>, Word>
    {
        public static readonly WordSpanComparer Instance = new();

        // 1. Word vs Word
        // Use .span property (Zero Alloc)
        public bool Equals(Word x, Word y) =>
            x.span.Equals(y.span, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(Word obj) =>
            string.GetHashCode(obj.span, StringComparison.OrdinalIgnoreCase);

        // 2. Span vs Word (Lookup optimization)
        // Allows Dictionary.TryGetValue(span) without allocating a string key
        public bool Equals(ReadOnlySpan<char> alternate, Word other) =>
            alternate.Equals(other.span, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ReadOnlySpan<char> alternate) =>
            string.GetHashCode(alternate, StringComparison.OrdinalIgnoreCase);

        // 3. Create (Materialization)
        // Required when adding a NEW entry to the dictionary from a Span.
        // This MUST allocate because the Word needs a permanent memory owner.
        public Word Create(ReadOnlySpan<char> alternate) =>
            new Word(alternate.ToString());
    }


}
}

