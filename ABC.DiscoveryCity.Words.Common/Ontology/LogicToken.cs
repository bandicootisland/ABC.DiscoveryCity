using System;

namespace ABC.DiscoveryCity.Words.Common.Ontology
{
    public readonly struct LogicToken
    {
        public string Text { get; }       // "fine particulate matter"
        public LogicOp Op { get; }        // LogicOp.IsA
        public string Target { get; }     // "pollutant"
        public WFlags Flags { get; }      // Formatting (Upper, Bold)
        public byte Strength { get; }     // 0-255 (Confidence / Weight)

        // Constructor for DSL (Code-first)
        public LogicToken(string text, LogicOp op, string target, WFlags flags = WFlags.None, byte strength = 255)
        {
            Text = text;
            Op = op;
            Target = target;
            Flags = flags;
            Strength = strength;
        }

        public override string ToString()
            => $"'{Text}' --[{Op} ({Strength})--> '{Target}'";
    }
}