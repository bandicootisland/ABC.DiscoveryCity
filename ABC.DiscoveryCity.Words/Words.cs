using ABC.DiscoveryCity.Words.Common;
using System.Collections.Frozen;

namespace ABC.DiscoveryCity.Words
{
    public static partial class Words
    {
        public static Word _space => new Word(" ");

        public static FrozenSet<Word> Dictionary { get => field ??= new HashSet<Word>(WordSpanComparer.Instance).ToFrozenSet(WordSpanComparer.Instance); set => field = value ??= new HashSet<Word>(WordSpanComparer.Instance).ToFrozenSet(WordSpanComparer.Instance); }

        public static FrozenSet<Word>.AlternateLookup<ReadOnlySpan<char>> Lookup => Dictionary.GetAlternateLookup<ReadOnlySpan<char>>();


        public static FrozenSet<Word> Load()
        {
            Load_All();
            return Dictionary;
        }


    }

}
// Force rebuild 2

