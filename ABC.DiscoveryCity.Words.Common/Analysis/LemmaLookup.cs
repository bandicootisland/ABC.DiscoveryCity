using System.Collections.Frozen;

namespace ABC.DiscoveryCity.Words.Common.Analysis
{
    /// <summary>
    /// Provides lemma-aware dictionary lookups.
    /// Tries original word first, then various lemmatized forms.
    /// The Word itself is NOT modified - only the lookup strategy changes.
    /// </summary>
    public static class LemmaLookup
    {
        // Common irregular verb forms: past/past participle → base
        private static readonly FrozenDictionary<string, string> IrregularVerbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Be
            { "was", "be" }, { "were", "be" }, { "been", "be" }, { "am", "be" }, { "are", "be" }, { "is", "be" },

            // Have
            { "had", "have" }, { "has", "have" },

            // Do
            { "did", "do" }, { "does", "do" }, { "done", "do" },

            // Common irregulars
            { "went", "go" }, { "gone", "go" }, { "goes", "go" },
            { "came", "come" }, { "comes", "come" },
            { "saw", "see" }, { "seen", "see" }, { "sees", "see" },
            { "gave", "give" }, { "given", "give" }, { "gives", "give" },
            { "took", "take" }, { "taken", "take" }, { "takes", "take" },
            { "made", "make" }, { "makes", "make" },
            { "said", "say" }, { "says", "say" },
            { "got", "get" }, { "gotten", "get" }, { "gets", "get" },
            { "knew", "know" }, { "known", "know" }, { "knows", "know" },
            { "thought", "think" }, { "thinks", "think" },
            { "found", "find" }, { "finds", "find" },
            { "told", "tell" }, { "tells", "tell" },
            { "felt", "feel" }, { "feels", "feel" },
            { "left", "leave" }, { "leaves", "leave" },
            { "meant", "mean" }, { "means", "mean" },
            { "kept", "keep" }, { "keeps", "keep" },
            { "let", "let" }, { "lets", "let" },
            { "began", "begin" }, { "begun", "begin" }, { "begins", "begin" },
            { "seemed", "seem" }, { "seems", "seem" },
            { "stood", "stand" }, { "stands", "stand" },
            { "sat", "sit" }, { "sits", "sit" },
            { "ran", "run" }, { "runs", "run" },
            { "held", "hold" }, { "holds", "hold" },
            { "brought", "bring" }, { "brings", "bring" },
            { "wrote", "write" }, { "written", "write" }, { "writes", "write" },
            { "read", "read" },  // same spelling, different pronunciation
            { "spoke", "speak" }, { "spoken", "speak" }, { "speaks", "speak" },
            { "broke", "break" }, { "broken", "break" }, { "breaks", "break" },
            { "chose", "choose" }, { "chosen", "choose" }, { "chooses", "choose" },
            { "fell", "fall" }, { "fallen", "fall" }, { "falls", "fall" },
            { "grew", "grow" }, { "grown", "grow" }, { "grows", "grow" },
            { "threw", "throw" }, { "thrown", "throw" }, { "throws", "throw" },
            { "drew", "draw" }, { "drawn", "draw" }, { "draws", "draw" },
            { "drove", "drive" }, { "driven", "drive" }, { "drives", "drive" },
            { "ate", "eat" }, { "eaten", "eat" }, { "eats", "eat" },
            { "drank", "drink" }, { "drunk", "drink" }, { "drinks", "drink" },
            { "swam", "swim" }, { "swum", "swim" }, { "swims", "swim" },
            { "sang", "sing" }, { "sung", "sing" }, { "sings", "sing" },
            { "rang", "ring" }, { "rung", "ring" }, { "rings", "ring" },
            { "won", "win" }, { "wins", "win" },
            { "lost", "lose" }, { "loses", "lose" },
            { "built", "build" }, { "builds", "build" },
            { "sent", "send" }, { "sends", "send" },
            { "spent", "spend" }, { "spends", "spend" },
            { "bought", "buy" }, { "buys", "buy" },
            { "caught", "catch" }, { "catches", "catch" },
            { "taught", "teach" }, { "teaches", "teach" },
            { "sought", "seek" }, { "seeks", "seek" },
            { "became", "become" }, { "becomes", "become" },
            { "understood", "understand" }, { "understands", "understand" },
            { "heard", "hear" }, { "hears", "hear" },
            { "met", "meet" }, { "meets", "meet" },
            { "slept", "sleep" }, { "sleeps", "sleep" },
            { "woke", "wake" }, { "woken", "wake" }, { "wakes", "wake" },
            { "wore", "wear" }, { "worn", "wear" }, { "wears", "wear" },
            { "led", "lead" }, { "leads", "lead" },
            { "paid", "pay" }, { "pays", "pay" },
            { "laid", "lay" }, { "lays", "lay" },
            { "set", "set" }, { "sets", "set" },
            { "put", "put" }, { "puts", "put" },
            { "cut", "cut" }, { "cuts", "cut" },
            { "hit", "hit" }, { "hits", "hit" },
            { "hurt", "hurt" }, { "hurts", "hurt" },
            { "shut", "shut" }, { "shuts", "shut" },

