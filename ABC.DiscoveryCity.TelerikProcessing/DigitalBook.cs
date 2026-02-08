// The "Finished Product" container
using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.Words.Common;
using Telerik.Windows.Documents.Fixed.Model;

public class DigitalBook
{
    // The Raw Data (Physical Layer)
    public BookCorpus Source { get; }

    // The Logical Structure (Semantic Layer)
    public IReadOnlyList<Sentence> Sentences { get; }
    public IReadOnlyList<Word> Words { get; }

    public DigitalBook(BookCorpus source, List<Sentence> sentences, List<Word> words)
    {
        Source = source;
        Sentences = sentences;
        Words = words;
    }
}

// The "One-Liner" Factory
public static class BookLoader
{
    public static DigitalBook Load(RadFixedDocument telerikDoc)
    {
        // Stage 1: Physical Extraction
        var builder = new CorpusBuilder();
        var corpus = builder.Process(telerikDoc);

        // Stage 2: Logical Parsing
        var ingestor = new CorpusIngestor();
        ingestor.Parse(corpus);

        // Return the complete package
        return new DigitalBook(corpus, ingestor.ResultSentences, ingestor.ResultWords);
    }
}