using ABC.DiscoveryCity.DocumentProcessing.Shared;
using Telerik.Windows.Documents.Fixed.Model;

namespace ABC.DiscoveryCity.TelerikProcessing;

/// <summary>
/// Factory that loads a Telerik RadFixedDocument into a DigitalBook.
/// DigitalBook itself lives in DocumentProcessing.Shared.
/// </summary>
public static class BookLoader
{
    public static DigitalBook Load(RadFixedDocument telerikDoc)
    {
        var builder = new CorpusBuilder();
        var corpus = builder.Process(telerikDoc);

        var ingestor = new CorpusIngestor();
        ingestor.Parse(corpus);

        return new DigitalBook(corpus, ingestor.ResultSentences, ingestor.ResultWords);
    }
}
