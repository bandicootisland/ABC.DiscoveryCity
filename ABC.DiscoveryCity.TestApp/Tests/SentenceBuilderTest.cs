//using static ABC.DiscoveryCity.Lexicon.Modest.Words.A;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.B;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.C;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.D;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.E;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.F;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.G;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.H;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.I;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.J;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.K;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.L;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.M;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.N;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.O;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.P;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.Q;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.R;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.S;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.T;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.U;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.V;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.W;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.X;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.Y;
//using static ABC.DiscoveryCity.Lexicon.Modest.Words.Z;
//using ABC.DiscoveryCity.Lexicon.Modest;

//using static ABC.DiscoveryCity.Lexicon.Modest.Words;

//using System;

//namespace ABC.DiscoveryCity.TestApp.Tests
//{
//    public static class SentenceBuilderTest
//    {
//        public static void Test()
//        {
//            Console.WriteLine("=== Sentence Builder Test ===");
//            Console.WriteLine();

//            // Option 1: Word + Word starts a sentence
//            var sentence1 = the + quick + brown + fox;
//            Console.WriteLine($"Word + Word:  \"{sentence1}\"");

            
//            var sentence2 = the + lazy + dog;
//            Console.WriteLine($"Using .s():   \"{sentence2}\"");

//            // Option 3: Longer sentence
//            var sentence3 = the + quick + brown + fox + jumps + over + the + lazy + dog;
//            Console.WriteLine($"Full:         \"{sentence3}\"");

//            // Compile-time safety - try typing a wrong word!
//             //string bad = the + quikc; // ❌ Compile error!

//            var s1 = the + quick + _c + very + quick + _c + fox;
//            Console.WriteLine(s1);// "the quick, very quick, fox"

//            var s2 = the + quick + brown + fox + _p;
//            Console.WriteLine(s2);// "the quick brown fox."

//            // Reserved words now use underscore suffix - cleaner!
//            var s3 = is_ + the + fox + quick + _q;
//            Console.WriteLine(s3);// "is the fox quick?"

//            // Standard punctuation
//            var _s1 = the + fox + _p;                    // "the fox."

//            // Custom characters
//            var _s2 = the + fox + _("'s") + tail;       // "the fox's tail"
//            var _s3 = a + _("=") + b;                   // "a= b"  
//            var _s4 = step + one + _(" → ") + step + two; // "step one → step two"

//            // Single char
//            var _s5 = value + _('=') + result;          // "value= result"


//            Console.WriteLine();
//            Console.WriteLine("✓ All sentences built with zero typo risk!");
//            Console.WriteLine();
//        }
//    }
//}

