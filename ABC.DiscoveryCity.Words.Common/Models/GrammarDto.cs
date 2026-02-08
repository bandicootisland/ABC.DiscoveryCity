using System.Collections.Generic;

namespace ABC.DiscoveryCity.Words.Common.Models
{
    public class GrammarResponse
    {
        public string Doi { get; set; } = "";
        public List<GrammarSentence> Sentences { get; set; } = new List<GrammarSentence>();
    }

    public class GrammarSentence
    {
        public int Ordinal { get; set; }
        public List<GrammarWord> Words { get; set; } = new List<GrammarWord>();
    }

    public class GrammarWord
    {
        public string Text { get; set; } = "";
        public string Flags { get; set; } = "";
        public bool IsTag { get; set; }
        public bool IsPunctuation { get; set; }
    }
}
