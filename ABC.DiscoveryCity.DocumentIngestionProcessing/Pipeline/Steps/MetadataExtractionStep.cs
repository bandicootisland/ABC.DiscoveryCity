using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.TelerikProcessing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

public class MetadataExtractionStep : IIngestionStep
{
    public string Name => "MetadataExtraction";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        switch (ctx.Category)
        {
            case FileCategory.Pdf:
                BuildPdfMetadata(ctx);
                break;
            case FileCategory.Spreadsheet:
                BuildSpreadsheetMetadata(ctx);
                break;
            case FileCategory.Video:
            case FileCategory.Audio:
                BuildMediaMetadata(ctx);
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the best available text for metadata extraction:
    /// enhanced (MIME-cleaned) if available, otherwise raw FullText.
    /// </summary>
    private static string GetTextForAnalysis(IngestionContext ctx)
    {
        if (ctx.EnhancedSentences != null && ctx.EnhancedSentences.Count > 0)
            return string.Join(" ", ctx.EnhancedSentences);
        return ctx.FullText;
    }

    private void BuildPdfMetadata(IngestionContext ctx)
    {
        var analysisText = GetTextForAnalysis(ctx);
        var deducedDate = MetadataExtractors.DeduceDateFromText(analysisText);
        var deducedTitle = MetadataExtractors.DeduceTitleFromText(analysisText, Path.GetFileNameWithoutExtension(ctx.FilePath));
        var (extractedNames, extractedTerms) = MetadataExtractors.ExtractNamesAndTerms(analysisText);

        var fileInfo = new FileInfo(ctx.FilePath);
        int wordCount = string.IsNullOrWhiteSpace(ctx.FullText)
            ? 0
            : ctx.FullText.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries).Length;

        string? sourceSubFolder = null;
        try
        {
            var pdfDir = Path.GetDirectoryName(ctx.FilePath);
            if (pdfDir != null)
            {
                var dataSetDir = Path.GetDirectoryName(ctx.PublishedDir.TrimEnd(Path.DirectorySeparatorChar));
                if (dataSetDir != null && pdfDir.StartsWith(dataSetDir))
                    sourceSubFolder = pdfDir.Substring(dataSetDir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch { }

        ctx.Metadata = new PdfMetadata
        {
            FileName = ctx.FileName,
            Title = deducedTitle,
            PageCount = ctx.PageCount,
            DeducedDate = deducedDate ?? DateTime.MinValue,
            Text = ctx.EnhancedSentences ?? ctx.DisplaySentences,
            Names = extractedNames,
            Terms = extractedTerms,
            DataSetName = ctx.DataSetName,
            SourceName = ctx.SourceName,
            OriginalFilePath = ctx.FilePath,
            SourceFolder = sourceSubFolder ?? "",
            IngestedAtUtc = DateTime.UtcNow,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            WordCount = wordCount
        };
    }

    private void BuildSpreadsheetMetadata(IngestionContext ctx)
    {
        var result = ctx.SpreadsheetResult!;
        var analysisText = GetTextForAnalysis(ctx);
        var (extractedNames, extractedTerms) = MetadataExtractors.ExtractNamesAndTerms(analysisText);
        var fileInfo = new FileInfo(ctx.FilePath);
        int wordCount = string.IsNullOrWhiteSpace(ctx.FullText)
            ? 0
            : ctx.FullText.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries).Length;

        ctx.Metadata = new PdfMetadata
        {
            FileName = ctx.FileName,
            Title = $"Spreadsheet: {result.FileName} ({result.SheetCount} sheet{(result.SheetCount != 1 ? "s" : "")}, {result.Rows.Count} rows)",
            PageCount = result.SheetCount,
            Text = ctx.EnhancedSentences ?? ctx.DisplaySentences,
            Names = extractedNames,
            Terms = extractedTerms,
            DataSetName = ctx.DataSetName,
            SourceName = ctx.SourceName,
            OriginalFilePath = ctx.FilePath,
            IngestedAtUtc = DateTime.UtcNow,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            WordCount = wordCount
        };
    }

    private void BuildMediaMetadata(IngestionContext ctx)
    {
        var mediaMeta = ctx.MediaMetadata!;
        var fileInfo = new FileInfo(ctx.FilePath);

        ctx.Metadata = new PdfMetadata
        {
            FileName = ctx.FileName,
            Title = $"{mediaMeta.MediaType.ToUpperInvariant()}: {mediaMeta.FileName}",
            PageCount = 0,
            Text = ctx.EnhancedSentences ?? ctx.DisplaySentences,
            Names = new List<string>(),
            DataSetName = ctx.DataSetName,
            SourceName = ctx.SourceName,
            OriginalFilePath = ctx.FilePath,
            IngestedAtUtc = DateTime.UtcNow,
            FileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            WordCount = 0
        };
    }
}
