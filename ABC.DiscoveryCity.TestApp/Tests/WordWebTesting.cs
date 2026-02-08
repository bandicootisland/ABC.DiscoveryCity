using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Structure;
using ABC.DiscoveryCity.Words.Structure;
using System.Diagnostics;

namespace ABC.DiscoveryCity.TestApp.Tests
{
    public class WordWebTesting
    {
        public static void Test(IEnumerable<WordGrammar> wordGrammar)
        {
            // Ensure words are loaded
            //ModestWords.LoadAll();

            Console.WriteLine("--- Word Web Testing ---");

            // 1. Initialization (At startup)
            WordWeb.Build(wordGrammar);

            // 2. Navigation
            var startWord = "run".ToConcept();

            if (startWord.IsValid)
            {
                Console.WriteLine($"Analysis for: {startWord.Text}");

                // Iterate Synonyms (Zero Alloc enumeration of Span)
                foreach (var syn in startWord.Synonyms)
                {
                    Console.WriteLine($"  - Synonym: {syn.Text}");
                }

                // Iterate Definitions
                foreach (var sense in startWord.Senses)
                {
                    // 'def' is your existing Sentence struct
                    Sentence def = sense.Definition;
                    Console.WriteLine($"  - Def: {def}");

                    // Deep Link: Check words INSIDE the definition
                    foreach (var innerWord in def.words)
                    {
                        var innerConcept = innerWord.ToConcept();
                        if (innerConcept.IsValid)
                        {
                            Console.WriteLine($"      -> Links to: {innerConcept.Text}");
                        }
                    }
                }
            }
            // "Give me all Nouns starting with 'S'"
            var results = DefinitionLibrary.LinearStore.Query("S", "noun");

            foreach (var entry in results)
            {
                Console.WriteLine($"{entry.Headword}: {entry.Senses[0].Id}");
                // No allocations here!
            }
        }


        public static async Task RunDemonstration(IEnumerable<WordGrammar> wordGrammar)
        {
            Console.WriteLine("--- 1. Generating Mock Dictionary Data ---");

            Console.WriteLine($"{wordGrammar.Count()} entries.");

            Console.WriteLine("\n--- 2. Building WordWeb (The Corpus) ---");

            var sw = Stopwatch.StartNew();

            // Run the build (Since it's CPU heavy, we can wrap it if running on UI thread)
            // Note: WordWeb.Build is synchronous because it manipulates static memory
            WordWeb.Build(wordGrammar);

            sw.Stop();
            Console.WriteLine($"WordWeb Built in {sw.ElapsedMilliseconds}ms.");

            Console.WriteLine("\n--- 3. Simulating User Document Analysis ---");

            // Imagine the user uploads a text file: "I want to run fast on foot."
            // We parse this into a Sentence (using your existing Sentence parser or a manual split)
            string fileContent = "I want to run fast on foot.";

            // Quick manual parse for the demo to get a Sentence struct
            var docWords = fileContent.Split(' ')
                                      .Select((txt, idx) => new ABC.DiscoveryCity.Words.Common.Word(txt.Trim('.'))) // Simple constructor
                                      .ToList();
            var userDoc = new Sentence(docWords);

            Console.WriteLine($"Document: \"{userDoc}\"");
            Console.WriteLine("Analyzing Links...");

            // Use the Async-Safe GetLinks() method
            foreach (var concept in userDoc.GetLinks())
            {
                Console.WriteLine($"\n[LINK FOUND]: '{concept.Text}'");

                // Show Definitions (Transplanted from the Corpus)
                var senses = concept.Senses;
                for (int i = 0; i < senses.Length; i++)
                {
                    var sense = senses[i];
                    Console.WriteLine($"  -> Def: {sense.Definition}");

                    // Show Synonyms (Logic Check)
                    if (!sense.Synonyms.IsEmpty)
                    {
                        Console.Write("  -> Synonyms: ");
                        foreach (var syn in sense.Synonyms) Console.Write(syn.Text + ", ");
                        Console.WriteLine();
                    }

                    // Deep Linking Check:
                    // Check if the definition ITSELF contains links (e.g., 'run' definition contains 'fast')
                    var deepLinks = sense.Definition.GetLinks();
                    foreach (var deepLink in deepLinks)
                    {
                        if (deepLink.Text.Equals(concept.Text, StringComparison.OrdinalIgnoreCase)) continue; // Skip self
                        Console.WriteLine($"     (Deep Link inside definition -> '{deepLink.Text}')");
                    }
                }
            }


            // 1. Helper Method
            void CheckDistance(string wordA, string wordB)
            {
                var c1 = wordA.ToConcept();
                var c2 = wordB.ToConcept();

                if (!c1.IsValid || !c2.IsValid)
                {
                    Console.WriteLine($"Cannot measure: '{wordA}' or '{wordB}' is not in the dictionary.");
                    return;
                }

                // Measure distance (Depth of 0 = same word, 1 = direct link, 2 = friend-of-friend)
                int dist = c1.GetSemanticDistance(c2, maxDepth: 5);

                string result = dist == -1 ? "No connection found" : dist.ToString();
                Console.WriteLine($"Distance '{wordA}' <-> '{wordB}': {result}");
            }

            // 2. Run Tests (after WordWeb.Build)
            Console.WriteLine("\n--- Testing Semantic Distance ---");

            CheckDistance("run", "sprint");  // Expect: 1 (Synonym)
            CheckDistance("run", "foot");    // Expect: 1 (Used in definition: "Move fast on foot")
            CheckDistance("run", "fast");    // Expect: 1 (Used in definition)
            CheckDistance("fast", "rapid");  // Expect: 1 (Synonym)
            CheckDistance("run", "rapid");   // Expect: 2 (Run -> Fast -> Rapid)
            CheckDistance("apple", "run");   // Expect: -1 (Unrelated)

        }

        
    }
}

