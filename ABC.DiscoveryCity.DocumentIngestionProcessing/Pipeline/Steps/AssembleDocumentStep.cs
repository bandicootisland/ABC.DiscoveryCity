using ABC.DiscoveryCity.TelerikProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

/// <summary>
/// Parses BookCorpus into Sentence structs via CorpusIngestor,
/// then generates cleaned display text via TextCleaner.
/// Produces both RawSentences (for ID generation) and DisplaySentences (for storage/UI).
/// Matches BookCity's AssembleBookStep pattern.
/// </summary>
public class AssembleDocumentStep : IIngestionStep
{
    public string Name => "AssembleDocument";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        // Shortcut: if DigitalBook was already built by Telerik and no corpus override happened,
        // use its pre-parsed sentences directly
        if (ctx.DigitalBook != null && ctx.Corpus == ctx.DigitalBook.Source)
        {
            ctx.RawSentences = ctx.DigitalBook.Sentences;
            ctx.DisplaySentences = TextCleaner.CleanSentences(
                ctx.RawSentences.Select(s => s.text).ToList());
            Console.WriteLine($"  [AssembleDocument] {ctx.RawSentences.Count} sentences (from Telerik DigitalBook)");
            return Task.CompletedTask;
        }

        // Parse corpus into sentences (DevExpress or any other corpus source)
        if (ctx.Corpus != null)
        {
            var ingestor = new CorpusIngestor();
            ingestor.Parse(ctx.Corpus);

            ctx.RawSentences = ingestor.ResultSentences;
            ctx.DisplaySentences = TextCleaner.CleanSentences(
                ctx.RawSentences.Select(s => s.text).ToList());

            // Rebuild FullText from corpus if not already set
            if (string.IsNullOrWhiteSpace(ctx.FullText))
                ctx.FullText = new string(ctx.Corpus.Content);

            Console.WriteLine($"  [AssembleDocument] {ctx.RawSentences.Count} sentences (from corpus)");
            return Task.CompletedTask;
        }

        // Non-corpus text path: RawTextLines from spreadsheet rows (or future transcripts).
        // Run through TextCleaner for consistent numbering — same pipeline as PDF sentences.
        if (ctx.RawTextLines.Count > 0)
        {
            ctx.DisplaySentences = TextCleaner.CleanSentences(ctx.RawTextLines);
            Console.WriteLine($"  [AssembleDocument] {ctx.DisplaySentences.Count} sentences (from RawTextLines)");
            return Task.CompletedTask;
        }

        // Media metadata: DisplaySentences already set by LoadDocumentStep (descriptive, not content)
        if (ctx.DisplaySentences.Count > 0)
        {
            Console.WriteLine($"  [AssembleDocument] {ctx.DisplaySentences.Count} sentences (pre-loaded)");
        }

        return Task.CompletedTask;
    }
}
