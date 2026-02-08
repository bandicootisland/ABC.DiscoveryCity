//using ABC.DiscoveryCity.Words.Common.Grammar;
//using System;
//using System.Collections;
//using System.Collections.Frozen;
//using System.Collections.Generic;
//using System.Collections.Immutable;
//using System.Diagnostics.Contracts;
//using System.Runtime.InteropServices;
//using System.Text;
//using static System.Net.Mime.MediaTypeNames;
//using static System.Object;

//namespace ABC.DiscoveryCity.Words.Common
//{

//    public static class WordTagExtensions
//    {
//        // 1. Define your constant keys here to avoid magic strings


//        // 2. The Extension Type (C# 13 Syntax)
//        // This makes the methods appear directly on the 'Word' struct
//        extension(Word word)
//        {
//            // --- Core Accessors ---

//            public void Tag(string category, string value)
//            {
//                // Delegates to the static column store using the Word's ID
//                WordTags.Add(word.Ordinal, category, value);
//            }

//            public string? GetTag(string category)
//            {
//                return WordTags.Get(word.Ordinal, category);
//            }

//            // --- Convenience Helpers (Your Domain Logic) ---

//            // Usage: word.TagGrammar("Noun");
//            public void TagGrammar(string partofspeech) => word.Tag(TagSet.Grammar, partofspeech);

//            // Usage: word.TagDefinition("101");
//            public void TagDefinition(string defId) => word.Tag(TagSet.Definition, defId);

//            // Usage: if (word.Grammar == "Noun") ...
//            public string? Grammar => word.GetTag(TagSet.Grammar);
//        }
//    }

//}

