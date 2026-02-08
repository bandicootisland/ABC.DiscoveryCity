using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Structure;
using System;

namespace ABC.DiscoveryCity.Words.Common.Structure
{
public static class GraphExtensions
{
    public static int GetSemanticDistance(this Concept source, Concept target, int maxDepth = 3)
    {
        // 1. Basic Checks
        if (!source.IsValid || !target.IsValid) return -1;
        if (source.Id == target.Id) return 0;
    
        // 2. Setup BFS
        // Visited set prevents loops (A -> B -> A)
        var visited = new HashSet<int>();
        var queue = new Queue<(int id, int depth)>();
    
        visited.Add(source.Id);
        queue.Enqueue((source.Id, 0));

        // 3. BFS Loop
        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            
            // Stop if we've reached the depth limit
            if (depth >= maxDepth) continue;

            // --- COLLECT NEIGHBORS ---

            // A. GLOBAL SYNONYMS (Headword Level)
            var globalSyns = WordWeb.GetSynonyms(currentId);
            for (int i = 0; i < globalSyns.Length; i++)
            {
                if (CheckNeighbor(globalSyns[i].Id, target.Id, depth, visited, queue)) 
                    return depth + 1;
            }

            // B. SENSES (Definitions + Sense Synonyms)
            var senses = WordWeb.GetSenses(currentId);
            for (int i = 0; i < senses.Length; i++)
            {
                var sense = senses[i];

                // 1. SENSE SYNONYMS (*** THE FIX ***)
                // This was missing before. It links "Run" -> "Sprint".
                var senseSyns = sense.Synonyms;
                for (int k = 0; k < senseSyns.Length; k++)
                {
                    if (CheckNeighbor(senseSyns[k].Id, target.Id, depth, visited, queue)) 
                        return depth + 1;
                }

                // 2. DEFINITION WORDS
                // We look at words used inside the definition.
                foreach (var link in sense.Definition.GetLinks())
                {
                    // HEURISTIC: Ignore 1-letter words ("a", "I", "s") to prevent 
                    // meaningless "Apple -> A -> Run" connections.
                    if (link.Text.Length < 2) continue;

                    if (CheckNeighbor(link.Id, target.Id, depth, visited, queue)) 
                        return depth + 1;
                }
            }
        }

        return -1; // No path found
    }

    // Inline Helper
    private static bool CheckNeighbor(int neighborId, int targetId, int currentDepth, HashSet<int> visited, Queue<(int, int)> queue)
    {
        if (neighborId == targetId) return true; // Found!

        if (visited.Add(neighborId))
        {
            queue.Enqueue((neighborId, currentDepth + 1));
        }
        return false;
    }
}
}