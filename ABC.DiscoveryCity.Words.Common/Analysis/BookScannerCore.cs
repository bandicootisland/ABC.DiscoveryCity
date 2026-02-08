using System.Collections.Frozen;
using System.Collections.Immutable;

namespace ABC.DiscoveryCity.Words.Common.Analysis
{
    /// <summary>
    /// Result of a dictionary lookup for a word.
    /// Generic to support different dictionary types.
    /// </summary>
    public readonly struct DictionaryLookupResult
    {
        public readonly object? Entry;
        public readonly ImmutableArray<object> Homonyms;
        public readonly string? PartOfSpeech;
        public readonly bool Found;

        public DictionaryLookupResult(object? entry, string? partOfSpeech = null)
        {
            Entry = entry;
            PartOfSpeech = partOfSpeech;
            Homonyms = ImmutableArray<object>.Empty;
            Found = entry != null;
        }

        public DictionaryLookupResult(object? entry, ImmutableArray<object> homonyms, string? partOfSpeech = null)
        {
            Entry = entry;
            Homonyms = homonyms;
            PartOfSpeech = partOfSpeech;
            Found = entry != null;
        }

        public static readonly DictionaryLookupResult NotFound = new(null);
    }

    /// <summary>
    /// Delegate for dictionary lookup. Allows different dictionaries to be plugged in.
    /// </summary>
    /// <param name="word">The word span to look up (zero-allocation).</param>
    /// <returns>Lookup result with entry and metadata.</returns>
    public delegate DictionaryLookupResult DictionaryLookupDelegate(ReadOnlySpan<char> word);

    /// <summary>
    /// Callback fired when a sentence is fully parsed and analyzed.
    /// </summary>
    /// <param name="sentence">The completed sentence.</param>
    /// <param name="stats">Statistics for this sentence.</param>
    /// <param name="sentenceIndex">Zero-based index of this sentence.</param>
    public delegate void SentenceReadyCallback(Sentence sentence, SentenceStats stats, int sentenceIndex);

    /// <summary>
    /// Core book scanning engine. Parses HTML, detects sentences,
    /// performs dictionary lookups, and streams results via callback.
    /// </summary>
    public class BookScannerCore
    {
        private readonly DictionaryLookupDelegate? _lookup;
        private readonly HashSet<string> _stopWords;

        // Common English stop words (can be extended)
        private static readonly string[] DefaultStopWords = new[]
        {
            "a", "an", "the", "and", "or", "but", "if", "then", "else",
            "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did",
            "to", "of", "in", "for", "on", "with", "at", "by", "from",
            "it", "its", "this", "that", "these", "those",
            "i", "you", "he", "she", "we", "they", "me", "him", "her", "us", "them",
            "my", "your", "his", "our", "their",
            "what", "which", "who", "whom", "when", "where", "why", "how",
            "all", "each", "every", "both", "few", "more", "most", "some", "any",
            "no", "not", "only", "own", "same", "so", "than", "too", "very",
            "as", "just", "also", "now", "here", "there"
        };

