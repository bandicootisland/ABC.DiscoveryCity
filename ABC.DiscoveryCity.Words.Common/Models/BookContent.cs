using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.Generic;
using ABC.DiscoveryCity.Words.Common.Structure;

namespace ABC.DiscoveryCity.Words.Common
{
    public class BookContent
    {
        public ImmutableArray<Word> Words { get; init; } = ImmutableArray<Word>.Empty;
        public FrozenDictionary<string, WordStructs.WordStats> Vocabulary { get; init; } = FrozenDictionary<string, WordStructs.WordStats>.Empty;
        public ImmutableArray<Sentence> Sentences { get; init; } = ImmutableArray<Sentence>.Empty;
        public List<Annotation> Annotations { get; init; } = new();
    }
}
