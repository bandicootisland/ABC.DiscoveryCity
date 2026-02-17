namespace ABC.DiscoveryCity.Models
{
    

/// <summary>
    /// The exact payload shape we serialize and push across the JS Interop barrier.
    /// </summary>
    public class GraphPayload
    {
        
        public IEnumerable<DocumentNode> nodes { get; set; } = new List<DocumentNode>();

        
        public IEnumerable<DocumentLink> links { get; set; } = new List<DocumentLink>();
    }
}