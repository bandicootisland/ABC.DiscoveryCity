using ABC.DiscoveryCity.Models;

namespace ABC.DiscoveryCity.Services;

    public interface IWeftDataService
    {
        /// <summary>
        /// Retrieves the primary subset of documents based on search and active facets.
        /// </summary>
        Task<List<DocumentNode>> QueryDocumentsAsync(string query, HashSet<string> activeFacets);

        /// <summary>
        /// Given a set of documents, asks the Weft engine to map out how they connect to each other.
        /// </summary>
        Task<List<DocumentLink>> GetRelationshipsAsync(IEnumerable<DocumentNode> currentNodes);
    }
