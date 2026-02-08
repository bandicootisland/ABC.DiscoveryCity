using ABC.DiscoveryCity.Words.Common.Grammar;
using System;
using System.Collections;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Runtime.InteropServices;
using System.Text;
using static System.Net.Mime.MediaTypeNames;
using static System.Object;

namespace ABC.DiscoveryCity.Words.Common
{
    public static partial class WordExtensions
    {
        extension(Word word)
        {
            // --- CASING ---
            // .u = Upper First ("hello" -> "Hello")
            public Word u => word.WithFlags(WFlags.UpperFirst);

            // .U = Upper All ("hello" -> "HELLO")
            public Word U => word.WithFlags(WFlags.UpperAll);

            // .l = Lower ("Hello" -> "hello")
            //public Word l => word.WithFlags(WFlags.Lower);

            // --- PUNCTUATION ---
            // .e = End/Dot ("word" -> "word.")
            public Word e => word.WithPunctuation('.');

            // .c = Comma ("word" -> "word,")
            public Word c => word.WithPunctuation(',');

            // .q = Question ("word" -> "word?")
            public Word q => word.WithPunctuation('?');

            // .x = Exclamation ("word" -> "word!")
            public Word x => word.WithPunctuation('!');
            
            public Word ns => word.WithFlags(WFlags.NoSpace);

            
        }
    }
}
