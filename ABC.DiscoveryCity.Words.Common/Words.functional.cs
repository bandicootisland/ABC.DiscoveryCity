using ABC.DiscoveryCity.Words.Common;
using System.Collections.Frozen;
namespace ABC.DiscoveryCity.Words.Common
{
    public class WordBehavior
    {
        public Func<object[], object?> Operation { get; }
        public WordBehavior(Func<object[], object?> operation)
        {
            Operation = operation;
        }
    }

    public readonly record struct FunctionalWord(string word, WordBehavior? Behavior = null)
    {
        public ReadOnlySpan<char> w => word.AsSpan();
        public string s => word ?? "";
        public FunctionalWord u => new FunctionalWord(word.Substring(0, 1).ToUpper() + (word.Length > 1 ? word.Substring(1) : ""));

        public int Ordinal { get; init; }

        public object? Invoke(params object[] args) => Behavior?.Operation(args);

        
        //public static Sentence operator +(FunctionalWord a, FunctionalWord b)
        //    => new Sentence(a) + b;

        //// Allow Word + Punc to start a sentence with punctuation
        //public static Sentence operator +(FunctionalWord a, Chars chars)
        //    => new Sentence(a) + chars;

        // Allow Word + int to invoke the function if it exists
        //public static object? operator +(FunctionalWord a, int b)
        //{
        //    if (a.Behavior != null)
        //    {
        //        // This is a bit tricky because we are accumulating arguments.
        //        // A simple binary operator can't easily handle variable arguments without an intermediate type.
        //        // But for a simple "sum + 1" it could work if we return an intermediate "Invocation" object.
        //        return new Invocation(a).AddArg(b);
        //    }
        //    return new Sentence(a) + new FunctionalWord(b.ToString());
        //}

        // Allow Word + Args to execute the function
        public static object? operator +(FunctionalWord a, Args args)
        {
            return a.Invoke(args.Values);
        }

        public override string ToString() => s;
    }

    public readonly struct Args
    {
        public object[] Values { get; }
        public Args(object[] values) => Values = values;
    }

    public static partial class WordStructs
    {
        // Using a Dictionary to store words mapped by their string representation
        // This allows looking up the 'functional' word which contains the lambda
        public static FrozenDictionary<string, FunctionalWord> Vocabulary { get; private set; } = FrozenDictionary<string, FunctionalWord>.Empty;

        public static FrozenSet<FunctionalWord> Dictionary { get => field ??= new HashSet<FunctionalWord>(FunctionalWordSpanComparer.Instance).ToFrozenSet(FunctionalWordSpanComparer.Instance); set => field = value ??= new HashSet<FunctionalWord>(FunctionalWordSpanComparer.Instance).ToFrozenSet(FunctionalWordSpanComparer.Instance); }

        public static void Load(IEnumerable<FunctionalWord> functionalWords)
        {
            var dict = new Dictionary<string, FunctionalWord>(StringComparer.OrdinalIgnoreCase);
            var set = new HashSet<FunctionalWord>(FunctionalWordSpanComparer.Instance);

            if (Dictionary != null)
            {
                foreach (var w in Dictionary)
                {
                    set.Add(w);
                }
            }

            foreach (var w in functionalWords)
            {
                dict[w.s] = w;
                set.Add(w);
            }
            Vocabulary = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            Dictionary = set.ToFrozenSet(FunctionalWordSpanComparer.Instance);
        }

        public static FunctionalWord Define(string name, Func<object[], object?> operation)
        {
            return new FunctionalWord(name, new WordBehavior(operation));
        }

        public static Args _p(params object[] args) => new Args(args);
    }

    public sealed class FunctionalWordSpanComparer :
        IEqualityComparer<FunctionalWord>,
        IAlternateEqualityComparer<ReadOnlySpan<char>, FunctionalWord>
    {
        public static readonly FunctionalWordSpanComparer Instance = new();

        public bool Equals(FunctionalWord x, FunctionalWord y) =>
            string.Equals(x.word, y.word, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(FunctionalWord obj) =>
            string.GetHashCode(obj.word.AsSpan(), StringComparison.OrdinalIgnoreCase);

        public bool Equals(ReadOnlySpan<char> alternate, FunctionalWord other) =>
            alternate.Equals(other.word.AsSpan(), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ReadOnlySpan<char> alternate) =>
            string.GetHashCode(alternate, StringComparison.OrdinalIgnoreCase);

        public FunctionalWord Create(ReadOnlySpan<char> alternate) =>
            new FunctionalWord(alternate.ToString());
    }

    public class Invocation
    {
        private readonly FunctionalWord _word;
        private readonly List<object> _args = new();

        public Invocation(FunctionalWord word)
        {
            _word = word;
        }

        public Invocation AddArg(object arg)
        {
            _args.Add(arg);
            return this;
        }

        public static Invocation operator +(Invocation a, int b)
        {
            return a.AddArg(b);
        }

        // Implicit conversion to the result type? Or an explicit Execute()?
        // If we want "var total = sum + 1 + 2;" to return the result, we need implicit conversion.
        // But we don't know when the chain ends.
        // "sum + 1 + 2" -> Invocation
        // To get the value, maybe we need to cast or call a method.
        // Or, if the user accepts "var total = (int)(sum + 1 + 2);"

        // Alternatively, if we strictly follow "sum _p(1,2,3)" style:
        // sum + _p(1,2,3) -> Sentence? No, we want execution.
    }
}