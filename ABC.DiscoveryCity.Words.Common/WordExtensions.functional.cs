using ABC.DiscoveryCity.Words.Common;
using System;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common
{
    public static class FunctionalWordExtensions
    {
        

        public static object? fx(this FunctionalWord word, params object[] args)
        {
            if (word.Behavior != null)
            {
                return word.Invoke(args);
            }
            // Support pipe syntax: data.fx(function, args)
            if (args.Length > 0 && args[0] is FunctionalWord func && func.Behavior != null)
            {
                var newArgs = new object[args.Length];
                newArgs[0] = word;
                Array.Copy(args, 1, newArgs, 1, args.Length - 1);
                return func.Invoke(newArgs);
            }
            return null;
        }

        public static object? fx(this FunctionalWord word, params FunctionalWord[] args)
        {
            if (word.Behavior != null)
            {
                var objArgs = new object[args.Length];
                for (int i = 0; i < args.Length; i++) objArgs[i] = args[i];
                return word.Invoke(objArgs);
            }
            // Support pipe syntax: data.fx(function, args)
            if (args.Length > 0 && args[0].Behavior != null)
            {
                var func = args[0];
                var newArgs = new object[args.Length];
                newArgs[0] = word;
                for (int i = 1; i < args.Length; i++) newArgs[i] = args[i];
                return func.Invoke(newArgs);
            }
            return null;
        }

        public static object? fx(this FunctionalWord function, Sentence args)
        {
            var wordArgs = args.words;
            var objArgs = new object[wordArgs.Length];
            for (int i = 0; i < wordArgs.Length; i++) objArgs[i] = wordArgs[i];
            return function.Invoke(objArgs);
        }

        public static object? fx(this Sentence sentence, FunctionalWord function)
        {
            var wordArgs = sentence.words;
            var objArgs = new object[wordArgs.Length];
            for (int i = 0; i < wordArgs.Length; i++) objArgs[i] = wordArgs[i];
            return function.Invoke(objArgs);
        }
                
        
        extension(HashSet<FunctionalWord> words)
        {
            public FunctionalWord AddWord(FunctionalWord word)
            {
                var w = word with { Ordinal = words.Count + 1 };
                words.Add(w);
                return word;
            }
        }
        extension(FrozenSet<FunctionalWord> words)
        {
            public FrozenSet<FunctionalWord> AppendWords(HashSet<FunctionalWord> hashset)
            {
                var appendedset = words.ToList();
                int count = appendedset.Count;
                foreach (var item in hashset)
                {
                    var i = item with { Ordinal = item.Ordinal + count };
                    appendedset.Add(i);
                }

                return appendedset.ToFrozenSet(FunctionalWordSpanComparer.Instance);
            }
        }
    }
    
   
}
