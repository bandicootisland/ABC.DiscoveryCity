using System.Collections.Frozen;
using System.Collections.Immutable;

namespace ABC.DiscoveryCity.Words.Common.Analysis
{
    /// <summary>
    /// Sense disambiguation using context words.
    /// Supports both manual rules and auto-mined context from dictionaries.
    /// </summary>
    public class SenseDisambiguator
    {
        // Mined context storage (populated by SetMinedContext)
        private FrozenDictionary<string, ImmutableArray<MinedSenseContext>>? _minedContext;

        /// <summary>
        /// Sets mined context from a dictionary extraction.
        /// Call this after mining to enable auto-disambiguation.
        /// </summary>
        public void SetMinedContext(FrozenDictionary<string, ImmutableArray<MinedSenseContext>> minedContext)
        {
            _minedContext = minedContext;
        }

        /// <summary>
        /// Represents mined context for a sense (used by external miners).
        /// </summary>
        public readonly struct MinedSenseContext
        {
            public string SenseId { get; init; }
            public FrozenSet<string> ContextWords { get; init; }
            public float Weight { get; init; }

            public MinedSenseContext(string senseId, IEnumerable<string> contextWords, float weight = 1.0f)
            {
                SenseId = senseId;
                ContextWords = contextWords.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                Weight = weight;
            }
        }

        /// <summary>
        /// Represents a disambiguated sense of a word.
        /// </summary>
        public readonly struct SenseResult
        {
            /// <summary>
            /// OED sense ID (e.g., "1.a", "1.d", "4.a").
            /// </summary>
            public readonly string SenseId;

            /// <summary>
            /// Human-readable label for the sense.
            /// </summary>
            public readonly string SenseLabel;

            /// <summary>
            /// Confidence score (0-1+).
            /// </summary>
            public readonly float Confidence;

            /// <summary>
            /// The context word that matched, if any.
            /// </summary>
            public readonly string? MatchedContext;

            public SenseResult(string senseId, string senseLabel, float confidence = 1.0f, string? matchedContext = null)
            {
                SenseId = senseId;
                SenseLabel = senseLabel;
                Confidence = confidence;
                MatchedContext = matchedContext;
            }

            public static readonly SenseResult Unknown = new("?", "unknown", 0f);
        }

        /// <summary>
        /// Rule for disambiguating a sense based on context words.
        /// </summary>
        private class SenseRule
        {
            public string SenseId { get; init; } = "";  // OED sense ID (e.g., "1.a", "1.d")
            public string Label { get; init; } = "";
            public HashSet<string> ContextWords { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public float Weight { get; init; } = 1.0f;
        }

        // Rules for "juice" - mapped to actual OED sense IDs
        // OED senses: 1.a (fruit liquid), 1.b (wine/alcohol), 1.c (sugarcane),
        //             1.d (electricity-slang), 1.e (petrol-slang), 1.f (drugs-slang),
        //             2 (body fluids), 3 (moisture), 4.a (figurative-essence),
        //             4.b (profits-obs), 4.c (political influence), 5 (broth-obs)
        private static readonly List<SenseRule> JuiceRules = new()
        {
            new SenseRule
            {
                SenseId = "1.a",
                Label = "liquid from fruit/vegetables",
                ContextWords = new(StringComparer.OrdinalIgnoreCase)
                {
                    "orange", "apple", "fruit", "lemon", "grape", "tomato",
                    "drink", "drank", "drinking", "sip", "sipped", "gulp",
                    "jug", "glass", "bottle", "cold", "sweet", "fresh",
                    "pour", "poured", "squeeze", "squeezed", "mango", "pineapple"
                },
                Weight = 1.5f  // Higher weight for literal meaning with strong context
            },
            new SenseRule
            {
                SenseId = "1.d",
                Label = "electricity/electric current (slang)",
                ContextWords = new(StringComparer.OrdinalIgnoreCase)
                {
                    "turbine", "turbines", "motor", "motors", "power", "battery", "batteries",
                    "generator", "generating", "generate", "electric", "electricity",
                    "trickle", "reserve", "charge", "charged", "current", "watts",
                    "solar", "panel", "panels", "grid", "volt", "volts", "amp", "amps",
                    "harness", "harnesses", "distributing", "bank", "batts", "batt",
                    "AC", "HiVap", "fans", "cooled", "structure", "structures",
                    "enough", "stores"  // context from Juice book
                },
                Weight = 1.3f
            },
            new SenseRule
            {
                SenseId = "1.e",
                Label = "petrol/fuel (slang)",
                ContextWords = new(StringComparer.OrdinalIgnoreCase)
                {
                    "fuel", "petrol", "gas", "tank", "fill", "station",
                    "accelerate", "throttle", "engine", "diesel", "pump"
                },
                Weight = 1.2f
            },
            new SenseRule
            {
                SenseId = "2",
                Label = "body fluids/moisture",
                ContextWords = new(StringComparer.OrdinalIgnoreCase)
                {
                    "vein", "veins", "blood", "body", "gastric", "stomach",
                    "digestive", "secretion", "gland", "saliva"
                },
                Weight = 1.0f
            },
            new SenseRule
            {
                SenseId = "4.a",
                Label = "essence/spirit (figurative)",
                ContextWords = new(StringComparer.OrdinalIgnoreCase)
                {
                    "surge", "fill", "filled", "feeling", "felt", "energy",
                    "tired", "exhausted", "run", "running", "push", "pushed",
                    "spirit", "essence", "life", "vigor", "vitality", "zest"
                },
                Weight = 1.0f
            },
            new SenseRule
            {
                SenseId = "4.c",
                Label = "influence/power (slang)",
                ContextWords = new(StringComparer.OrdinalIgnoreCase)
                {
                    "got", "have", "had", "influence", "connections", "pull",
                    "favor", "political", "leverage", "clout"
                },
                Weight = 1.0f
            }
        };

