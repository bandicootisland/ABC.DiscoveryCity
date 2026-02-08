namespace ABC.DiscoveryCity.PostgreSQL;

/// <summary>
/// Configuration settings for PostgreSQL connection and vector operations.
/// </summary>
public class PostgreSQLConfig
{
    /// <summary>
    /// Connection string for the WordCity PostgreSQL database.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Dimension of vector embeddings (default 384 for MiniLM).
    /// </summary>
    public int VectorDimension { get; set; } = 384;

    /// <summary>
    /// Command timeout in seconds for long-running operations.
    /// </summary>
    public int CommandTimeout { get; set; } = 300;

    /// <summary>
    /// Maximum connection pool size.
    /// </summary>
    public int MaxPoolSize { get; set; } = 20;
}
