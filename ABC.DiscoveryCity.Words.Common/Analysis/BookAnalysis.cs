namespace ABC.DiscoveryCity.Words.Common.Analysis
{
    /// <summary>
    /// Container for book-level analysis results.
    /// Keyed by word/sentence ordinals - external to the core structs.
    /// </summary>
    public class BookAnalysis
    {
        /// <summary>
        /// Word-level annotations, keyed by word ordinal.
        /// Sparse storage - typically only content words are annotated.
        /// </summary>
        public Dictionary<int, WordAnalysis> WordAnnotations { get; } = new();

        /// <summary>
        /// Sentence-level stats, keyed by sentence ordinal.
        /// </summary>
        public Dictionary<int, SentenceStats> SentenceStats { get; } = new();

        // === Aggregate Statistics ===

        /// <summary>
        /// Total word count (including spaces, punctuation, tags).
        /// </summary>
        public int TotalTokens { get; set; }

        /// <summary>
        /// Content words only (excludes spaces, punctuation, tags, stop words).
        /// </summary>
        public int ContentWordCount { get; set; }

        /// <summary>
        /// Number of unique word forms.
        /// </summary>
        public int UniqueWordCount { get; set; }

        /// <summary>
        /// Words found in dictionary.
        /// </summary>
        public int DefinedWordCount { get; set; }

        /// <summary>
        /// Words not found in dictionary.
        /// </summary>
        public int UndefinedWordCount { get; set; }

        /// <summary>
        /// Total sentence count.
        /// </summary>
        public int SentenceCount { get; set; }

        /// <summary>
        /// Dictionary coverage as percentage.
        /// </summary>
        public float CoveragePercent => ContentWordCount > 0
            ? (float)DefinedWordCount / ContentWordCount * 100
            : 0;

        /// <summary>
        /// Average sentence length in content words.
        /// </summary>
        public float AverageSentenceLength => SentenceCount > 0
            ? (float)ContentWordCount / SentenceCount
            : 0;

        /// <summary>
        /// Gets or creates a WordAnalysis for the given ordinal.
        /// </summary>
        public WordAnalysis GetOrCreateWordAnalysis(int ordinal)
        {
            if (!WordAnnotations.TryGetValue(ordinal, out var analysis))
            {
                analysis = new WordAnalysis();
                WordAnnotations[ordinal] = analysis;
            }
            return analysis;
        }

        /// <summary>
        /// Gets or creates SentenceStats for the given ordinal.
        /// </summary>
        public SentenceStats GetOrCreateSentenceStats(int ordinal)
        {
            if (!SentenceStats.TryGetValue(ordinal, out var stats))
            {
                stats = new SentenceStats();
                SentenceStats[ordinal] = stats;
            }
            return stats;
        }
    }

    /// <summary>
    /// Statistics for a single sentence.
    /// </summary>
    public class SentenceStats
    {
        /// <summary>
        /// Page number where this sentence appears (0 = front matter or unknown).
        /// </summary>
        public int Page { get; set; }

        /// <summary>
        /// Chapter number (1-based). 0 = front matter or unknown.
        /// </summary>
        public int Chapter { get; set; }

        /// <summary>
        /// Chapter identifier (e.g., "ch01", "prologue", etc.)
        /// </summary>
        public string? ChapterId { get; set; }

        /// <summary>
        /// Sentence type based on ending punctuation.
        /// </summary>
        public SentenceType Type { get; set; }

        /// <summary>
        /// Number of content words in the sentence.
        /// </summary>
        public int ContentWordCount { get; set; }

        /// <summary>
        /// Total tokens including spaces, punctuation.
        /// </summary>
        public int TokenCount { get; set; }

        /// <summary>
        /// Words found in dictionary.
        /// </summary>
        public int DefinedWordCount { get; set; }

        /// <summary>
        /// Words not found in dictionary.
        /// </summary>
        public int UndefinedWordCount { get; set; }

        /// <summary>
        /// Average word length in characters.
        /// </summary>
        public float AverageWordLength { get; set; }

        /// <summary>
        /// Readability metrics (future: Flesch-Kincaid, etc.)
        /// </summary>
        public float ReadabilityScore { get; set; }
    }

    /// <summary>
    /// Classification of sentence by ending punctuation.
    /// </summary>
    public enum SentenceType : byte
    {
        Unknown = 0,
        Declarative = 1,    // Ends with .
        Question = 2,       // Ends with ?
        Exclamation = 3,    // Ends with !
        Fragment = 4        // No clear ending (e.g., heading, list item)
    }
}
