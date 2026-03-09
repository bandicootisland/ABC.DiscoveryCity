using ABC.DiscoveryCity.Words.Common;

namespace ABC.DiscoveryCity.DocumentProcessing.Shared;

/// <summary>
/// The finished product — combines physical extraction (BookCorpus)
/// with semantic analysis (Sentences, Words).
/// </summary>
public class DigitalBook
{
    public BookCorpus Source { get; }
    public IReadOnlyList<Sentence> Sentences { get; }
    public IReadOnlyList<Word> Words { get; }

    public DigitalBook(BookCorpus source, List<Sentence> sentences, List<Word> words)
    {
        Source = source;
        Sentences = sentences;
        Words = words;
    }
}