            // Irregular plurals → singular (noun forms)
            { "children", "child" },
            { "men", "man" },
            { "women", "woman" },
            { "feet", "foot" },
            { "teeth", "tooth" },
            { "mice", "mouse" },
            { "geese", "goose" },
            { "people", "person" },
            { "lives", "life" },
            { "wives", "wife" },
            { "knives", "knife" },
            // "leaves" already maps to "leave" (verb) - "leaf" handled by rule-based -ves → -f
            { "selves", "self" },
            { "halves", "half" },
            { "wolves", "wolf" },
            { "calves", "calf" },
            { "shelves", "shelf" },
            { "loaves", "loaf" },
            { "thieves", "thief" },
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Generates possible lemma forms for a word, in order of likelihood.
        /// Returns the original word first, then variations.
        /// </summary>
        public static IEnumerable<string> GetLemmaForms(string word)
        {
            if (string.IsNullOrEmpty(word)) yield break;

            // 1. Original word (always try first)
            yield return word;

            // 2. Check irregular forms
            if (IrregularVerbs.TryGetValue(word, out var irregular))
            {
                yield return irregular;
            }

            // 3. Lowercase version (if different)
            string lower = word.ToLowerInvariant();
            if (lower != word)
            {
                yield return lower;
            }

            int len = lower.Length;

            // 4. Strip -'s (possessive) → check for apostrophe at end
            if (len > 2 && (lower.EndsWith("'s") || lower.EndsWith("'s")))
            {
                yield return lower[..^2];
            }

            // 5. Strip -s (simple plural/3rd person)
            if (len > 2 && lower.EndsWith("s") && !lower.EndsWith("ss"))
            {
                yield return lower[..^1];
            }

            // 6. Strip -es (plurals: boxes, watches, bushes)
            if (len > 3 && lower.EndsWith("es"))
            {
                yield return lower[..^2];

                // Also try -e removal: ages → age
                yield return lower[..^1];
            }

            // 7. Strip -ies → -y (stories → story, cities → city)
            if (len > 4 && lower.EndsWith("ies"))
            {
                yield return lower[..^3] + "y";
            }

            // 8. Strip -ed (past tense)
            if (len > 3 && lower.EndsWith("ed"))
            {
                // needed → need
                yield return lower[..^2];

                // stopped → stop (double consonant)
                if (len > 4 && lower[^3] == lower[^4])
                {
                    yield return lower[..^3];
                }

                // loved → love (-d only)
                if (lower[^3] != 'e')
                {
                    yield return lower[..^1];
                }

                // tried → try (-ied → -y)
                if (len > 4 && lower.EndsWith("ied"))
                {
                    yield return lower[..^3] + "y";
                }
            }

            // 9. Strip -ing (progressive)
            if (len > 4 && lower.EndsWith("ing"))
            {
                // running → run (double consonant)
                if (len > 5 && lower[^4] == lower[^5])
                {
                    yield return lower[..^4];
                }

                // making → make (add -e)
                yield return lower[..^3] + "e";

                // doing → do
                yield return lower[..^3];

                // trying → try (-ying → -y)
                if (lower.EndsWith("ying"))
                {
                    yield return lower[..^4] + "y";
                }
            }

            // 10. Strip -er (comparative: bigger → big)
            if (len > 3 && lower.EndsWith("er"))
            {
                yield return lower[..^2];

                // bigger → big (double consonant)
                if (len > 4 && lower[^3] == lower[^4])
                {
                    yield return lower[..^3];
                }
            }

            // 11. Strip -est (superlative: biggest → big)
            if (len > 4 && lower.EndsWith("est"))
            {
                yield return lower[..^3];

                // biggest → big (double consonant)
                if (len > 5 && lower[^4] == lower[^5])
                {
                    yield return lower[..^4];
                }
            }

            // 12. Strip -ly (adverbs: quickly → quick)
            if (len > 3 && lower.EndsWith("ly"))
            {
                yield return lower[..^2];

                // happily → happy (-ily → -y)
                if (lower.EndsWith("ily"))
                {
                    yield return lower[..^3] + "y";
                }
            }
        }

        /// <summary>
        /// Creates a lemma-aware lookup delegate that wraps an existing dictionary lookup.
        /// Tries original word first, then lemmatized forms.
        /// </summary>
        /// <param name="baseLookup">The underlying dictionary lookup delegate.</param>
        /// <returns>A new lookup delegate that tries lemma forms.</returns>
        public static DictionaryLookupDelegate CreateLemmaAwareLookup(DictionaryLookupDelegate baseLookup)
        {
            return (ReadOnlySpan<char> word) =>
            {
                string wordStr = word.ToString();

                foreach (var form in GetLemmaForms(wordStr))
                {
                    var result = baseLookup(form.AsSpan());
                    if (result.Found)
                    {
                        // Return result, but note: the original word is what's stored
                        // The lemma form is just for lookup
                        return result;
                    }
                }

                return DictionaryLookupResult.NotFound;
            };
        }
    }
}
