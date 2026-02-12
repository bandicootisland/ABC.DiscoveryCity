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
//using LargeWords = ABC.DiscoveryCity.Lexicon.Modest.Words;

//using ABC.DiscoveryCity.Lexicon.Modest;
//using System;

//namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Tests
//{
//    public class WordsTesting
//    {
//        public static void Test()
//        {
//            // Ensure words are loaded
//            LargeWords.LoadAll();
            
//            Console.WriteLine("--- Large Words Testing ---");
            
//            // Using the clean syntax: the + chieftain
//            // Note: We use the static import to access 'the' and 'chieftain' directly
            
//            if (LargeWords.Dictionary.Contains(new Word("the")) && LargeWords.Dictionary.Contains(new Word("chieftain")))
//            {
//                var sentence = the + chieftain;
//                Console.WriteLine($"Generated Sentence: {sentence}");
//            }
//            else
//            {
//                Console.WriteLine("Could not find 'the' or 'chieftain' in Large Dictionary.");
//            }
//        }
//    }
//}

