using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dapper; // Nuget: Dapper
using Npgsql;

namespace ABC.DiscoveryCity.Services
{
    public class SearchService
    {
        private readonly string _connString;

        // Inject your embedding service (OpenAI/Ollama) here
        // private readonly IEmbeddingService _ai; 

        public SearchService(string connString)
        {
            _connString = connString;
        }

        public async Task<List<SearchResult>> SearchAsync(string query, int bookId)
        {
            // 1. Get Query Vector (Placeholder)
            // float[] queryVector = await _ai.GetVectorAsync(query);
            float[] queryVector = new float[1536];

            using var conn = new NpgsqlConnection(_connString);

            // 2. Perform Vector Search
            // We find the top match, then join with neighbors to get context (Ordinal +/- 1)
            // This query fetches the "Target" paragraph AND its neighbors in one go.
            string sql = @"
                WITH center AS (
                    SELECT id, book_id, ordinal, dsl_text, 
                           1 - (embedding <=> @vec) as similarity
                    FROM smart_paragraphs
                    WHERE book_id = @bid
                    ORDER BY embedding <=> @vec
                    LIMIT 1
                ),
                window AS (
                    SELECT p.id, p.ordinal, p.dsl_text, 
                           c.similarity as center_score,
                           (p.ordinal - c.ordinal) as offset
                    FROM smart_paragraphs p
                    JOIN center c ON p.book_id = c.book_id
                    WHERE p.ordinal BETWEEN c.ordinal - 1 AND c.ordinal + 1
                )
                SELECT * FROM window ORDER BY ordinal;
            ";

            var rows = await conn.QueryAsync(sql, new { bid = bookId, vec = queryVector });

            // 3. Reconstruct
            var results = new List<SearchResult>();

            // Group by the 'center' logic if you were fetching multiple hits.
            // Since we fetched one window, we just render it.
            var combinedHtml = new System.Text.StringBuilder();

            foreach (var row in rows)
            {
                // Deserialize JSONB -> List<string>
                var dsl = JsonSerializer.Deserialize<List<string>>(row.dsl_text as string);

                // Render to HTML using the Renderer we built
                string html = DslRenderer.RenderToHtml(dsl);

                combinedHtml.Append(html);

                // Add a space between paragraphs if needed, or rely on HTML tags
                if (!html.EndsWith(">")) combinedHtml.Append(' ');
            }

            results.Add(new SearchResult
            {
                Html = combinedHtml.ToString(),
                Score = rows.First().center_score
            });

            return results;
        }
    }

    public class SearchResult
    {
        public string Html { get; set; }
        public double Score { get; set; }
    }
}