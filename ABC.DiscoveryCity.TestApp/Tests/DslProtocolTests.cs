using System;
using System.Text;
using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Domain;
using ABC.DiscoveryCity.Words.Common.Structure; // Your Namespace




namespace ABC.DiscoveryCity.TestApp.Tests
{
    public class DslProtocolTests
    {

        public void Run() {


            try
            {
                // 1. Basic Standard Tests
                Test_Compile("Hello", WFlags.UpperFirst, '\0', "hello.u");
                Test_Compile("USA", WFlags.UpperAll, '\0', "usa.U");
                Test_Compile("world", WFlags.None, '.', "world.e");

                // 2. Parser Logic Tests (Round Trip)
                Test_Parse("hello.u", "Hello", WFlags.UpperFirst);
                Test_Parse("list.c", "list", WFlags.None, ',');

                // 3. The New "Stacking" Logic (.uc, .qo)
                // "Hello," -> UpperFirst + Comma
                Test_Parse_Details("hello.uc", "hello", WFlags.UpperFirst, ',');

                // Dialogue: "Hello" -> QuoteOpen + UpperFirst
                Test_Parse_Details("hello.uqo", "hello", WFlags.UpperFirst | WFlags.QuoteOpen, '\0');

                // 4. Atomic Flags (Glue)
                // "Anti-" -> UpperFirst + NoSpace
                Test_Parse_Details("anti.uns", "anti", WFlags.UpperFirst | WFlags.NoSpace, '\0');

                // 5. Edge Cases (Safety)
                // Google.com should NOT parse as flags
                Test_Parse_Safety("google.com");
                Test_Parse_Safety("bob@example.co.uk"); // .uk is invalid flag, so treat as text

                // 6. Full Sentence Simulation
                Test_Full_Sentence();

                Console.WriteLine("\nAll Tests Passed Successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("\n!!! TEST FAILED !!!");
                Console.WriteLine(ex.Message);
            }

        
        }

        // --- Test Helpers ---

        static void Test_Compile(string input, WFlags flags, char punct, string expected)
        {
            string actual = DslProtocol.Compile(input, flags, punct);
            if (actual != expected)
                throw new Exception($"Compile Failed: Expected '{expected}', got '{actual}'");

            Console.WriteLine($"[PASS] Compile: {input} -> {actual}");
        }

        static void Test_Parse(string dsl, string expectedText, WFlags expectedFlags, char expectedPunct = '\0')
        {
            var word = DslProtocol.Parse(dsl.AsSpan(), null, 0);
            string rendered = RenderText(word);

            if (rendered != expectedText)
                throw new Exception($"Parse Text Failed: '{dsl}' -> Expected '{expectedText}', got '{rendered}'");

            if (word.Flags != expectedFlags)
                throw new Exception($"Parse Flags Failed: '{dsl}' -> Expected {expectedFlags}, got {word.Flags}");

            if (word.Punctuation != expectedPunct)
                throw new Exception($"Parse Punct Failed: '{dsl}' -> Expected '{expectedPunct}', got '{word.Punctuation}'");

            Console.WriteLine($"[PASS] Parse:   {dsl} -> {rendered} (Flags: {word.Flags})");
        }

        static void Test_Parse_Details(string dsl, string rawLowerText, WFlags exactFlags, char punct)
        {
            var word = DslProtocol.Parse(dsl.AsSpan(), null, 0);

            if (word.text.ToString() != rawLowerText)
                throw new Exception($"Detail Text Failed: Expected '{rawLowerText}', got '{word.text}'");

            // Check flags using HasFlag logic or exact match
            if (word.Flags != exactFlags)
                throw new Exception($"Detail Flags Failed: Expected {exactFlags}, got {word.Flags}");

            if (word.Punctuation != punct)
                throw new Exception($"Detail Punct Failed: Expected '{punct}', got '{word.Punctuation}'");

            Console.WriteLine($"[PASS] Complex: {dsl} parsed correctly.");
        }

        static void Test_Parse_Safety(string input)
        {
            var word = DslProtocol.Parse(input.AsSpan(), null, 0);

            // Should treat the whole string as text, Flags = None
            if (word.text.ToString() != input)
                throw new Exception($"Safety Failed: Text modified. Expected '{input}', got '{word.text}'");

            if (word.Flags != WFlags.None)
                throw new Exception($"Safety Failed: Flags detected in '{input}'. Expected None.");

            Console.WriteLine($"[PASS] Safety:  {input} -> Validated as Raw Text.");
        }

        static void Test_Full_Sentence()
        {
            Console.WriteLine("\n--- Testing Full Sentence Rendering ---");
            // K said "Hello, world."

            var w1 = DslProtocol.Parse("k.u".AsSpan(), null, 0);
            var w2 = DslProtocol.Parse("said".AsSpan(), null, 1);
            var w3 = DslProtocol.Parse("hello.ucqo".AsSpan(), null, 2); // Upper, Comma, QuoteOpen
            var w4 = DslProtocol.Parse("world.eqc".AsSpan(), null, 3);  // Period, QuoteClose

            var sb = new StringBuilder();
            sb.Append(RenderFull(w1));
            sb.Append(RenderFull(w2));
            sb.Append(RenderFull(w3));
            sb.Append(RenderFull(w4));

            string result = sb.ToString();
            string expected = "K said \"Hello, world.\" "; // Note trailing space

            if (result != expected)
                throw new Exception($"Sentence Failed.\nExpected: '{expected}'\nActual:   '{result}'");

            Console.WriteLine($"[PASS] Sentence: {result}");
        }

        // --- Rendering Logic (Replicating your Word.struct) ---

        static string RenderText(Word w)
        {
            if ((w.Flags & WFlags.IsRaw) != 0) return w.text.ToString();
            string t = w.text.ToString();
            if ((w.Flags & WFlags.UpperAll) != 0) return t.ToUpperInvariant();
            if ((w.Flags & WFlags.UpperFirst) != 0) return char.ToUpper(t[0]) + t.Substring(1);
            return t;
        }

        static string RenderFull(Word w)
        {
            var sb = new StringBuilder();
            if ((w.Flags & WFlags.QuoteOpen) != 0) sb.Append('"');
            sb.Append(RenderText(w));
            if (w.Punctuation != '\0') sb.Append(w.Punctuation);
            if ((w.Flags & WFlags.QuoteClose) != 0) sb.Append('"');
            if ((w.Flags & WFlags.NoSpace) == 0) sb.Append(' ');
            return sb.ToString();
        }
    }
}
    


