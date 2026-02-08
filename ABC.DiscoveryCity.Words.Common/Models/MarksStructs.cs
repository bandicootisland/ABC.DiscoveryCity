using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;




namespace ABC.DiscoveryCity.Words.Common
{
    public enum MarkType
    {
        Text,       // Standard words (e.g. "birds")
        Whitespace, // Spaces, tabs
        Punctuation // . , ; !
    }

    public struct Mark
    {
        public string Value { get; }
        public MarkType Type { get; }
        public int Index { get; }

        public Mark(string value, MarkType type, int index)
        {
            Value = value;
            Type = type;
            Index = index;
        }

        // --- Heuristic Helpers ---
        public bool IsWhitespace => Type == MarkType.Whitespace;
        public bool IsPunctuation => Type == MarkType.Punctuation;

        // Specific checks for your sentence logic
        public bool IsSentenceEnd => Value == "." || Value == "!" || Value == "?";
        public bool IsComma => Value == ",";

        public override string ToString() => $"[{Type}: '{Value}']";
    }
}