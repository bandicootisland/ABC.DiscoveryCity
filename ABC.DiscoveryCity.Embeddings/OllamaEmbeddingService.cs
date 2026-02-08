using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ABC.DiscoveryCity.Embeddings;

/// <summary>
/// Embedding service using Ollama's local API.
/// </summary>
public class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _modelName;

    /// <summary>
    /// Dimension of embeddings (384 for all-minilm, 768 for nomic-embed-text).
    /// </summary>
    public int Dimension { get; }

    /// <summary>
    /// Create an Ollama embedding service.
    /// </summary>
    /// <param name="httpClient">HttpClient (optional, creates one if null)</param>
    /// <param name="baseUrl">Ollama base URL (default: http://localhost:11434)</param>
    /// <param name="modelName">Model name (default: all-minilm)</param>
    /// <param name="dimension">Expected dimension (default: 384)</param>
    public OllamaEmbeddingService(
        HttpClient? httpClient = null, 
        string baseUrl = "http://localhost:11434",
        string modelName = "all-minilm:latest", 
        int dimension = 384)
    {
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri(baseUrl) };
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
        _modelName = modelName;
        Dimension = dimension;
    }

    /// <inheritdoc/>
    public async Task<float[]> GetEmbeddingAsync(string text)
    {
        // MiniLM has limited context window (~256 tokens), truncate to ~500 chars
        string truncatedText = text.Length > 500 ? text.Substring(0, 500) : text;
        
        var requestObj = new { model = _modelName, prompt = truncatedText };
        var jsonContent = new StringContent(
            JsonSerializer.Serialize(requestObj),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync("/api/embeddings", jsonContent);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"Ollama error {response.StatusCode}: {errorBody}");
        }

        var jsonString = await response.Content.ReadAsStringAsync();
        var ollamaResponse = JsonSerializer.Deserialize<OllamaResponse>(jsonString);

        if (ollamaResponse?.Embedding == null || ollamaResponse.Embedding.Length == 0)
        {
            throw new Exception("Ollama returned empty embedding.");
        }

        return ollamaResponse.Embedding;
    }

    private class OllamaResponse
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
