using ABC.DiscoveryCity.Models;

namespace ABC.DiscoveryCity.Services
{
    public class WeftHttpDataService : IWeftDataService
    {
        private readonly SearchService _searchService;
        private readonly UserListService _userListService;

        public WeftHttpDataService(SearchService searchService, UserListService userListService)
        {
            _searchService = searchService;
            _userListService = userListService;
        }

        public async Task<List<DocumentNode>> QueryDocumentsAsync(string query, HashSet<string> activeFacets)
        {
            try
            {
                // Intercept "Saved List" requests
                if (!string.IsNullOrWhiteSpace(query) && query.StartsWith("list:"))
                {
                    var listId = query.Substring(5);
                    var list = await _userListService.GetByIdAsync(listId);
                    if (list != null)
                    {
                        return list.Items.Select(item => new DocumentNode
                        {
                            Id = item.FilePath ?? Guid.NewGuid().ToString(),
                            Title = item.FileName,
                            FilePath = item.FilePath,
                            DataSetName = item.DataSetName,
                            DocumentType = item.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" :
                                          (item.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "Transcript" :
                                          (item.FileName.EndsWith(".msg", StringComparison.OrdinalIgnoreCase) ? "Email" : "Image")),
                            CreatedDate = item.Date ?? DateTime.UtcNow,
                            Author = "Unknown",
                            Snippet = "Loaded from Saved List: " + list.Name,
                            PageCount = item.PageCount,
                            RelevanceScore = (decimal)(item.Distance)
                        }).Where(n => activeFacets == null || activeFacets.Count == 0 || activeFacets.Contains(n.DocumentType)).ToList();
                    }
                    return new List<DocumentNode>();
                }
                
                // Normal query using SearchService
                var limit = 50;
                var refinedQuery = string.IsNullOrWhiteSpace(query) ? null : query;
                var searchResults = await _searchService.SearchPagedAsync(refinedQuery, 0, limit, false, null, null);
                
                return searchResults.Items.Select(r => new DocumentNode
                {
                    Id = r.FilePath ?? Guid.NewGuid().ToString(),
                    Title = r.FileName,
                    FilePath = r.FilePath,
                    ThumbnailPath = r.FullImagePath ?? r.ThumbnailPath,
                    SourceUrl = r.SourceUrl,
                    DataSetName = r.DataSetName,
                    DocumentType = r.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" :
                                  (r.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "Transcript" :
                                  (r.FileName.EndsWith(".msg", StringComparison.OrdinalIgnoreCase) ? "Email" : "Image")),
                    CreatedDate = r.Date ?? DateTime.UtcNow,
                    Author = "Unknown",
                    Snippet = r.Text,
                    PageCount = r.PageCount,
                    RelevanceScore = (decimal)(Math.Max(0, 1.0 - r.Distance))
                }).Where(n => activeFacets == null || activeFacets.Count == 0 || activeFacets.Contains(n.DocumentType)).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Dæmask Architect] Error mapping documents: {ex.Message}");
                return new List<DocumentNode>();
            }
        }

        public async Task<List<DocumentLink>> GetRelationshipsAsync(IEnumerable<DocumentNode> currentNodes)
        {
            if (!currentNodes.Any()) return new List<DocumentLink>();

            // Provide visual grouping relations for the Graph View since real relationships aren't supported yet
            var nodes = currentNodes.ToList();
            var links = new List<DocumentLink>();
            var random = new Random();

            for (int i = 0; i < nodes.Count; i++)
            {
                // Give it some visual flair by connecting to randomly near nodes
                var connections = random.Next(1, 3);
                for (int c = 0; c < connections; c++)
                {
                    var targetIndex = random.Next(nodes.Count);
                    if (targetIndex != i)
                    {
                        links.Add(new DocumentLink
                        {
                            SourceId = nodes[i].Id,
                            TargetId = nodes[targetIndex].Id,
                            Weight = random.Next(1, 5) // Random weight 1 - 4
                        });
                    }
                }
            }
            
            return await Task.FromResult(links);
        }
    }
}
