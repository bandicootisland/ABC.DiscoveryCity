using ABC.DiscoveryCity.Words.Common.Processing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

/// <summary>
/// Runs span-based text enhancement on DisplaySentences:
/// MIME hex decode, soft break removal, stray '=' cleanup, whitespace collapse.
///
/// Produces EnhancedSentences — a parallel list to DisplaySentences.
/// Raw OCR text (DisplaySentences) is preserved unchanged for evidence integrity.
/// EnhancedSentences is used for embeddings, search, and derived metadata.
/// </summary>
public class TextEnhanceStep : IIngestionStep
{
    public string Name => "TextEnhance";

    public Task ExecuteAsync(IngestionContext ctx)
    {
         if (ctx.DisplaySentences.Count == 0)
            return Task.CompletedTask;

        var enhanced = TextEnhancer.EnhanceAll(ctx.DisplaySentences);

        // Count actual changes
        int changed = 0;
        for (int i = 0; i < enhanced.Count; i++)
        {
            if (!ReferenceEquals(enhanced[i], ctx.DisplaySentences[i]))
                changed++;
        }

        if (changed > 0)
        {
            ctx.EnhancedSentences = enhanced;
            Console.WriteLine($"  [TextEnhance] {changed}/{enhanced.Count} sentences enhanced");
        }
        else
        {
            Console.WriteLine($"  [TextEnhance] no changes needed");
        }

        return Task.CompletedTask;
    }
}
