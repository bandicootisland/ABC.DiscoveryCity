using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ABC.DiscoveryCity.Models;

namespace ABC.DiscoveryCity.Services
{
    public class MockWeftDataService : IWeftDataService
    {
        public Task<List<DocumentNode>> QueryDocumentsAsync(string query, HashSet<string> activeFacets)
        {
            var docs = new List<DocumentNode>();
            var types = new[] { "PDF", "Email", "Image", "Transcript" };
            var random = new Random();

            // Generate 50 dummy nodes to populate the views
            for (int i = 1; i <= 50; i++)
            {
                var type = types[random.Next(types.Length)];
                
                // Obey the UI's active facets
                if (activeFacets != null && activeFacets.Any() && !activeFacets.Contains(type))
                    continue;

                // Simple search filter simulation
                if (!string.IsNullOrWhiteSpace(query) && !$"Document {i}".Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                docs.Add(new DocumentNode
                {
                    Id = $"doc-{i}",
                    Title = $"Weft Thread Document {i}",
                    DocumentType = type,
                    CreatedDate = DateTime.Now.AddDays(-random.Next(1, 365)),
                    Author = $"Analyst_{random.Next(1, 5)}",
                    Snippet = "Sample extracted text block demonstrating contextual relevance...",
                    PageCount = random.Next(1, 50),
                    RelevanceScore = (decimal)Math.Round(random.NextDouble(), 2)
                });
            }
            return Task.FromResult(docs);
        }

        public Task<List<DocumentLink>> GetRelationshipsAsync(IEnumerable<DocumentNode> currentNodes)
        {
            var links = new List<DocumentLink>();
            var nodes = currentNodes.ToList();
            var random = new Random();

            // Create synthetic topological webbing between the currently visible nodes
            for (int i = 0; i < nodes.Count; i++)
            {
                // Link each node to 1-3 other random nodes in the current view
                int linkCount = random.Next(1, 4);
                for (int j = 0; j < linkCount; j++)
                {
                    var targetNode = nodes[random.Next(nodes.Count)];
                    
                    // Prevent self-referencing links
                    if (nodes[i].Id != targetNode.Id)
                    {
                        links.Add(new DocumentLink
                        {
                            SourceId = nodes[i].Id,
                            TargetId = targetNode.Id,
                            Weight = random.Next(1, 5),
                            RelationshipType = "Semantic Match"
                        });
                    }
                }
            }
            return Task.FromResult(links);
        }
    }
}