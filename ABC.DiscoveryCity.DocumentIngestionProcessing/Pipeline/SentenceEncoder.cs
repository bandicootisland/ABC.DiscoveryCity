using System.IO.Compression;
using ABC.DiscoveryCity.Embeddings;
using ABC.DiscoveryCity.PostgreSQL;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline
{
    public class SentenceEncoder : ISemanticEncoder
    {
        private readonly SentenceQuantizer _quantizer;
        private readonly IEmbeddingService _embedding;

        // Initialize with optional DB storage and fallback file path
        public SentenceEncoder(string sqBinPath, IEmbeddingService embedding, DictionaryStorageService? dictStorage = null)
        {
            _quantizer = new SentenceQuantizer(16, 256, 64);
            bool loaded = false;

            // 1. Try to load from Database (XLSX package)
            if (dictStorage != null)
            {
                try
                {
                    byte[]? package = dictStorage.LoadDictionaryPackage("sq_codebook");
                    if (package != null)
                    {
                        using var ms = new MemoryStream(package);
                        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                        var entry = zip.GetEntry("data/sq_codebook.bin");
                        if (entry != null)
                        {
                            using var s = entry.Open();
                            _quantizer.Load(s);
                            loaded = true;
                            Console.WriteLine("[INFO] SQ Codebook loaded from Database package.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to load SQ Codebook from DB: {ex.Message}");
                }
            }

            // 2. Fallback to local file
            if (!loaded && File.Exists(sqBinPath))
            {
                try
                {
                    _quantizer.Load(sqBinPath);
                    loaded = true;
                    Console.WriteLine($"[INFO] SQ Codebook loaded from local file: {sqBinPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERROR] Failed to load SQ Codebook from disk: {ex.Message}");
                }
            }

            if (!loaded)
            {
                Console.WriteLine($"[WARN] SQ Codebook NOT FOUND in DB or at {sqBinPath}. Inference will yield zero-hashes until trained!");
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
