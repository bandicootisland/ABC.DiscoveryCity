using System.Text.Json.Serialization;

namespace ABC.DiscoveryCity.Models
{
    /// <summary>
    /// Represents a single document. This serves as a row in the Grid,
    /// a card in the Pivot, and a vertex in the Graph.
    /// </summary>
    public class DocumentNode
    {
        // Required by 3d-force-graph for node identity
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("name")]
        public string Title { get; set; } = string.Empty;

        // Grouping maps to node colors in the graph and categories in the Pivot
        [JsonPropertyName("group")]
        public string DocumentType { get; set; } = string.Empty;

        // Additional Metadata for the Grid and Pivot Facets
        [JsonPropertyName("createddate")]
        public DateTime CreatedDate { get; set; }

        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        [JsonPropertyName("snippet")]
        public string Snippet { get; set; } = string.Empty;

        [JsonPropertyName("pagecount")]
        public int PageCount { get; set; }

        [JsonPropertyName("relevancescore")]
        public decimal RelevanceScore { get; set; }
    }
}
