using ABC.DiscoveryCity.DevExpressProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class DevExpressCorpusStep : IIngestionStep
{
    public string Name => "DevExpressCorpus";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.Category != FileCategory.Pdf) return Task.CompletedTask;

        byte[] pdfBytes = File.ReadAllBytes(ctx.FilePath);
        var extractor = new DevExpressPdfTextExtractor();
        var corpus = extractor.Process(pdfBytes);
        var devExpressText = new string(corpus.Content);

        // Only override Telerik corpus if DevExpress produced more content
        if (!string.IsNullOrWhiteSpace(devExpressText) && devExpressText.Length > ctx.FullText.Length)
        {
            Console.WriteLine($"    DevExpress extracted {devExpressText.Length:N0} chars (vs Telerik {ctx.FullText.Length:N0}) — using DevExpress");
            ctx.FullText = devExpressText;
            ctx.Corpus = corpus;
            ctx.PageCount = corpus.Layout.Length > 0 ? corpus.Layout.Max(t => t.PageIndex) : 0;
        }
        else if (!string.IsNullOrWhiteSpace(devExpressText))
        {
            Console.WriteLine($"    DevExpress extracted {devExpressText.Length:N0} chars (vs Telerik {ctx.FullText.Length:N0}) — keeping Telerik");
        }

        return Task.CompletedTask;
    }
}
