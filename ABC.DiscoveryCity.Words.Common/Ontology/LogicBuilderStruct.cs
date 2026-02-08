
namespace ABC.DiscoveryCity.Words.Common.Ontology
{
    public readonly struct LogicBuilder
    {
        public string SubjectText { get; }
        public LogicOp Op { get; }

        public LogicBuilder(string subject, LogicOp op)
        {
            SubjectText = subject;
            Op = op;
        }

        // The Second Pipe: Builder | Target -> Token
        public static LogicToken operator |(LogicBuilder b, Word target)
        {
            // Defaulting strength to 255 (Absolute Fact) for pipe syntax
            return new LogicToken(b.SubjectText, b.Op, target.text, WFlags.None, 255);
        }
    }
}