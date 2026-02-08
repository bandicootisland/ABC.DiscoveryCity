using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Structure;
using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace ABC.DiscoveryCity.Words.Common.Structure
{
    public static class CorpusWordWeb
    {
        // --- MASTER DATA ---

        // 1. The Vocabulary (Unique Headwords)
        private static FrozenDictionary<string, int> _vocabIndex = FrozenDictionary<string, int>.Empty;
        private static string[] _idToText = Array.Empty<string>();

        // 2. The Corpus (All Definition Text)
        // A single SentenceData context holding millions of Words.
        private static SentenceData _masterCorpus = new SentenceData();

        // --- RELATIONAL INDICES (Graph) ---

        // Concept ID -> Senses
        private static Sense[][] _conceptSenses = Array.Empty<Sense[]>();
        // Concept ID -> Global Synonyms
        private static Concept[][] _conceptSynonyms = Array.Empty<Concept[]>();

        // Sense ID -> Location in Master Corpus (Offset, Count)
        private static (int Offset, int Count)[] _senseBounds = Array.Empty<(int, int)>();
        // Sense ID -> Parent Concept ID
        private static int[] _senseToConceptId = Array.Empty<int>();
        // Sense ID -> Specific Synonyms
        private static Concept[][] _senseSynonyms = Array.Empty<Concept[]>();

        public static bool IsLoaded { get; private set; }

        // --- ACCESSORS (Hot Path) ---

        public static string GetText(int id) =>
            (uint)id < (uint)_idToText.Length ? _idToText[id] : "";

        public static ReadOnlySpan<Sense> GetSenses(int id) =>
            (uint)id < (uint)_conceptSenses.Length ? _conceptSenses[id] : ReadOnlySpan<Sense>.Empty;

        public static ReadOnlySpan<Concept> GetSynonyms(int id) =>
            (uint)id < (uint)_conceptSynonyms.Length ? _conceptSynonyms[id] : ReadOnlySpan<Concept>.Empty;

        public static ReadOnlySpan<Concept> GetSenseSynonyms(int id) =>
            (uint)id < (uint)_senseSynonyms.Length ? _senseSynonyms[id] : ReadOnlySpan<Concept>.Empty;

        public static Concept GetConceptForSense(int id) =>
            (uint)id < (uint)_senseToConceptId.Length ? new Concept(_senseToConceptId[id]) : new Concept(-1);

        public static Sentence GetDefinitionSentence(int senseId)
        {
            if ((uint)senseId >= (uint)_senseBounds.Length) return new Sentence(Word.None);

            var (offset, count) = _senseBounds[senseId];
            // Return a Window into the Master Corpus
            return new Sentence(_masterCorpus, offset, count);
        }

        public static Concept Lookup(ReadOnlySpan<char> text)
        {
            if (_vocabIndex.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out int id))
                return new Concept(id);
            return new Concept(-1);
        }

        // --- THE BUILDER ---

        public static void Build(IEnumerable<WordGrammar> sourceGrammar)
        {
            var allWords = new List<Word>(10_000_000);
            var vocabBuilder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var idToTextList = new List<string>();

            var headSenses = new List<Sense[]>();
            var headSyns = new List<Concept[]>();

            var senseBounds = new List<(int Offset, int Count)>();
            var senseToConcept = new List<int>();
            var senseSyns = new List<Concept[]>();

            // The Master Context
            var masterContext = new SentenceData();
            
            int currentGlobalOffset = 0;
            // The Master Context we are building
            var context = new SentenceData();
            int currentOffset = 0;

            // --- HELPERS ---
            int GetId(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return -1;
                ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(vocabBuilder, text, out bool exists);
                if (!exists)
                {
                    id = idToTextList.Count;
                    idToTextList.Add(text);
                    // Pad arrays
                    while (headSenses.Count <= id) headSenses.Add(Array.Empty<Sense>());
                    while (headSyns.Count <= id) headSyns.Add(Array.Empty<Concept>());
                }
                return id;
            }

            // --- BUILD LOOP ---
            foreach (var entry in sourceGrammar)
            {
                int conceptId = GetId(entry.Headword.text);

                if (entry.Definitions != null && entry.Definitions.Count > 0)
                {
                    var sensesForConcept = new Sense[entry.Definitions.Count];

                    for (int i = 0; i < entry.Definitions.Count; i++)
                    {
                        var def = entry.Definitions[i];

                        // 1. Transplant Words
                        var sourceSpan = def.Text.words;
                        int startOffset = currentGlobalOffset;
                        int wordCount = sourceSpan.Length;

                        for (int w = 0; w < wordCount; w++)
                        {
                            var srcWord = sourceSpan[w];
                            allWords.Add(new Word(
                                srcWord.Text, srcWord.Flags, srcWord.Punctuation,
                                srcWord.EncStart, srcWord.EncEnd,
                                masterContext, currentGlobalOffset, -1
                            ));
                            currentGlobalOffset++;
                        }

                        // 2. Register Sense
                        // senseId increases sequentially (0, 1, 2...)
                        int senseId = senseBounds.Count;
                        senseBounds.Add((startOffset, wordCount));
                        senseToConcept.Add(conceptId);
                        sensesForConcept[i] = new Sense(senseId);

                        // 3. Register Synonyms (*** THE FIX IS HERE ***)
                        // We REMOVED the 'while' loop padding. 
                        // Because senseId is sequential, we just ADD.

                        if (def.Synonyms != null && def.Synonyms.Count > 0)
                        {
                            var links = new List<Concept>(def.Synonyms.Count);
                            foreach (var s in def.Synonyms)
                            {
                                string key = s.Length == 1 ? s[0].text : s.ToString().Trim();
                                int synId = GetId(key);
                                if (synId != -1) links.Add(new Concept(synId));
                            }
                            senseSyns.Add(links.ToArray());
                        }
                        else
                        {
                            senseSyns.Add(Array.Empty<Concept>());
                        }
                    }
                    headSenses[conceptId] = sensesForConcept;
                }
            }

            // --- FREEZE & ASSIGN ---

            _idToText = idToTextList.ToArray();
            _vocabIndex = vocabBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            // Assign the giant array to the Master Context
            _masterCorpus = context;
            _masterCorpus.Words = allWords.ToArray();
            _masterCorpus.Ordinal = 0;

            _conceptSenses = headSenses.ToArray();
            _conceptSynonyms = headSyns.ToArray();

            _senseBounds = senseBounds.ToArray();
            _senseToConceptId = senseToConcept.ToArray();
            _senseSynonyms = senseSyns.ToArray();

            IsLoaded = true;

            // Garbage Collection
            vocabBuilder = null;
            allWords = null;
            GC.Collect();
        }
    }
}