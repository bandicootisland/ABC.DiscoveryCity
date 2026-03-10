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

        // Compare by real word count, not string length.
        // Telerik text has \r\n after every word, inflating Length and always "winning"
        // even when DevExpress extracted more actual content.
        int dxWords = CountWords(devExpressText);
        int tkWords = CountWords(ctx.FullText);

        if (!string.IsNullOrWhiteSpace(devExpressText) && dxWords > tkWords)
        {
            Console.WriteLine($"    DevExpress extracted {dxWords} words (vs Telerik {tkWords}) — using DevExpress");
            ctx.FullText = devExpressText;
            ctx.Corpus = corpus;
            ctx.PageCount = corpus.Layout.Length > 0 ? corpus.Layout.Max(t => t.PageIndex) : 0;
        }
        else if (!string.IsNullOrWhiteSpace(devExpressText))
        {
            Console.WriteLine($"    DevExpress extracted {dxWords} words (vs Telerik {tkWords}) — keeping Telerik");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Count whitespace-separated words. Ignores \r\n inflation —
    /// "hello\r\nworld" and "hello world" both count as 2 words.
    /// </summary>
    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;
        bool inWord = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }
}
