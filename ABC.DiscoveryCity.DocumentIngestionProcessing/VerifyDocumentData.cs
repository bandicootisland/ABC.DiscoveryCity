using ABC.DiscoveryCity.PostgreSQL;
using Npgsql;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing
{
    public class VerifyDocumentData
    {
        public static async Task Run(DbService db)
        {
            string targetFile = "EFTA00072580.pdf";
            string searchTerm = "Prince Andrew";

            Console.WriteLine($"--- Verifying Data for {targetFile} ---");
            
            string connString = "Host=192.168.1.114;Port=5435;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo;Database=discoverycity";
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
            string sqlParent = "SELECT Id, COALESCE(FileName, FilePath, ''), Metadata FROM ParentDocuments WHERE FileName LIKE @path OR FilePath LIKE @path";
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

            // 2. Check Sentences JSONB for Text
            string sqlSentences = "SELECT Sentences FROM ParentDocuments WHERE Id = @pid";
            using var cmdSentences = new NpgsqlCommand(sqlSentences, connection);
            cmdSentences.Parameters.AddWithValue("pid", parentId);

            int sentenceCount = 0;
            bool termFound = false;

            using (var reader = await cmdSentences.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync() && !reader.IsDBNull(0))
                {
                    string sentencesJson = reader.GetString(0);
                    var sentences = System.Text.Json.JsonSerializer.Deserialize<List<string>>(sentencesJson) ?? new();
                    sentenceCount = sentences.Count;
                    for (int i = 0; i < sentences.Count; i++)
                    {
                        if (sentences[i].Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"[MATCH] Found '{searchTerm}' in Sentence {i}.");
                            Console.WriteLine($"Text Preview: {sentences[i].Substring(0, Math.Min(sentences[i].Length, 100))}...");
                            termFound = true;
                        }
                    }
                }
            }

            Console.WriteLine($"Total sentences: {sentenceCount}");

            if (!termFound)
            {
                Console.WriteLine($"[FAILURE] '{searchTerm}' was NOT found in any sentences for this document.");
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
                string fName = result.FileName;
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
