using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using ABC.DiscoveryCity.Embeddings;

Console.WriteLine("=== ABC DiscoveryCity Semantic Training (Sentence Quantizer) ===");
Console.WriteLine("=== STRATIFIED SAMPLE SQ TRAINING ===");

string discoveryCityConn = "Host=192.168.1.114;Port=5435;Database=discoverycity;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo";
int limit = 100000;
string binPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sq_codebook.bin");

if (args.Any(a => a.StartsWith("--encode=")))
{
    var text = args.First(a => a.StartsWith("--encode=")).Substring(9);
    Console.WriteLine($"\n=== SQ COMPRESSION TEST ===");
    Console.WriteLine($"Loading Codebook from: {binPath}");
    
    if (!File.Exists(binPath))
    {
        Console.WriteLine("Codebook not found! Train it first.");
        return;
    }
    
    var quantizer = new ABC.DiscoveryCity.Embeddings.SentenceQuantizer(16, 256, 64);
    quantizer.Load(binPath);
    var service = new ABC.DiscoveryCity.Embeddings.OllamaEmbeddingService();
    
    Console.WriteLine($"Embedding text: '{text}' ...");
    var vector = service.GetEmbeddingAsync(text).GetAwaiter().GetResult();
    
    Console.WriteLine("\nRaw Vector Slice (First 5 dims):");
    Console.WriteLine($" {vector[0]:F4}, {vector[1]:F4}, {vector[2]:F4}, {vector[3]:F4}, {vector[4]:F4} ... (1024 total)");
    
    // Compress it!
    Guid signature = quantizer.Quantize(vector);
    byte[] bytes = signature.ToByteArray();
    
    Console.WriteLine("\nCompressed Signature (16 Bytes):");
    Console.WriteLine($" {signature}");
    
    // Reconstruct manually for MSE
    float[] decoded = new float[1024];
    for (int i = 0; i < 16; i++)
    {
        int centroidId = bytes[i];
        float[] cData = quantizer.Codebooks[i][centroidId];
        Array.Copy(cData, 0, decoded, i * 64, 64);
    }
    
    // Compare fidelity
    float mse = 0;
    for (int i = 0; i < 1024; i++) mse += (vector[i] - decoded[i]) * (vector[i] - decoded[i]);
    mse /= 1024f;
    
    Console.WriteLine($"\nRestored vector mean squared error: {mse:F5}");
    Console.WriteLine($"Compression Ratio: 4096 bytes -> 16 bytes! (99.6% Reduction)");
    return;
}

var embeddingService = new OllamaEmbeddingService();
var trainingSentences = new List<string>();

using (var conn = new NpgsqlConnection(discoveryCityConn))
{
    conn.Open();
    Console.WriteLine("Selecting 7000 diverse document sources...");
    var documentIds = new List<Guid>();
    using (var cmd = new NpgsqlCommand("SELECT Id FROM ParentDocuments WHERE Sentences IS NOT NULL AND jsonb_array_length(Sentences) > 10 ORDER BY RANDOM() LIMIT 7000", conn))
    using (var reader = cmd.ExecuteReader())
    {
        while (reader.Read()) documentIds.Add(reader.GetGuid(0));
    }

    if (documentIds.Count == 0)
    {
        Console.WriteLine("No documents found with sentences! Aborting.");
        return;
    }

    // For very small test batches, don't try to pull from 7000 docs.
    int docsToUse = Math.Min(documentIds.Count, limit);
    documentIds = documentIds.Take(docsToUse).ToList();

    int sentencesPerDoc = Math.Max(1, limit / documentIds.Count);
    Console.WriteLine($"Extracting ~{sentencesPerDoc} sentences from each of the {documentIds.Count} documents...");

    foreach (var docId in documentIds)
    {
        string jsonText = "";
        using (var cmd = new NpgsqlCommand("SELECT Sentences FROM ParentDocuments WHERE Id = @Id", conn))
        {
            cmd.Parameters.AddWithValue("Id", docId);
            var scalar = cmd.ExecuteScalar();
            if (scalar != DBNull.Value && scalar != null) jsonText = (string)scalar;
        }

        if (string.IsNullOrEmpty(jsonText)) continue;

        var sentences = JsonSerializer.Deserialize<List<string>>(jsonText);
        if (sentences == null || sentences.Count == 0) continue;

        // Random offset to skip Title Pages and Table of Contents
        int maxStart = Math.Max(0, sentences.Count - sentencesPerDoc - 5);
        int start = Random.Shared.Next(0, maxStart + 1);
        
        var slice = sentences.Skip(start).Take(sentencesPerDoc);
        
        foreach (var s in slice)
        {
            // Lightweight linguistic filter (prevents pure garbage from wasting a centroid)
            if (s.Length >= 40 && s.Length <= 800)
            {
                int letters = 0;
                foreach (char c in s) if (char.IsLetter(c)) letters++;
                double ratio = (double)letters / Math.Max(1, s.Length);
                
                if (ratio > 0.65) trainingSentences.Add(s);
            }
        }

        if (trainingSentences.Count >= limit) break;
        
        if (trainingSentences.Count % 5000 == 0 && trainingSentences.Count > 0)
            Console.WriteLine($"  Collected {trainingSentences.Count}/{limit} raw sentences...");
    }
}

Console.WriteLine($"\nSample built: {trainingSentences.Count} high-quality sentences.");
Console.WriteLine("Sending to local Ollama for embedding (this may take 15-30 minutes)...");

var vectors = new ConcurrentBag<float[]>();
int embedded = 0;

// We process symmetrically with Ollama's optimal batch concurrency
var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = 3 };
await Parallel.ForEachAsync(trainingSentences, parallelOptions, async (sentence, ct) =>
{
    try
    {
        var vec = await embeddingService.GetEmbeddingAsync(sentence);
        vectors.Add(vec);
        int count = Interlocked.Increment(ref embedded);
        if (count % 1000 == 0) Console.WriteLine($"   Embedded {count}/{trainingSentences.Count}...");
    }
    catch (Exception) { /* skip failures safely */ }
});

var finalVectors = vectors.ToList();
if (finalVectors.Count == 0)
{
    Console.WriteLine("Failed to generate any embeddings. Aborting.");
    return;
}

Console.WriteLine($"\nGenerated {finalVectors.Count} vectors (dimension {finalVectors[0].Length}). Starting SQ Training...");
var trainer = new SentenceTrainer();
var quant = trainer.Train(finalVectors, 16, 256);

quant.Save(binPath);
Console.WriteLine($"Training complete. Quantizer saved to {binPath}");
