using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Domain;
using Npgsql; // Nuget: Npgsql
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace ABC.DiscoveryCity.Services
{
    public class LocalIngestionService
    {
        private readonly string _connectionString;

        public LocalIngestionService(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task IngestBookAsync(int bookId, List<List<string>> dslChunks)
        {
            Console.WriteLine($"Starting Ingestion for Book {bookId} ({dslChunks.Count} chunks)...");

            using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // We use a Transaction to ensure the book is either fully in or fully out
            using var trans = await conn.BeginTransactionAsync();

            try
            {
                // OPTIONAL: Clear old chunks for this book if re-importing
                using (var cmdClear = new NpgsqlCommand("DELETE FROM smart_paragraphs WHERE book_id = @bid", conn, trans))
                {
                    cmdClear.Parameters.AddWithValue("bid", bookId);
                    await cmdClear.ExecuteNonQueryAsync();
                }

                // Prepare the INSERT statement
                // Note: We cast the parameter to jsonb explicitly
                string sql = @"
                    INSERT INTO smart_paragraphs (book_id, ordinal, dsl_text, embedding) 
                    VALUES (@bid, @ord, @dsl::jsonb, @vec)
                ";

                for (int i = 0; i < dslChunks.Count; i++)
                {
                    var chunk = dslChunks[i];

                    // 1. Serialize FULL Fidelity for the Database (Humans need the images!)
                    string jsonB = SerializeDslToJson(chunk);

                    // 2. Clean Text for the AI (Robots hate Base64)
                    // Pass the LIST 'chunk', not the string 'jsonB'
                    string cleanText = GetCleanTextForEmbedding(chunk);

                    // 3. Get Vector
                    //float[] vector = await _ai.GetEmbeddingAsync(cleanText);

                    float[] vector = new float[1024]; // Dummy 1024-dim vector for now

                    // 3. Write to DB
                    using (var cmd = new NpgsqlCommand(sql, conn, trans))
                    {
                        cmd.Parameters.AddWithValue("bid", bookId);
                        cmd.Parameters.AddWithValue("ord", i);
                        cmd.Parameters.AddWithValue("dsl", jsonB);

                        // PgVector requires the vector to be passed as a specific type or array
                        // Npgsql.EntityFrameworkCore.PostgreSQL.Vector handles this automatically,
                        // but raw Npgsql usually takes float[] fine if pgvector extension is loaded.
                        cmd.Parameters.AddWithValue("vec", vector);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    if (i % 50 == 0) Console.Write(".");
                }

                await trans.CommitAsync();
                Console.WriteLine("\nIngestion Complete!");
            }
            catch (Exception)
            {
                await trans.RollbackAsync();
                throw;
            }
        }

        // --- THE CRITICAL SERIALIZER ---
        // Converts List<string> to Valid JSONB Array: ["hello", "world.u"]
        private string SerializeDslToJson(List<string> tokens)
        {
            return System.Text.Json.JsonSerializer.Serialize(tokens);
        }
        private string GetCleanTextForEmbedding(List<string> chunk)
        {
            var sb = new StringBuilder();

            foreach (var token in chunk)
            {
                // Fast peek to skip Tags immediately
                // This removes <div id='juice...'>, <title>, <html> etc.
                if (token.StartsWith("<") || token.StartsWith("\"<")) continue;

                // Parse to check flags properly (handling implicit raw)
                // We pass '0' for index as we don't need context here
                var w = DslProtocol.Parse(token.AsSpan(), null, 0);

                // Double check: If it is a Tag or Raw Image, skip it.
                if ((w.Flags & WFlags.IsTag) != 0 || (w.Flags & WFlags.IsRaw) != 0)
                {
                    // Heuristic: If it's short raw text (like "&amp;"), maybe keep it?
                    // But usually for search, stripping all Raw is safer.
                    continue;
                }

                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w.Text.Span);
            }

            return sb.ToString();
        }
    }
}