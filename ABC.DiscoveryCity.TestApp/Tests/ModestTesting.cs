using Words = ABC.DiscoveryCity.Words.Words;
using static ABC.DiscoveryCity.Words.Words.D;
using static ABC.DiscoveryCity.Words.Words.E;
using static ABC.DiscoveryCity.Words.Words.H;
using static ABC.DiscoveryCity.Words.Words.M;
using static ABC.DiscoveryCity.Words.Words.W;

using ABC.DiscoveryCity.Words;
using System;

namespace ABC.DiscoveryCity.TestApp.Tests
{
    public class ModestTesting
    {
        public static void Test()
        {
            // Ensure words are loaded
            //ModestWords.LoadAll();
            
            Console.WriteLine("--- Modest Words Testing ---");
            
            // Using words we added to modest.txt: hello, world, modest, dictionary, example
            
            var sentence = hello + world;
            Console.WriteLine($"Generated Sentence: {sentence}");
            
            var sentence2 = modest + dictionary + example;
            Console.WriteLine($"Generated Sentence 2: {sentence2}");
        }
    }
}
