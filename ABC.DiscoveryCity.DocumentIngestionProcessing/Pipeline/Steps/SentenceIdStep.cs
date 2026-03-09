using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.Words.Common.Models;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

/// <summary>
/// Generates UUIDv8 sentence IDs with structural DNA and blockchain chaining.
/// Priority: RawSentences (corpus Sentence structs) → RawTextLines (clean un-numbered text)
///         → DisplaySentences (last resort, includes numbering prefixes).
/// </summary>
public class SentenceIdStep : IIngestionStep
{
    public string Name => "SentenceIds";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.RawSentences.Count > 0)
        {
            // Corpus path (PDF): checksums from raw Sentence.text (no numbering)
            ctx.SentenceIds = SentenceIdBuilder.GenerateIds(ctx.RawSentences);
        }
        else if (ctx.RawTextLines.Count > 0)
        {
            // Non-corpus text path (spreadsheet, future transcripts): clean un-numbered text
            ctx.SentenceIds = SentenceIdBuilder.GenerateIds((IReadOnlyList<string>)ctx.RawTextLines);
        }
        else if (ctx.DisplaySentences.Count > 0)
        {
            // Fallback (media metadata): descriptive text, no raw text available
            ctx.SentenceIds = SentenceIdBuilder.GenerateIds(ctx.DisplaySentences);
        }
        else
        {
            return Task.CompletedTask;
        }

        Console.WriteLine($"  [SentenceIds] {ctx.SentenceIds.Count} UUIDv8 IDs generated");
        return Task.CompletedTask;
    }
}
