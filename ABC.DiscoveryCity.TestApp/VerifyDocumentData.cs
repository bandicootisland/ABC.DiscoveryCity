using ABC.DiscoveryCity.PostgreSQL;
using Npgsql;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ABC.DiscoveryCity.TestApp
{
    public class VerifyDocumentData
    {
        public static async Task Run(DbService db)
        {
            string targetFile = "EFTA00072580.pdf";
            string searchTerm = "Prince Andrew";

            Console.WriteLine($"--- Verifying Data for {targetFile} ---");
            
            string connString = "Host=192.168.1.114;Port=5435;Username=discovery_user;Password=q1W@e3R$;Database=discoverycity";
            await using var connection = new NpgsqlConnection(connString);
            await connection.OpenAsync();

            Console.WriteLine("Connected to DB.");

            // Check Vector Extension Version
            using (var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector';", connection))
            {
                var version = await cmd.ExecuteScalarAsync();
                Console.WriteLine($"Pgvector Extension Version: {version}");
                
                // Try Update
                using (var updateCmd = new NpgsqlCommand("ALTER EXTENSION vector UPDATE;", connection))
                {
                    try { 
                        await updateCmd.ExecuteNonQueryAsync(); 
                        Console.WriteLine("pgvector extension updated (or already latest).");
                    } catch (Exception ex) {
                         Console.WriteLine($"Warning: Could not update pgvector: {ex.Message}");
                    }
                }
            }

            // 1. Find Parent ID
            string sqlParent = "SELECT Id, FilePath, Metadata FROM ParentDocuments WHERE FilePath LIKE @path";
            using var cmdParent = new NpgsqlCommand(sqlParent, connection);
            cmdParent.Parameters.AddWithValue("path", $"%{targetFile}%");
            
            int parentId = -1;
            using (var reader = await cmdParent.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    parentId = reader.GetInt32(0);
                    string path = reader.GetString(1);
                    Console.WriteLine($"[FOUND] Parent Document ID: {parentId}");
                    Console.WriteLine($"Path: {path}");
                }
                else
                {
                    Console.WriteLine($"[ERROR] Document {targetFile} NOT FOUND in ParentDocuments table.");
                    return;
                }
            }

            // 2. Check Chunks for Text
            string sqlChunks = "SELECT ChunkIndex, TextContent FROM DocumentChunks WHERE ParentId = @pid ORDER BY ChunkIndex";
            using var cmdChunks = new NpgsqlCommand(sqlChunks, connection);
            cmdChunks.Parameters.AddWithValue("pid", parentId);

            int chunkCount = 0;
            bool termFound = false;

            using (var reader = await cmdChunks.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    chunkCount++;
                    string text = reader.GetString(1);
                    if (text.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"[MATCH] Found '{searchTerm}' in Chunk {reader.GetInt32(0)}.");
                        Console.WriteLine($"Text Preview: {text.Substring(0, Math.Min(text.Length, 100))}...");
                        termFound = true;
                    }
                }
            }

            if (!termFound)
            {
                Console.WriteLine($"[FAILURE] '{searchTerm}' was NOT found in any text chunks for this document.");
            }
            else
            {
                 Console.WriteLine($"[SUCCESS] '{searchTerm}' IS present in the database.");
            }
            
            // 3. Test Search Ranking
            Console.WriteLine($"\n--- Testing Search Ranking for '{searchTerm}' ---");
            var results = await db.SearchSimilarAsync(searchTerm, limit: 20); // Increase limit to see if it's just pushed down
            
            bool foundInSearch = false;
            int rank = 1;
            foreach (var result in results)
            {
                string fName = Path.GetFileName(result.FilePath);
                Console.WriteLine($"Rank {rank}: {fName} (Dist: {result.Distance:F4})");
                if (fName.Contains("EFTA00072580"))
                {
                    Console.WriteLine($"[FOUND] Target document found at Rank {rank}!");
                    foundInSearch = true;
                }
                rank++;
            }
            
            if (!foundInSearch)
            {
                Console.WriteLine($"[WARNING] Target document NOT found in top 20 search results for '{searchTerm}'.");
                // Check distance of the target document manually?
                // We'd need to embed 'Prince Andrew' and compute distance to chunks of EFTA00072580 manually or via SQL
            }
        }
    }
}
