using System;
using ABC.DiscoveryCity.Words.Common.Models;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline
{
    public interface ISemanticEncoder
    {
        Guid QuantizeToGuid(string rawText);
    }

    public class LinguisticAnalysis
    {
        public string Subject { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Intent { get; set; } = string.Empty;
        public string Manner { get; set; } = string.Empty;
    }

    public interface ILinguisticParser
    {
        LinguisticAnalysis Analyze(string rawText);
    }

    public interface ISymbolRegistry
    {
        short GetOrCreateId(string role, string value);
    }

    public class IngestionService
    {
        private readonly ISemanticEncoder _encoder;
        private readonly ILinguisticParser _parser;
        private readonly ISymbolRegistry _symbols;

        public IngestionService(ISemanticEncoder encoder, ILinguisticParser parser, ISymbolRegistry symbols)
        {
            _encoder = encoder;
            _parser = parser;
            _symbols = symbols;
        }

        public SentenceSignature Ingest(string rawText, Guid sentenceId, int docId)
        {
            // 1. Create the base struct
            var signature = new SentenceSignature
            {
                Id = sentenceId,
                DocId = docId
            };

            // 2. Semantic Path (The 'Vector' part)
            // Returns a 16-byte Guid representing the compressed 384-dim vector
            signature.SemanticId = _encoder.QuantizeToGuid(rawText);

            // 3. Linguistic Path (The 'Interrogative' part)
            // We use the parser to get the 7 roles
            var analysis = _parser.Analyze(rawText);
            
            signature.Who = _symbols.GetOrCreateId("Who", analysis.Subject);
            signature.What = _symbols.GetOrCreateId("What", analysis.Action);
            signature.Where = _symbols.GetOrCreateId("Where", analysis.Location);
            signature.When = _symbols.GetOrCreateId("When", analysis.Time);
            signature.Which = _symbols.GetOrCreateId("Which", analysis.Category);
            signature.Why = _symbols.GetOrCreateId("Why", analysis.Intent);
            signature.How = _symbols.GetOrCreateId("How", analysis.Manner);

            return signature;
        }
    }
}