        // Word -> rules mapping
        private static readonly FrozenDictionary<string, List<SenseRule>> WordRules =
            new Dictionary<string, List<SenseRule>>(StringComparer.OrdinalIgnoreCase)
            {
                { "juice", JuiceRules }
            }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Disambiguates a word based on surrounding context.
        /// Uses mined context if available, otherwise falls back to manual rules.
        /// </summary>
        /// <param name="targetWord">The word to disambiguate.</param>
        /// <param name="contextWords">Surrounding words (before and after).</param>
        /// <param name="windowSize">How many words on each side to consider.</param>
        /// <returns>The best matching sense, or Unknown if no rules match.</returns>
        public SenseResult Disambiguate(string targetWord, IReadOnlyList<string> contextWords, int targetIndex, int windowSize = 5)
        {
            // Collect context window first (used by both paths)
            var window = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int start = Math.Max(0, targetIndex - windowSize);
            int end = Math.Min(contextWords.Count, targetIndex + windowSize + 1);

            for (int i = start; i < end; i++)
            {
                if (i != targetIndex)
                {
                    window.Add(contextWords[i]);
                }
            }

            // Try mined context first (auto-extracted from dictionary)
            if (_minedContext != null && _minedContext.TryGetValue(targetWord, out var minedSenses))
            {
                var minedResult = DisambiguateWithMinedContext(minedSenses, window);
                if (minedResult.Confidence > 0.5f) // Only use if confident
                {
                    return minedResult;
                }
            }

            // Fall back to manual rules
            if (!WordRules.TryGetValue(targetWord, out var rules))
            {
                return SenseResult.Unknown;
            }

            // Score each sense using manual rules
            float bestScore = 0;
            SenseRule? bestRule = null;
            string? matchedWord = null;

            foreach (var rule in rules)
            {
                foreach (var contextWord in window)
                {
                    if (rule.ContextWords.Contains(contextWord))
                    {
                        float score = rule.Weight;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestRule = rule;
                            matchedWord = contextWord;
                        }
                    }
                }
            }

            if (bestRule != null)
            {
                return new SenseResult(bestRule.SenseId, bestRule.Label, bestScore, matchedWord);
            }

            // Default sense if no context match (most common meaning)
            return new SenseResult("1.a", "liquid from fruit/vegetables (default)", 0.5f, null);  // Default for "juice"
        }

        /// <summary>
        /// Disambiguates using auto-mined context from dictionary.
        /// </summary>
        private SenseResult DisambiguateWithMinedContext(ImmutableArray<MinedSenseContext> senses, HashSet<string> window)
        {
            float bestScore = 0;
            string bestSenseId = "";
            string? matchedWord = null;
            int matchCount = 0;

            foreach (var sense in senses)
            {
                int senseMatches = 0;
                string? firstMatch = null;

                foreach (var contextWord in window)
                {
                    if (sense.ContextWords.Contains(contextWord))
                    {
                        senseMatches++;
                        firstMatch ??= contextWord;
                    }
                }

                // Score = matches * weight
                float score = senseMatches * sense.Weight;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSenseId = sense.SenseId;
                    matchedWord = firstMatch;
                    matchCount = senseMatches;
                }
            }

            if (matchCount > 0)
            {
                // Confidence based on number of matches (more matches = higher confidence)
                float confidence = Math.Min(1.0f, 0.5f + (matchCount * 0.15f));
                return new SenseResult(bestSenseId, $"mined:{bestSenseId}", confidence, matchedWord);
            }

            return SenseResult.Unknown;
        }

        /// <summary>
        /// Disambiguates a word within a sentence.
        /// </summary>
        public SenseResult DisambiguateInSentence(string targetWord, Sentence sentence, int wordIndexInSentence)
        {
            // Extract content words from sentence
            var contentWords = new List<string>();
            int adjustedTargetIndex = 0;

            var words = sentence.words;
            for (int i = 0; i < words.Length; i++)
            {
                var word = words[i];
                if (!word.Flags.HasFlag(WFlags.IsTag) &&
                    !string.IsNullOrWhiteSpace(word.text) &&
                    word.text.Length > 1 &&
                    !char.IsPunctuation(word.text[0]))
                {
                    if (i == wordIndexInSentence)
                    {
                        adjustedTargetIndex = contentWords.Count;
                    }
                    contentWords.Add(word.text);
                }
            }

            return Disambiguate(targetWord, contentWords, adjustedTargetIndex);
        }
    }
}