        public BookScannerCore(DictionaryLookupDelegate? dictionaryLookup = null, IEnumerable<string>? stopWords = null)
        {
            _lookup = dictionaryLookup;
            _stopWords = new HashSet<string>(
                stopWords ?? DefaultStopWords,
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Scans HTML content, parsing into sentences and performing dictionary lookups.
        /// Streams sentences via callback as they complete.
        /// </summary>
        /// <param name="html">The HTML content to scan.</param>
        /// <param name="onSentenceReady">Optional callback fired per sentence.</param>
        /// <returns>BookContent and BookAnalysis results.</returns>
        public (BookContent Content, BookAnalysis Analysis) Scan(
            string html,
            SentenceReadyCallback? onSentenceReady = null)
        {
            var analysis = new BookAnalysis();
            var words = new List<Word>();
            var sentences = new List<Sentence>();
            var vocabOrdinals = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            var currentSentenceData = new SentenceData { Ordinal = 1 };
            var currentSentenceWords = new List<Word>();

            int ordinal = 1;
            int sentenceOrdinal = 1;
            int sentenceIndex = 0;
            int currentPage = 0;  // 0 = front matter or unknown
            int currentChapter = 0;  // 0 = front matter or unknown
            string? currentChapterId = null;

            // Current sentence stats
            var currentStats = new SentenceStats();

            void ClassifyAndLookup(Word word, string text)
            {
                var wordAnalysis = analysis.GetOrCreateWordAnalysis(word.Ordinal);

                // Classify the word
                if (word.Flags.HasFlag(WFlags.IsTag))
                {
                    wordAnalysis.Class = WordClass.Tag;
                }
                else if (string.IsNullOrWhiteSpace(text))
                {
                    wordAnalysis.Class = WordClass.Space;
                }
                else if (text.Length == 1 && (char.IsPunctuation(text[0]) || char.IsSymbol(text[0])))
                {
                    wordAnalysis.Class = WordClass.Punctuation;
                }
                else if (_stopWords.Contains(text))
                {
                    wordAnalysis.Class = WordClass.StopWord;
                }
                else if (char.IsDigit(text[0]))
                {
                    wordAnalysis.Class = WordClass.Number;
                }
                else if (char.IsUpper(text[0]) && currentSentenceWords.Count > 0)
                {
                    // Proper noun if capitalized and not sentence start
                    wordAnalysis.Class = WordClass.Content | WordClass.ProperNoun;
                }
                else
                {
                    wordAnalysis.Class = WordClass.Content;
                }

                // Dictionary lookup for content words
                if (wordAnalysis.Class.HasFlag(WordClass.Content) && _lookup != null)
                {
                    var result = _lookup(word.span);
                    wordAnalysis.DictionaryEntry = result.Entry;
                    wordAnalysis.Homonyms = result.Homonyms;
                    wordAnalysis.PartOfSpeech = result.PartOfSpeech;
                    wordAnalysis.LookupCompleted = true;

                    if (result.Found)
                    {
                        currentStats.DefinedWordCount++;
                    }
                    else
                    {
                        currentStats.UndefinedWordCount++;
                    }
                }

                // Update sentence stats
                if (wordAnalysis.Class.HasFlag(WordClass.Content))
                {
                    currentStats.ContentWordCount++;
                }
                currentStats.TokenCount++;
            }

            void AddWord(string text, bool isTag = false)
            {
                var flags = isTag ? WFlags.IsTag : WFlags.None;
                var w = new Word(text.AsMemory(), flags, '\0', '\0', '\0',
                    currentSentenceData, currentSentenceWords.Count, ordinal);

                words.Add(w);
                currentSentenceWords.Add(w);

                // Track vocabulary (content words only)
                if (!string.IsNullOrWhiteSpace(text) && !isTag && text.Length > 1)
                {
                    if (!vocabOrdinals.TryGetValue(text, out var list))
                    {
                        list = new List<int>();
                        vocabOrdinals[text] = list;
                    }
                    list.Add(ordinal);
                }

                // Classify and lookup
                ClassifyAndLookup(w, text);

                ordinal++;
            }

            void EndSentence(string? endChar = null)
            {
                if (currentSentenceWords.Count == 0) return;

                // Determine sentence type
                currentStats.Type = endChar switch
                {
                    "?" => SentenceType.Question,
                    "!" => SentenceType.Exclamation,
                    "." => SentenceType.Declarative,
                    _ => SentenceType.Fragment
                };

                // Calculate average word length
                int totalChars = 0;
                int contentCount = 0;
                foreach (var w in currentSentenceWords)
                {
                    if (analysis.WordAnnotations.TryGetValue(w.Ordinal, out var wa) &&
                        wa.Class.HasFlag(WordClass.Content))
                    {
                        totalChars += w.text.Length;
                        contentCount++;
                    }
                }
                currentStats.AverageWordLength = contentCount > 0 ? (float)totalChars / contentCount : 0;

                // Finalize sentence
                if (endChar != null) currentSentenceData.EndChar = endChar;
                currentSentenceData.Words = currentSentenceWords.ToArray();
                var sentence = new Sentence(currentSentenceData);
                sentences.Add(sentence);

                // Store sentence stats
                analysis.SentenceStats[sentenceOrdinal] = currentStats;

                // Update book-level stats
                analysis.DefinedWordCount += currentStats.DefinedWordCount;
                analysis.UndefinedWordCount += currentStats.UndefinedWordCount;
                analysis.ContentWordCount += currentStats.ContentWordCount;

                // Fire callback - sentence is ready!
                onSentenceReady?.Invoke(sentence, currentStats, sentenceIndex);

                // Reset for next sentence
                sentenceIndex++;
                sentenceOrdinal++;
                currentSentenceWords.Clear();
                currentSentenceData = new SentenceData { Ordinal = sentenceOrdinal };
                currentStats = new SentenceStats
                {
                    Page = currentPage,
                    Chapter = currentChapter,
                    ChapterId = currentChapterId
                };
            }

            // === Main parsing loop ===
            ReadOnlySpan<char> span = html.AsSpan();
            int n = span.Length;
            int wordStart = -1;
            bool collectingWhitespace = false;
            bool pendingSentenceEnd = false;
            string? pendingEndChar = null;

            const int MaxSentenceWordLength = 1000;

            for (int i = 0; i < n; i++)
            {
                char c = span[i];

                // HTML Tag
                if (c == '<')
                {
                    // Flush pending word
                    if (wordStart >= 0)
                    {
                        var wordSpan = span.Slice(wordStart, i - wordStart);
                        string text = collectingWhitespace && wordSpan.Length == 1 && wordSpan[0] == ' '
                            ? StringCache.Space
                            : StringCache.Intern(wordSpan);
                        AddWord(text);
                        wordStart = -1;
                    }
                    collectingWhitespace = false;

                    if (pendingSentenceEnd)
                    {
                        EndSentence(pendingEndChar);
                        pendingSentenceEnd = false;
                        pendingEndChar = null;
                    }

                    // Read tag
                    int tagStart = i;
                    while (i < n && span[i] != '>') i++;
                    int length = (i < n) ? (i - tagStart + 1) : (n - tagStart);

                    var tagSpan = span.Slice(tagStart, length);
                    bool isSentenceBreakTag = tagSpan.IsSentenceBreakTag();

                    // Detect page markers: <a id="page123"></a> or <a id="pageiii"></a>
                    int detectedPage = DetectPageMarker(tagSpan);
                    if (detectedPage > 0)
                    {
                        currentPage = detectedPage;
                        currentStats.Page = currentPage;
                    }

                    // Detect chapter markers: <div id='juice_ch01.xhtml' class='chapter'>
                    var (chapterNum, chapterId) = DetectChapterMarker(tagSpan);
                    if (chapterNum > 0)
                    {
                        currentChapter = chapterNum;
                        currentChapterId = chapterId;
                        currentStats.Chapter = currentChapter;
                        currentStats.ChapterId = currentChapterId;
                    }

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

                // Punctuation
                if (isPunct)
                {
                    // Check for contraction: word + apostrophe + suffix
                    bool isContraction = false;
                    int contractionEnd = i;

                    if (ContractionHandler.IsApostrophe(c) && wordStart >= 0 && !collectingWhitespace)
                    {
                        // Look ahead for contraction suffix
                        int suffixStart = i + 1;
                        int suffixEnd = suffixStart;

                        // Collect suffix letters (e.g., "t", "re", "ve", "ll", "d", "m", "s")
                        while (suffixEnd < n && char.IsLetter(span[suffixEnd]))
                        {
                            suffixEnd++;
                        }

                        if (suffixEnd > suffixStart)
                        {
                            var wordSpan = span.Slice(wordStart, i - wordStart);
                            var suffixSpan = span.Slice(suffixStart, suffixEnd - suffixStart);

                            if (ContractionHandler.IsContraction(wordSpan, c, suffixSpan))
                            {
                                // It's a contraction - continue collecting to include apostrophe and suffix
                                isContraction = true;
                                contractionEnd = suffixEnd;
                            }
                        }
                    }

                    if (isContraction)
                    {
                        // Skip to end of contraction - the word will be emitted when we hit next boundary
                        // Don't flush the word yet, just continue past the apostrophe and suffix
                        i = contractionEnd - 1;  // -1 because loop will increment
                        // Word continues from wordStart through contractionEnd
                    }
                    else
                    {
                        // Normal punctuation handling
                        if (wordStart >= 0)
                        {
                            var wordSpan = span.Slice(wordStart, i - wordStart);
                            string text = collectingWhitespace && wordSpan.Length == 1 && wordSpan[0] == ' '
                                ? StringCache.Space
                                : StringCache.Intern(wordSpan);
                            AddWord(text);
                            wordStart = -1;
                        }
                        collectingWhitespace = false;

                        if (pendingSentenceEnd)
                        {
                            EndSentence(pendingEndChar);
                            pendingSentenceEnd = false;
                            pendingEndChar = null;
                        }

                        string punctString = StringCache.GetChar(c);
                        AddWord(punctString);

                        // Sentence end detection
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
                }
                // Whitespace
                else if (isSpace)
                {
                    if (wordStart >= 0 && !collectingWhitespace)
                    {
                        var wordSpan = span.Slice(wordStart, i - wordStart);
                        AddWord(StringCache.Intern(wordSpan));
                        wordStart = -1;
                    }

                    if (wordStart < 0)
                    {
                        wordStart = i;
                    }
                    collectingWhitespace = true;
                }
                // Content character
                else
                {
                    if (wordStart >= 0 && collectingWhitespace)
                    {
                        var wordSpan = span.Slice(wordStart, i - wordStart);
                        string text = wordSpan.Length == 1 && wordSpan[0] == ' '
                            ? StringCache.Space
                            : StringCache.Intern(wordSpan);
                        AddWord(text);
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

                // Safety: runaway sentences
                if (currentSentenceWords.Count >= MaxSentenceWordLength)
                {
                    var lastWord = currentSentenceWords[^1];
                    if (!lastWord.IsPunctuation && (lastWord.span.IsEmpty || lastWord.span[0] != '<'))
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
                AddWord(text);
            }

            if (pendingSentenceEnd)
            {
                EndSentence(pendingEndChar);
            }

            // Final sentence without punctuation
            if (currentSentenceWords.Count > 0)
            {
                currentSentenceData.Words = currentSentenceWords.ToArray();
                var sentence = new Sentence(currentSentenceData);
                sentences.Add(sentence);
                analysis.SentenceStats[sentenceOrdinal] = currentStats;
                analysis.ContentWordCount += currentStats.ContentWordCount;
                analysis.DefinedWordCount += currentStats.DefinedWordCount;
                analysis.UndefinedWordCount += currentStats.UndefinedWordCount;
                onSentenceReady?.Invoke(sentence, currentStats, sentenceIndex);
            }

            // Finalize analysis
            analysis.TotalTokens = words.Count;
            analysis.UniqueWordCount = vocabOrdinals.Count;
            analysis.SentenceCount = sentences.Count;

            var content = new BookContent
            {
                Words = words.ToImmutableArray(),
                Sentences = sentences.ToImmutableArray(),
                Vocabulary = vocabOrdinals.ToFrozenDictionary(
                    kvp => kvp.Key,
                    kvp => new WordStructs.WordStats(kvp.Value.ToImmutableArray()),
                    StringComparer.OrdinalIgnoreCase),
                Annotations = new List<Annotation>()
            };

            return (content, analysis);
        }

        /// <summary>
        /// Detects page markers in anchor tags like &lt;a id="page123"&gt; or &lt;a id="pageiii"&gt;.
        /// Returns the page number, or 0 if not a page marker.
        /// </summary>
        private static int DetectPageMarker(ReadOnlySpan<char> tagSpan)
        {
            // Look for: <a id="page123"> or similar
            // Format: id="page" followed by number or roman numerals

            const string pagePattern = "id=\"page";
            int idx = tagSpan.IndexOf(pagePattern.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return 0;

            int start = idx + pagePattern.Length;
            if (start >= tagSpan.Length) return 0;

            // Check if it's a number or roman numeral
            int end = start;
            while (end < tagSpan.Length && tagSpan[end] != '"' && tagSpan[end] != '>')
            {
                end++;
            }

            if (end <= start) return 0;

            var pageValue = tagSpan.Slice(start, end - start);

            // Try parse as integer first
            if (int.TryParse(pageValue, out int pageNum))
            {
                return pageNum;
            }

            // Try parse as roman numeral (simple version for front matter)
            return ParseRomanNumeral(pageValue);
        }

        /// <summary>
        /// Detects chapter markers in div tags like &lt;div id='juice_ch01.xhtml' class='chapter'&gt;.
        /// Returns (chapterNumber, chapterId) or (0, null) if not a chapter marker.
        /// </summary>
        private static (int ChapterNum, string? ChapterId) DetectChapterMarker(ReadOnlySpan<char> tagSpan)
        {
            // Look for: <div ... class='chapter' or class="chapter"
            // And extract chapter number from id like 'juice_ch01.xhtml' or 'ch01'

            // Must be a div tag with chapter class
            if (!tagSpan.StartsWith("<div", StringComparison.OrdinalIgnoreCase))
                return (0, null);

            // Check for class='chapter' or class="chapter"
            bool hasChapterClass = tagSpan.IndexOf("class='chapter'".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0
                                || tagSpan.IndexOf("class=\"chapter\"".AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0;

            if (!hasChapterClass)
                return (0, null);

            // Extract id value
            int idStart = -1;
            int idEnd = -1;

            // Look for id=' or id="
            int idIdx = tagSpan.IndexOf("id='".AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (idIdx >= 0)
            {
                idStart = idIdx + 4;
                idEnd = tagSpan.Slice(idStart).IndexOf('\'');
                if (idEnd >= 0) idEnd += idStart;
            }
            else
            {
                idIdx = tagSpan.IndexOf("id=\"".AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (idIdx >= 0)
                {
                    idStart = idIdx + 4;
                    idEnd = tagSpan.Slice(idStart).IndexOf('"');
                    if (idEnd >= 0) idEnd += idStart;
                }
            }

            if (idStart < 0 || idEnd < 0)
                return (0, null);

            var idValue = tagSpan.Slice(idStart, idEnd - idStart);
            string chapterId = idValue.ToString();

            // Try to extract chapter number from patterns like:
            // - juice_ch01.xhtml -> 1
            // - ch01 -> 1
            // - chapter_1 -> 1
            int chapterNum = ExtractChapterNumber(idValue);

            return (chapterNum, chapterId);
        }

        /// <summary>
        /// Extracts chapter number from an id string like "juice_ch01.xhtml" or "ch01".
        /// </summary>
        private static int ExtractChapterNumber(ReadOnlySpan<char> id)
        {
            // Look for "ch" followed by digits
            int chIdx = id.IndexOf("ch".AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (chIdx >= 0)
            {
                int numStart = chIdx + 2;
                int numEnd = numStart;

                while (numEnd < id.Length && char.IsDigit(id[numEnd]))
                {
                    numEnd++;
                }

                if (numEnd > numStart)
                {
                    if (int.TryParse(id.Slice(numStart, numEnd - numStart), out int num))
                    {
                        return num;
                    }
                }
            }

            // Fallback: look for any sequence of digits
            for (int i = 0; i < id.Length; i++)
            {
                if (char.IsDigit(id[i]))
                {
                    int numStart = i;
                    int numEnd = i;
                    while (numEnd < id.Length && char.IsDigit(id[numEnd]))
                    {
                        numEnd++;
                    }

                    if (int.TryParse(id.Slice(numStart, numEnd - numStart), out int num))
                    {
                        return num;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Simple roman numeral parser for page numbers (i, ii, iii, iv, v, vi, vii, viii, ix, x, etc.)
        /// Returns negative numbers for front matter (-1, -2, etc.) to distinguish from main content.
        /// </summary>
        private static int ParseRomanNumeral(ReadOnlySpan<char> roman)
        {
            if (roman.IsEmpty) return 0;

            int total = 0;
            int prevValue = 0;

            for (int i = roman.Length - 1; i >= 0; i--)
            {
                int value = char.ToLower(roman[i]) switch
                {
                    'i' => 1,
                    'v' => 5,
                    'x' => 10,
                    'l' => 50,
                    'c' => 100,
                    'd' => 500,
                    'm' => 1000,
                    _ => 0
                };

                if (value == 0) return 0;  // Invalid character

                if (value < prevValue)
                    total -= value;
                else
                    total += value;

                prevValue = value;
            }

            // Return negative for front matter pages (roman numerals)
            return -total;
        }
    }
}
