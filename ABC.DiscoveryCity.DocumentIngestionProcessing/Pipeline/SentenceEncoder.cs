using System;
using System.IO;
using ABC.DiscoveryCity.Embeddings;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline
{
    public class SentenceEncoder : ISemanticEncoder
    {
        private readonly SentenceQuantizer _quantizer;
        private readonly IEmbeddingService _embedding;

        // Initialize with default paths and dimensions
        public SentenceEncoder(string sqBinPath, IEmbeddingService embedding)
        {
            _quantizer = new SentenceQuantizer(16, 256, 64);
            
            if (File.Exists(sqBinPath))
            {
                _quantizer.Load(sqBinPath);
            }
            else
            {
                // Warn but do not crash during test builds if not trained yet
                Console.WriteLine($"[WARN] SQ Codebook not found at {sqBinPath}. Inference will yield zero-hashes until trained!");
            }
            
            _embedding = embedding;
        }

        public Guid QuantizeToGuid(string rawText)
        {
            if (_embedding == null) return Guid.Empty;

            float[] vector = _embedding.GetEmbeddingAsync(rawText).GetAwaiter().GetResult();
            
            // If codebook was intentionally missing/empty, just return empty to prevent crash
            if (_quantizer.Codebooks[0][0][0] == 0f && _quantizer.Codebooks[0][255][0] == 0f) return Guid.Empty;

            return _quantizer.Quantize(vector);
        }
    }
}
