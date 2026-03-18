using ABC.DiscoveryCity.DocumentIngestionProcessing.Tests;
using ABC.DiscoveryCity.TelerikProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class LoadDocumentStep : IIngestionStep
{
    public string Name => "LoadDocument";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        switch (ctx.Category)
        {
            case FileCategory.Pdf:
                LoadPdf(ctx);
                break;

            case FileCategory.Spreadsheet:
                LoadSpreadsheet(ctx);
                break;

            case FileCategory.Video:
            case FileCategory.Audio:
                LoadMediaFile(ctx);
                break;

            default:
                throw new InvalidOperationException($"Unsupported file type: {ctx.FileExtension}");
        }

        // Ensure per-document Published subfolder exists
        var outputDir = ctx.OutputDir;
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Copy source file to Published subfolder
        if (!string.IsNullOrEmpty(ctx.PublishedDir))
        {
            ctx.PublishedFilePath = Path.Combine(outputDir, ctx.FileName);
            if (!File.Exists(ctx.PublishedFilePath))
            {
                File.Copy(ctx.FilePath, ctx.PublishedFilePath);
                Console.WriteLine($"  Copied → Published: {ctx.FileName}");
            }
        }
        else
        {
            ctx.PublishedFilePath = ctx.FilePath;
        }

        return Task.CompletedTask;
    }

    private void LoadPdf(IngestionContext ctx)
    {
        var (digitalBook, telerikDoc) = TelerikBookCorpusIngestionTests.RunParseBook(ctx.FilePath);
        ctx.TelerikDocument = telerikDoc;
        ctx.DigitalBook = digitalBook;
        ctx.PageCount = telerikDoc.Pages.Count;
    }

    private void LoadSpreadsheet(IngestionContext ctx)
    {
        var result = SpreadsheetLoader.Load(ctx.FilePath);

        // Store raw row text with sheet context (no ordinal numbering).
        // AssembleDocumentStep will create Sentence structs and apply TextCleaner numbering.
        foreach (var row in result.Rows)
        {
            string prefix = result.SheetCount > 1
                ? $"[{row.SheetName}:R{row.RowNumber}]"
                : $"[R{row.RowNumber}]";
            ctx.RawTextLines.Add($"{prefix} {row.Text}");
        }

        ctx.FullText = string.Join(" ", result.Rows.Select(r => r.Text));
        ctx.PageCount = result.SheetCount;

        // Store result for metadata step
        ctx.SpreadsheetResult = result;
    }

    private void LoadMediaFile(IngestionContext ctx)
    {
        var mediaMeta = MediaFileProcessor.GetMetadata(ctx.FilePath);

        // Media metadata is descriptive (not real content text).
        // Store as DisplaySentences directly — no Sentence struct needed.
        // When transcription is added, it would populate RawTextLines instead,
        // and AssembleDocumentStep would create proper Sentence structs.
        ctx.DisplaySentences = MediaFileProcessor.ToSentences(mediaMeta);
        ctx.FullText = string.Join(" ", ctx.DisplaySentences);
        ctx.PageCount = 0;

        // Store for metadata step
        ctx.MediaMetadata = mediaMeta;
    }
}
