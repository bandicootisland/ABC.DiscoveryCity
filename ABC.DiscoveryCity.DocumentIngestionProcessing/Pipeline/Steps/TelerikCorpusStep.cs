namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class TelerikCorpusStep : IIngestionStep
{
    public string Name => "TelerikCorpus";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.Category != FileCategory.Pdf) return Task.CompletedTask;
        if (ctx.TelerikDocument == null) return Task.CompletedTask;

        var simpleText = ctx.TelerikDocument.ToSimpleTextDocument(TimeSpan.FromSeconds(5 * 60));
        ctx.FullText = simpleText.Text;

        // Fallback: if simpleText is empty, try constructing from words
        if (string.IsNullOrWhiteSpace(ctx.FullText) && ctx.DigitalBook?.Words.Count > 0)
        {
            ctx.FullText = string.Join(" ", ctx.DigitalBook.Words.Select(w => w.text));
        }

        // Set corpus from DigitalBook — AssembleDocumentStep will parse into sentences
        if (ctx.DigitalBook != null)
        {
            ctx.Corpus = ctx.DigitalBook.Source;
        }

        return Task.CompletedTask;
    }
}
