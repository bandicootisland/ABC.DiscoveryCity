using System.Collections.Immutable;

namespace ABC.DiscoveryCity.Words.Common.Analysis
{
    /// <summary>
    /// Classification of word types for analysis purposes.
    /// </summary>
    [Flags]
    public enum WordClass : byte
    {
        None = 0,
        Content = 1,        // Meaningful word (noun, verb, adj, adv)
        StopWord = 2,       // Function word (the, a, is, of, etc.)
        ProperNoun = 4,     // Capitalized name
        Number = 8,         // Numeric content
        Punctuation = 16,   // . , ! ? etc.
        Tag = 32,           // HTML/XML tag
        Space = 64          // Whitespace token
    }

    /// <summary>
    /// Extensible word-level analysis results.
    /// Stored externally, keyed by word ordinal.
    /// </summary>
    public class WordAnalysis
    {
        // === Dictionary Lookup ===

        /// <summary>
        /// Primary dictionary entry found for this word.
        /// Null if not found or not yet looked up.
        /// </summary>
        public object? DictionaryEntry { get; set; }

        /// <summary>
        /// All homonym entries if multiple exist.
        /// </summary>
        public ImmutableArray<object> Homonyms { get; set; } = ImmutableArray<object>.Empty;

        /// <summary>
        /// Whether dictionary lookup has been performed.
        /// </summary>
        public bool LookupCompleted { get; set; }

        /// <summary>
        /// Whether word was found in dictionary.
        /// </summary>
        public bool FoundInDictionary => LookupCompleted && DictionaryEntry != null;

        // === Grammar ===

        /// <summary>
        /// Part of speech from dictionary or NLP analysis.
        /// Examples: "n.", "v.", "adj.", "adv."
        /// </summary>
        public string? PartOfSpeech { get; set; }

        /// <summary>
        /// Base form of the word.
        /// Examples: "running" -> "run", "children" -> "child"
        /// </summary>
        public string? Lemma { get; set; }

        /// <summary>
        /// Verb tense if applicable.
        /// </summary>
        public string? Tense { get; set; }

        // === Semantic ===

        /// <summary>
        /// Link to WordWeb concept ID for semantic analysis.
        /// </summary>
        public int ConceptId { get; set; }

        /// <summary>
        /// Selected sense ID when word has multiple meanings.
        /// </summary>
        public int SelectedSenseId { get; set; }

        /// <summary>
        /// TF-IDF or similar weighting for this word in context.
        /// </summary>
        public float SemanticWeight { get; set; }

        // === Classification ===

        /// <summary>
        /// Word classification flags.
        /// </summary>
        public WordClass Class { get; set; }

        /// <summary>
        /// True if this is a content word (not stop word, punctuation, tag, space).
        /// </summary>
        public bool IsContentWord => Class.HasFlag(WordClass.Content);
    }
}
