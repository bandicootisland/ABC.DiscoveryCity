using System;
using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Domain;
using ABC.DiscoveryCity.Words.Common.Structure; // For SentenceData if needed

namespace ABC.DiscoveryCity.TestApp.Tests
{
    public static class DslProtocolTest
    {
        public static void Run()
        {
            Console.WriteLine("=== DSL PROTOCOL TEST ===");

            TestParsing();
            TestCompilation();
            TestRoundTrip();
            TestSpacingLogic();
        }

        private static void TestParsing()
        {
            Console.WriteLine("\n--- Parsing Tests ---");

            // 1. Simple word (Default: Append Space)
            // Note: Parse logic returns a Word. 
            // Word.ToString() *now* appends a space by default unless NoSpace is set.
            VerifyParse("hello", "hello "); 

            // 2. Formatting (.u)
            // "hello.u" -> UpperFirst -> "Hello "
            VerifyParse("hello.u", "Hello ");

            // 3. Formatting (.U)
            // "hello.U" -> UpperAll -> "HELLO "
            VerifyParse("hello.U", "HELLO ");

            // 4. NoSpace (.n)
            // "hello.n" -> "hello" (No trailing space)
            VerifyParse("hello.n", "hello");

            // 5. Punctuation (.e -> .)
            // "end.e" -> "end. "
            VerifyParse("end.e", "end. ");

            // 6. Punctuation + NoSpace (.e.n) - Order doesn't matter for flags, but parsing eats suffixes R-to-L
            // "end.e.n" -> "end." (No trailing space)
            VerifyParse("end.e.n", "end.");

            // 7. Question Mark (.q -> ?)
            VerifyParse("why.q", "why? ");

            // 8. Complex (.u.c) -> UpperFirst + Comma
            // "hello.u.c" -> "Hello, "
            VerifyParse("hello.u.c", "Hello, ");
        }

        private static void VerifyParse(string input, string expectedOutput)
        {
            // Null context ok for test? 
            // Word constructor allows null context, but Parse passes 'null'.
            // Let's ensure Safe parsing.
            var w = DslProtocol.Parse(input.AsSpan(), null, 0);
            string actual = w.ToString();

            if (actual == expectedOutput)
            {
                Console.WriteLine($"[PASS] '{input}' -> '{actual}'");
            }
            else
            {
                Console.WriteLine($"[FAIL] '{input}' -> Expected '{expectedOutput}', Got '{actual}'");
               // Console.WriteLine($"       Raw Text: '{w.text}', Flags: {w.Flags}, Punct: {(int)w.Punctuation}");
            }
        }

        private static void TestCompilation()
        {
            Console.WriteLine("\n--- Compilation Tests ---");

            // 1. Simple
            VerifyCompile("hello", WFlags.None, '\0', "hello");

            // 2. Upper First
            VerifyCompile("Hello", WFlags.UpperFirst, '\0', "hello.u");

            // 3. No Space
            //VerifyCompile("anti-", WFlags.NoSpace, '\0', "anti-.n"); // assuming '-' is part of text

            // 4. Dot
            VerifyCompile("end", WFlags.None, '.', "end.e"); // Dot -> .e

             // 5. Question + NoSpace
            //VerifyCompile("what", WFlags.NoSpace, '?', "what.q.n");
        }

        private static void VerifyCompile(string text, WFlags flags, char punct, string expectedDsl)
        {
            string actual = DslProtocol.Compile(text, flags, punct);
            if (actual == expectedDsl)
            {
                Console.WriteLine($"[PASS] '{text}' + Flags -> '{actual}'");
            }
            else
            {
                Console.WriteLine($"[FAIL] '{text}' -> Expected '{expectedDsl}', Got '{actual}'");
            }
        }

        private static void TestRoundTrip()
        {
             Console.WriteLine("\n--- Round Trip Tests ---");
             string[] inputs = { "hello", "World.u", "USA.U", "glue.n", "end.e", "what.q", "run.x.n" };

             foreach(var input in inputs)
             {
                 var w = DslProtocol.Parse(input.AsSpan(), null, 0);
                 
                 // Re-compile logic is separate from Word struct, 
                 // we need to see if Compile(w.text, w.Flags, w.Punctuation) returns the input (mostly).
                 // Note: Input "World.u" -> Parse -> text="world", Flags=UpperFirst.
                 // Compile("world", UpperFirst) -> "world.u".
                 
                 // However, normalization happens. "Hello" -> Parse -> "hello" + UpperFirst.
                 // So we can't compare 'w.text' to 'input' directly.
                 
                 //string compiled = DslProtocol.Compile(w.text, w.Flags, w.Punctuation);
                 
                 //// Strict equality might fail on order (.n.q vs .q.n) if implementation differs, 
                 //// but DslProtocol implementation seems deterministic.

                 //if (compiled == input || (input == "World.u" && compiled == "world.u")) // minor case normalization check
                 //{
                 //     Console.WriteLine($"[PASS] RoundTrip '{input}'");
                 //}
                 //else
                 //{
                 //    // Allow for case normalization differences (input "World.u" is valid DSL, but usually we store "world.u")
                 //    if (input.ToLowerInvariant() == compiled.ToLowerInvariant())
                 //    {
                 //        Console.WriteLine($"[PASS] RoundTrip '{input}' (Case Normalized)");
                 //    }
                 //    else
                 //    {
                 //       Console.WriteLine($"[FAIL] RoundTrip '{input}' -> '{compiled}'");
                 //    }
                 //}
             }
        }

        private static void TestSpacingLogic()
        {
             Console.WriteLine("\n--- Spacing Logic Tests ---");
             
             // Check the implicit behavior of Word struct
             var w1 = new Word("hello"); // No flags
             if (!w1.ToString().EndsWith(" "))
             {
                 Console.WriteLine("[FAIL] Default word should have space.");
             }
             else
             {
                  Console.WriteLine("[PASS] Default word has space.");
             }

             //var w2 = w1.WithFlags(WFlags.NoSpace);
             //if (w2.ToString().EndsWith(" "))
             //{
             //    Console.WriteLine("[FAIL] NoSpace flag should suppress space.");
             //}
             //else
             //{
             //     Console.WriteLine("[PASS] NoSpace flag suppressed space.");
             //}
        }
    }
}
