using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Structure;
using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace ABC.DiscoveryCity.Words.Structure
{
    /// <summary>
    /// The Central In-Memory Database for the Word City.
    /// Uses Columnar/Flat-Array storage to minimize GC overhead for millions of words.
    /// </summary>
    public static class WordWeb
    {
        // =========================================================
        // 1. MASTER STORAGE (The "Heap" of the Web)
        // =========================================================

        // The specific Context that owns ALL definition words (35M+).
        // Every 'Word' struct you get from a definition points here.
        private static SentenceData _masterCorpus = new SentenceData();

        // The Vocabulary Index (String -> Int ID)
        private static FrozenDictionary<string, int> _vocabIndex = FrozenDictionary<string, int>.Empty;

        // The Reverse Index (Int ID -> String)
        private static string[] _idToText = Array.Empty<string>();

        // =========================================================
        // 2. RELATIONSHIP GRAPHS (Adjacency Lists)
        // =========================================================

        // HEADWORD LEVEL: Concept ID -> Array of Senses
        private static Sense[][] _conceptSenses = Array.Empty<Sense[]>();

        // HEADWORD LEVEL: Concept ID -> Array of Synonyms (Global)
        private static Concept[][] _conceptSynonyms = Array.Empty<Concept[]>();

        // SENSE LEVEL: Sense ID -> (Offset, Count) in the Master Corpus
        private static (int Offset, int Count)[] _senseBounds = Array.Empty<(int, int)>();

        // SENSE LEVEL: Sense ID -> Back-link to Headword ID
        private static int[] _senseToConceptId = Array.Empty<int>();

        // SENSE LEVEL: Sense ID -> Specific Synonyms (e.g. "Run" means "Manage" in business context)
        private static Concept[][] _senseSynonyms = Array.Empty<Concept[]>();
        
        // SENSE LEVEL: Sense ID -> Part Of Speech (e.g. "noun", "verb", "phrase")
        private static string[] _sensePOS = Array.Empty<string>();

        // State Flag
        public static bool IsLoaded { get; private set; }

        // =========================================================
        // 3. HOT PATH ACCESSORS (O(1) / Zero Alloc)
        // =========================================================

        // Get the string text for an ID (Bounds checked)
        
        // Update Accessors to route negatives
        public static string GetText(int id)
        {
            if (id < 0) return UserLexicon.GetText(id);
            return (uint)id < (uint)_idToText.Length ? _idToText[id] : "";
        }
        
        public static string GetSensePOS(int id) =>
            (uint)id < (uint)_sensePOS.Length ? _sensePOS[id] : "";
        // Get definitions for a Headword
        public static ReadOnlySpan<Sense> GetSenses(int id) =>
            (uint)id < (uint)_conceptSenses.Length ? _conceptSenses[id] : ReadOnlySpan<Sense>.Empty;

        // Get global synonyms for a Headword
        public static ReadOnlySpan<Concept> GetSynonyms(int id) =>
            (uint)id < (uint)_conceptSynonyms.Length ? _conceptSynonyms[id] : ReadOnlySpan<Concept>.Empty;

        // Get synonyms specific to a Sense
        public static ReadOnlySpan<Concept> GetSenseSynonyms(int id) =>
            (uint)id < (uint)_senseSynonyms.Length ? _senseSynonyms[id] : ReadOnlySpan<Concept>.Empty;

        // Back-link: Which word does this definition define?
        public static Concept GetConceptForSense(int id) =>
            (uint)id < (uint)_senseToConceptId.Length ? new Concept(_senseToConceptId[id]) : new Concept(-1);

        // --- THE CRITICAL METHOD ---
        // Returns a "Window" into the Master Corpus without copying data.        
        public static Sentence GetDefinitionSentence(int senseId)
        {
            if (senseId < 0) return UserLexicon.GetDefinition(senseId);

            if ((uint)senseId >= (uint)_senseBounds.Length) return new Sentence(Word.None);
            var (offset, count) = _senseBounds[senseId];
            return new Sentence(_masterCorpus, offset, count);
        }


        // Inside WordWeb.cs

        public static Concept Lookup(ReadOnlySpan<char> text)
        {
            // 1. Check User Dictionary First (Override)
            if (UserLexicon.TryLookup(text, out int userId))
            {
                return new Concept(userId);
            }

            // 2. Check Static Web
            if (_vocabIndex.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(text, out int staticId))
            {
                return new Concept(staticId);
            }

            return new Concept(int.MinValue); // Invalid
        }

        

        // =========================================================
        // 4. THE BUILDER (Transplant Logic)
        // =========================================================

        public static void Build(IEnumerable<WordGrammar> sourceGrammar)
        {
            // --- A. PREPARE BUILDERS ---
            // Heuristic: Start large to avoid resizing.
            var allWords = new List<Word>(10_000_000);

            var vocabBuilder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var idToTextList = new List<string>();

            // Graph Builders (Index = Concept ID)
            var headSenses = new List<Sense[]>();
            var headSyns = new List<Concept[]>();

            // Sense Builders (Index = Sense ID)
            var senseBounds = new List<(int Offset, int Count)>();
            var senseToConcept = new List<int>();
            var senseSyns = new List<Concept[]>();
            var sensePOS = new List<string>();

            // The New Master Context we are constructing
            var masterContext = new SentenceData();
            int currentGlobalOffset = 0;

            // --- B. HELPER: GET OR CREATE ID ---
            // --- B. HELPER: GET OR CREATE ID (With Casing Refinement) ---
            int GetId(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return -1;

                // 1. Check if ID exists (Case-Insensitive)
                ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(vocabBuilder, text, out bool exists);

                if (!exists)
                {
                    // NEW ENTRY: Store exactly as provided
                    id = idToTextList.Count;
                    idToTextList.Add(text);

                    // Expand arrays
                    while (headSenses.Count <= id) headSenses.Add(Array.Empty<Sense>());
                    while (headSyns.Count <= id) headSyns.Add(Array.Empty<Concept>());
                }
                else
                {
                    // EXISTING ENTRY: Check if we can improve the display text
                    // This fixes the "UP" vs "up" issue.

                    // We only care if the CURRENT stored version is ALL CAPS.
                    // If it is, and the NEW version is NOT, we prefer the new version.

                    string currentStored = idToTextList[id];

                    // Fast Check: Only check character properties if lengths match (optimization)
                    // and the text is actually different.
                    if (!currentStored.Equals(text, StringComparison.Ordinal))
                    {
                        if (IsAllUpper(currentStored) && !IsAllUpper(text))
                        {
                            // UPGRADE: "UP" -> "up"
                            // We found a lowercase/mixed-case instance, which is 
                            // generally preferred for the canonical dictionary form.
                            idToTextList[id] = text;
                        }
                        else if (char.IsUpper(currentStored[0]) && char.IsLower(text[0]))
                        {
                            // OPTIONAL REFINEMENT: "Apple" -> "apple"
                            // If we have Title Case but find Lowercase, usually Lowercase 
                            // is the true headword (unless it's a proper noun).
                            // You can comment this out if you prefer Title Case to stick.

                            // Check if the rest of the word matches to avoid edge cases
                            if (IsAllLower(text))
                            {
                                idToTextList[id] = text;
                            }
                        }
                    }
                }
                return id;
            }

            // --- Helpers ---

            bool IsAllUpper(string s)
            {
                for (int i = 0; i < s.Length; i++)
                    if (char.IsLetter(s[i]) && !char.IsUpper(s[i])) return false;
                return true;
            }

            bool IsAllLower(string s)
            {
                for (int i = 0; i < s.Length; i++)
                    if (char.IsLetter(s[i]) && !char.IsLower(s[i])) return false;
                return true;
            }

            // --- C. MAIN LOOP ---
            foreach (var entry in sourceGrammar)
            {
                int conceptId = GetId(entry.Headword.text);

                if (entry.Definitions != null && entry.Definitions.Count > 0)
                {
                    var sensesForConcept = new Sense[entry.Definitions.Count];

                    for (int i = 0; i < entry.Definitions.Count; i++)
                    {
                        var def = entry.Definitions[i];

                        // 1. Transplant Words (Remains the same)
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
                        // senseId is the CURRENT count (e.g. 0, then 1, then 2)
                        int senseId = senseBounds.Count;
                        senseBounds.Add((startOffset, wordCount));
                        senseToConcept.Add(conceptId);
                        sensePOS.Add(def.PartOfSpeech ?? "");
                        sensesForConcept[i] = new Sense(senseId);

                        // 3. Register Synonyms (*** FIXED ***)
                        // We just ADD. We do NOT padding with 'while', because senseId is sequential.

                        if (def.Synonyms != null && def.Synonyms.Count > 0)
                        {
                            var links = new List<Concept>(def.Synonyms.Count);
                            foreach (var s in def.Synonyms)
                            {
                                string key = s.Length == 1 ? s[0].text : s.ToString().Trim();
                                int synId = GetId(key);
                                if (synId != -1) links.Add(new Concept(synId));
                            }
                            senseSyns.Add(links.ToArray()); // <--- Direct Add
                        }
                        else
                        {
                            senseSyns.Add(Array.Empty<Concept>()); // <--- Direct Add Empty
                        }
                    }
                    headSenses[conceptId] = sensesForConcept;
                }
            }

            // --- D. FREEZE & ASSIGN ---
            // Convert everything to static arrays to release builder memory

            _idToText = idToTextList.ToArray();
            _vocabIndex = vocabBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

            // Assign the massive word list to the Master Context
            _masterCorpus = masterContext;
            _masterCorpus.Words = allWords.ToArray();
            _masterCorpus.Ordinal = 0;

            // Finalize Graphs
            _conceptSenses = headSenses.ToArray();
            _conceptSynonyms = headSyns.ToArray();

            _senseBounds = senseBounds.ToArray();
            _senseToConceptId = senseToConcept.ToArray();
            _senseSynonyms = senseSyns.ToArray();
            _sensePOS = sensePOS.ToArray();

            IsLoaded = true;

            // --- E. CLEANUP ---
            // Help the GC reclaim the massive builder lists immediately
            vocabBuilder = null;
            idToTextList = null;
            allWords = null;
            headSenses = null;
            senseBounds = null;

            GC.Collect();
        }

    }
}