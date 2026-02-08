namespace ABC.DiscoveryCity.Embeddings;

/// <summary>
/// Interface for embedding generation services.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generate an embedding vector for the given text.
    /// </summary>
    Task<float[]> GetEmbeddingAsync(string text);

    /// <summary>
    /// The dimension size of embeddings produced (e.g. 384 for MiniLM).
    /// </summary>
    int Dimension { get; }
}
