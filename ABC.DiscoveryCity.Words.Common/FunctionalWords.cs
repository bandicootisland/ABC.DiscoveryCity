using System.Collections.Frozen;
using System.Linq;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common
{
    public static partial class WordStructs
    {
        public static class Functions
        {
            public static FunctionalWord sum => new FunctionalWord(nameof(sum), new WordBehavior(args =>
            {
                int s = 0;
                foreach (var arg in args)
                {
                    if (arg is int i) s += i;
                    // Add other numeric types if needed
                }
                return s;
            }));

            public static FunctionalWord concat => new FunctionalWord(nameof(concat), new WordBehavior(args =>
            {
                var sb = new StringBuilder();
                foreach (var arg in args)
                {
                    sb.Append(arg?.ToString());
                }
                return sb.ToString();
            }));

            public static FunctionalWord countwords => new FunctionalWord(nameof(countwords), new WordBehavior(args =>
            {
                return args.Length;
            }));

            public static FunctionalWord SentenceGrammarAndSpacing => new FunctionalWord(nameof(SentenceGrammarAndSpacing), new WordBehavior(args =>
            {
                if (args.Length== 0)
                {
                    return SentenceGrammarAndSpacing;
                }
                Sentence sentence = (Sentence)args[0];
                
                if (sentence.Equals(default(Sentence)))
                {
                    return SentenceGrammarAndSpacing; 
                }
                int i = 0;
                int last = sentence.words.Length;
                Sentence s=default;
                foreach (var w in sentence.words.ToArray())
                {
                    i++;
                    if (i == 1)
                    {
                        //sentence.words
                        s= new Sentence(w.u);
                    }
                    else if (i== last)
                    {
                        s = s+ w + Word.FullStopMark;
                    }
                    else
                    {
                        s = s + w;
                    }
                }
                args[0] = s;



                return SentenceGrammarAndSpacing;
            }));
            internal static void Load_Functions()
            {
                var hashset = new HashSet<FunctionalWord>(2_000, FunctionalWordSpanComparer.Instance);
                hashset.AddWord(sum);
                hashset.AddWord(concat);
                hashset.AddWord(countwords);
                hashset.AddWord(SentenceGrammarAndSpacing);
                Dictionary = Dictionary.AppendWords(hashset);

            }
        }

    }
}