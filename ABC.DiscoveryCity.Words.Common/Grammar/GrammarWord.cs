using System;
using System.Collections.Generic;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common.Grammar
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public enum PartOfSpeech
    {
        Noun, Pronoun, Verb, Adjective, Adverb, Preposition, Determiner, Conjunction, Unknown
    }

    public struct Word
    {
        public string Text;
        public PartOfSpeech Type;
        public int Index;

        public Word(string text, PartOfSpeech type, int index)
        {
            Text = text;
            Type = type;
            Index = index;
        }

        public override string ToString() => $"{Text} ({Type})";
    }

    public class SentenceSpine
    {
        public Word? Subject { get; set; }
        public Word? MainVerb { get; set; }
        public Word? DirectObject { get; set; }

        // Determine the "Concept" based on what we found
        public string NarrativeConcept
        {
            get
            {
                if (!Subject.HasValue && !MainVerb.HasValue) return "Fragment (Impressionism)";
                if (MainVerb.HasValue && !Subject.HasValue) return "Imperative (Command)";
                if (Subject.HasValue && MainVerb.HasValue && !DirectObject.HasValue) return "Essential Truth (Intransitive)";
                return "Active Agency (Transitive)";
            }
        }
    }
}
