using System.Text.Json.Serialization;

namespace ABC.DiscoveryCity.Models
{
    /// <summary>
    /// Represents the relationship between two documents (The threads of the web).
    /// </summary>
    public class DocumentLink
    {
        // 'source' and 'target' are strictly required string/int IDs by most JS graph renderers
        [JsonPropertyName("source")]
        public string SourceId { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string TargetId { get; set; } = string.Empty;

        // Optional: Can dictate the thickness or distance of the visual line
        [JsonPropertyName("value")]
        public int Weight { get; set; } = 1;

        // e.g., "Direct Reply", "Shared Author", "Semantic Similarity"
        [JsonPropertyName("relationshiptype")]
        public string RelationshipType { get; set; } = string.Empty;
    }
}
