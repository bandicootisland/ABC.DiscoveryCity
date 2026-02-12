//using System;
//using System.Collections.Generic;
//using System.Text;

//namespace ABC.DiscoveryCity.DocumentIngestionProcessing
//{
//    using ABC.BookPack.Dictionaries;
//    using ABC.DiscoveryCity.Words.Common;
//    using System;
//    using System.Diagnostics;
//    using System.Reflection;

//    public static class DictionaryBenchmark
//    {
//        public static void Run(string filePath)
//        {
//            Console.WriteLine($"Reading file: {filePath}...");

//            // 1. Read Raw String (we don't time disk I/O, just parsing)
//            string bigHtml = System.IO.File.ReadAllText(filePath);
//            ReadOnlyMemory<char> memory = bigHtml.AsMemory();

//            Console.WriteLine($"File Length: {memory.Length:N0} characters");
//            Console.WriteLine("Starting Benchmark...");
//            Console.WriteLine(new string('-', 40));

//            // Force cleanup before starting to get a clean baseline
//            GC.Collect();
//            GC.WaitForPendingFinalizers();
//            GC.Collect();

//            long memStart = GC.GetTotalMemory(true);
//            int gc0Start = GC.CollectionCount(0);
//            int gc1Start = GC.CollectionCount(1);
//            int gc2Start = GC.CollectionCount(2);

//            var stopwatch = Stopwatch.StartNew();

//            // --- THE WORK ---
//            //var (words, entries) = CollinsDictionaryLoader.LoadCollinsDictionary();
//            // ----------------

//            stopwatch.Stop();

//            long memEnd = GC.GetTotalMemory(false); // Don't force GC here, we want to see the load
//            int gc0End = GC.CollectionCount(0);

//            // --- REPORTING ---
//            Console.WriteLine($"Time Taken:   {stopwatch.Elapsed.TotalMilliseconds:N0} ms");
//            Console.WriteLine($"Memory Used:  {(memEnd - memStart) / 1024 / 1024} MB (Approx increase)");
//            Console.WriteLine($"GC Gen 0:     {gc0End - gc0Start} (Lower is better)");

//            Console.WriteLine(new string('-', 40));
//            Console.WriteLine("STATS:");
//            Console.WriteLine($"Total Words:      {CollinsDictionaryLoader.Words.Length:N0}");
//            //Console.WriteLine($"Total Sentences:  {DictionaryLoader.Sentences.Length:N0}");
//            Console.WriteLine($"Vocabulary Size:  {entries.Count:N0} (Unique words)");

//            // Reflection hack to see how big the internal String Pool is
//            var poolField = typeof(StringCache).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Static);
//            var pool = poolField.GetValue(null) as System.Collections.Generic.Dictionary<string, string>;
//            Console.WriteLine($"String Pool Size: {pool?.Count:N0} unique strings cached");

//            double compressionRatio = (double)pool.Count / CollinsDictionaryLoader.Words.Length * 100;
//            Console.WriteLine($"Compression:      Only storing {compressionRatio:F2}% of string objects");
//        }

//        public static void DebugEntry(string targetWord, string filePath)
//        {
//            var engine = new CollinsDictionaryEngine();
//            Console.WriteLine("Loading...");
//            string rawHtml = CollinsDictionaryLoader.LoadCollinsDictionaryHtml(filePath);
//            var (words, sentences) = CollinsDictionaryLoader.LoadDictionaryHtml(rawHtml.AsMemory());

//            // Find the specific entry            
//            int startIndex = -1;

//            for (int i = 0; i < words.Length; i++)
//            {
//                if (words[i].text.Equals(targetWord, StringComparison.OrdinalIgnoreCase))
//                {
//                    // Backtrack to find <p>
//                    for (int k = i; k > Math.Max(0, i - 50); k--)
//                    {
//                        if (words[k].text.StartsWith("<p", StringComparison.OrdinalIgnoreCase))
//                        {
//                            startIndex = k;
//                            break;
//                        }
//                    }
//                    break;
//                }
//            }

//            if (startIndex != -1)
//            {
//                Console.WriteLine($"\n--- DEBUGGING TOKENS FOR: {targetWord} ---");
//                // Print next 100 tokens
//                for (int i = startIndex; i < startIndex + 100; i++)
//                {
//                    var w = words[i];
//                    string type = w.text.StartsWith("<") ? "TAG" : "TXT";
//                    // Check filters
//                    bool isContent = w.IsContent();

//                    Console.WriteLine($"[{i}] {type}: '{w.text}' (IsContent: {isContent})");
//                }
//            }
//        }
//    }
//}