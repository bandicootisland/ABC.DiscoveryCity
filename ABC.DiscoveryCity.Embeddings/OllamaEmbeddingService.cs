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
    /// Dimension of embeddings (1024 for mxbai-embed-large, 384 for all-minilm).
    /// </summary>
    public int Dimension { get; }

    /// <summary>
    /// Create an Ollama embedding service.
    /// </summary>
    /// <param name="httpClient">HttpClient (optional, creates one if null)</param>
    /// <param name="baseUrl">Ollama base URL (default: http://localhost:11434)</param>
    /// <param name="modelName">Model name (default: mxbai-embed-large)</param>
    /// <param name="dimension">Expected dimension (default: 1024)</param>
    public OllamaEmbeddingService(
        HttpClient? httpClient = null,
        string baseUrl = "http://localhost:11434",
        string modelName = "mxbai-embed-large:latest",
        int dimension = 1024)
    {
        _httpClient = httpClient ?? new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(300) };
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
        // Start with generous limit, retry with progressively shorter text on context overflow
        int maxChars = 2000;

        while (maxChars >= 200)
        {
            string truncatedText = text.Length > maxChars ? text.Substring(0, maxChars) : text;

            var requestObj = new { model = _modelName, prompt = truncatedText };
            var jsonContent = new StringContent(
                JsonSerializer.Serialize(requestObj),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("/api/embeddings", jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                if (errorBody.Contains("context length", StringComparison.OrdinalIgnoreCase))
                {
                    maxChars /= 2;
                    continue;
                }
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

        throw new Exception($"Text too dense to embed even at {maxChars * 2} chars.");
    }

    private class OllamaResponse
    {
        [JsonPropertyName("embedding")]
        public float[]? Embedding { get; set; }
    }
}
