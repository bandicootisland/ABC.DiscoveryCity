using ABC.DiscoveryCity.Words.Common;
using System;
using System.Collections.Generic;

namespace ABC.DiscoveryCity.Words.Common
{
    public static partial class WordExtensions
    {
        extension(Word word)
        {
            // Tag this word with a specific definition ID
            // Usage: w.TagDefinition("and", "3");
            public void TagDefinition(string headword, string senseId)
            {
                // Store format: "and:3"
                // This is a tiny string, very memory efficient.
                word.Tag("Def", $"{headword}:{senseId}");
            }

            // Retrieve the actual Sentence object from the library
            public Sentence? Definition
            {
                get
                {
                    string? key = word.GetTag("Def");
                    if (key == null) return null;
                    
                    return DefinitionLibrary.GetDefinition(key);
                }
            }
        }
    }
}
