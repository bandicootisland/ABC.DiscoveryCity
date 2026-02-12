using ABC.DiscoveryCity.Services;
using ABC.DiscoveryCity.Words.Common;
using System;
using System.Collections.Generic;
using System.Text;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Tests
{
    public class BookIngestionTests
    {
        
        public static void RunParseBook(BookContent book)
        {

            // ACT: Parse the book
            List<List<string>> bookStructure = BookProcessor.ParseBook(book);

            // ASSERT: Visualize
            Console.WriteLine($"\nTotal Chunks: {bookStructure.Count}");

            for (int i = 0; i < bookStructure.Count; i++)
            {
                var chunk = bookStructure[i];
                Console.WriteLine($"\n--- CHUNK {i} ({chunk.Count} tokens) ---");

                // Print as a JSON-like array for inspection
                Console.Write("[");
                for (int j = 0; j < Math.Min(chunk.Count, 500_000); j++) // Show first 500,000 words
                {
                    Console.Write($"\"{chunk[j]}\", ");
                }
                Console.WriteLine("...]");
            }
        }
        public static async Task RunFullImport(BookContent myBook)
        {
            // 1. Parse (CPU Bound)
            // "Clean the text, strip HTML noise, fix casing"
            var chunks = BookProcessor.ParseBook(myBook);

            // 2. Ingest (IO Bound)
            // "Serialize to JSON, (Calculate Vectors), Write to Postgres"
            var ingestion = new LocalIngestionService("Host=localhost;Port=5433;Database=wordcity;Username=wordcity;Password=wordcity_dev");
            await ingestion.IngestBookAsync(101, chunks);
        }
    }
}
