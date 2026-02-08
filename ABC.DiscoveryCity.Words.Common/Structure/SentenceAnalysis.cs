using System;
using System.Collections.Generic;
using ABC.DiscoveryCity.Words.Structure; 

namespace ABC.DiscoveryCity.Words.Common.Structure
{
    /// <summary>
    /// Holds the state of semantic analysis for a specific context (e.g. valid for one book or one session).
    /// Stores which definition is selected for a given word occurrence.
    /// Usage: Pass this context to .AsSemantic() to enable stateful analysis.
    /// </summary>
    public class SemanticContext
    {
        // Key: Word.Ordinal (Global unique ID of the word usage in the book)
        // Value: Sense ID (The specific definition selected)
        private readonly Dictionary<int, int> _selectedSenses = new();

        // Key: Sense ID (Global Dictionary Definition ID)
        // Value: Count (How many times this sense was used/selected)
        private readonly Dictionary<int, int> _senseUsageCounts = new();

        public void SelectSense(int wordOrdinal, int senseId)
        {
            // Update selection
            if (_selectedSenses.TryGetValue(wordOrdinal, out int oldSenseId))
            {
                // Decrement old count
                if (_senseUsageCounts.TryGetValue(oldSenseId, out int count) && count > 0)
                    _senseUsageCounts[oldSenseId] = count - 1;
            }

            _selectedSenses[wordOrdinal] = senseId;

            // Increment new count
            if (!_senseUsageCounts.ContainsKey(senseId))
                _senseUsageCounts[senseId] = 0;
            _senseUsageCounts[senseId]++;
        }

        public int? GetSelectedSense(int wordOrdinal) 
            => _selectedSenses.TryGetValue(wordOrdinal, out int id) ? id : null;

        public int GetUsageCount(int senseId) 
            => _senseUsageCounts.TryGetValue(senseId, out int count) ? count : 0;
            
        public Dictionary<int, int> GetUsageSummary() => new Dictionary<int, int>(_senseUsageCounts);
    }

    public static class SentenceAnalysisExtensions
    {
        /// <summary>
        /// Projects the Sentence into a Semantic View, allowing lazy access to Definitions/Concepts.
        /// </summary>
        public static SemanticSentence AsSemantic(this Sentence sentence, SemanticContext? context = null) 
            => new SemanticSentence(sentence, context);
    }

    public readonly struct SemanticSentence
    {
        public readonly Sentence Sentence;
        private readonly SemanticContext? _context;

        public SemanticSentence(Sentence s, SemanticContext? context)
        {
            Sentence = s;
            _context = context;
        }

        public Enumerator GetEnumerator() => new Enumerator(Sentence, _context);

        public ref struct Enumerator
        {
            private ReadOnlySpan<Word> _words;
            private readonly SemanticContext? _context;
            private int _index;

            public Enumerator(Sentence s, SemanticContext? context)
            {
                _words = s.words;
                _context = context;
                _index = -1;
            }

            public bool MoveNext() => ++_index < _words.Length;

            public SemanticWord Current => new SemanticWord(_words[_index], _context);
        }
    }

    public readonly struct SemanticWord
    {
        public readonly Word Word;
        private readonly SemanticContext? _context;
        
        public SemanticWord(Word w, SemanticContext? context = null)
        {
            Word = w;
            _context = context;
        }

        // Lazy Lookup: Only hits the dictionary when accessed
        public Concept Concept => WordWeb.Lookup(Word.span);
        
        public bool HasDefinitions => Concept.IsValid && !Concept.Senses.IsEmpty;
        
        public ReadOnlySpan<Sense> Senses => Concept.Senses;

        // Stateful Logic
        public Sense? SelectedSense
        {
            get
            {
                if (_context == null) return null;
                var id = _context.GetSelectedSense(Word.Ordinal);
                return id.HasValue ? new Sense(id.Value) : null;
            }
            set
            {
                if (_context != null && value.HasValue && value.Value.IsValid)
                {
                    _context.SelectSense(Word.Ordinal, value.Value.Id);
                }
            }
        }

        /// <summary>
        /// Returns a semantic view of the definition for the currently selected sense (or first if none selected).
        /// This enables recursive analysis ("Drill Down").
        /// </summary>
        public SemanticSentence AnalyzeDefinition(int senseIndex = -1)
        {
             // 1. Determine which sense to analyze
             Sense targetSense;
             if (senseIndex >= 0 && senseIndex < Senses.Length)
             {
                 targetSense = Senses[senseIndex];
             }
             else if (SelectedSense.HasValue)
             {
                 targetSense = SelectedSense.Value;
             }
             else if (Senses.Length > 0)
             {
                 targetSense = Senses[0];
             }
             else
             {
                 return new SemanticSentence(new Sentence(Word.None), _context);
             }

             // 2. Return the semantic view of that definition's sentence
             // We propagate the context so we can select words INSIDE the definition too!
             return targetSense.Definition.AsSemantic(_context);
        }

        public void PrintRecursiveDefinitions(int maxDepth, int currentDepth = 0)
        {
            if (currentDepth >= maxDepth) return;

            string indent = new string(' ', (currentDepth + 1) * 4); 

            // Analyze the definition of the currently selected sense (or default)
            var definitionSentence = AnalyzeDefinition();

            // Track processed concepts to avoid duplicates in the same definition
            var seenConcepts = new HashSet<int>();

            foreach (var innerWord in definitionSentence)
            {
                // Simple filter to reduce noise (skip short stop words)
                if (innerWord.HasDefinitions && innerWord.Word.text.Length > 3)
                {
                    // Deduplicate key: Use Concept ID to treat synonyms/inflections as same
                    int conceptId = innerWord.Concept.Id;
                    if (seenConcepts.Contains(conceptId)) continue;
                    seenConcepts.Add(conceptId);

                    Console.WriteLine($"{indent}- {innerWord.Word} ({innerWord.Senses.Length} subsenses)");

                    // Recursive call
                    innerWord.PrintRecursiveDefinitions(maxDepth, currentDepth + 1);
                }
            }
        }

        public override string ToString() => Word.ToString();
    }
}
