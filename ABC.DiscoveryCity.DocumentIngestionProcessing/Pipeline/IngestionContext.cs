using ABC.DiscoveryCity.DocumentProcessing.Shared;
using ABC.DiscoveryCity.Embeddings;
using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.Words.Common;
using Telerik.Windows.Documents.Fixed.Model;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline;

public class IngestionContext
{
    // --- Input ---
    public required string FilePath { get; init; }
    public required string PublishedDir { get; init; }
    public required string DataSetName { get; init; }
    public required string SourceName { get; init; }
    public required Guid DataSetId { get; init; }

    // --- Configuration flags ---
    public bool NoImages { get; init; }
    public bool NoEmbeddings { get; init; }
    public bool ForceReprocess { get; init; }
    public long MaxFileSizeForCopy { get; init; } = 100 * 1024 * 1024; // 100 MB

    // --- Services (injected) ---
    public required DbService DbService { get; init; }
    public IEmbeddingService? EmbeddingService { get; init; }
    public ThumbnailService? ThumbnailService { get; init; }
    public PdfImageExtractor? PdfImageExtractor { get; init; }
    public SemaphoreSlim? ImageThrottle { get; init; }

    // --- State populated by steps ---
    public byte[]? PdfBytes { get; set; }
    public RadFixedDocument? TelerikDocument { get; set; }
    public DigitalBook? DigitalBook { get; set; }
    public BookCorpus? Corpus { get; set; }
    public string FullText { get; set; } = "";

    /// <summary>
    /// Raw text lines from non-corpus sources (spreadsheet rows, future transcripts).
    /// AssembleDocumentStep converts these to Sentence structs via Word splitting.
    /// </summary>
    public List<string> RawTextLines { get; set; } = new();

    /// <summary>
    /// Parsed Sentence structs with raw (un-numbered) text.
    /// Used for SentenceId generation. Set by AssembleDocumentStep.
    /// </summary>
    public IReadOnlyList<Sentence> RawSentences { get; set; } = Array.Empty<Sentence>();

    /// <summary>
    /// Cleaned + numbered display strings for storage and UI.
    /// Set by AssembleDocumentStep via TextCleaner.
    /// </summary>
    public List<string> DisplaySentences { get; set; } = new();

    /// <summary>
    /// Enhanced text (MIME cleaned, whitespace normalized).
    /// Derived from DisplaySentences — raw OCR is preserved separately.
    /// Null until TextEnhanceStep runs; if null, DisplaySentences is used for embeddings/search.
    /// </summary>
    public List<string>? EnhancedSentences { get; set; }

    public List<Guid> SentenceIds { get; set; } = new();
    public PdfMetadata? Metadata { get; set; }
    public string PublishedFilePath { get; set; } = "";
    public int PageCount { get; set; }

    // --- Image results (collected by ThumbnailStep, stored by StoreStep) ---
    public string FullImagePath { get; set; } = "";
    public string ThumbImagePath { get; set; } = "";
    public int FullImageWidth { get; set; }
    public int FullImageHeight { get; set; }
    public int ThumbImageWidth { get; set; }
    public int ThumbImageHeight { get; set; }
    public byte[] FullImageData { get; set; } = Array.Empty<byte>();
    public byte[] ThumbImageData { get; set; } = Array.Empty<byte>();

    // --- File-type-specific data (populated by LoadDocumentStep) ---
    public SpreadsheetResult? SpreadsheetResult { get; set; }
    public MediaMetadata? MediaMetadata { get; set; }
    public ImageFileMetadata? ImageFileMetadata { get; set; }

    /// <summary>
    /// Set when a file exceeds MaxFileSizeForCopy — DB entry is created but the file is not copied to Published.
    /// </summary>
    public bool SkipFileCopy { get; set; }

    // --- Page images (rendered by PageImagesStep) ---
    public Dictionary<int, byte[]> RenderedPages { get; set; } = new();

    // --- Background tasks (image generation runs async) ---
    public Task? BackgroundImageTask { get; set; }

    // --- Helpers ---
    public string FileName => Path.GetFileName(FilePath);
    public string FileExtension => Path.GetExtension(FilePath).ToLowerInvariant();
    public string OutputDir
    {
        get
        {
            if (string.IsNullOrEmpty(PublishedDir)) return Path.GetDirectoryName(FilePath) ?? "";
            var baseName = Path.GetFileNameWithoutExtension(FilePath);
            return Path.Combine(PublishedDir, baseName);
        }
    }

    public FileCategory Category => FileExtension switch
    {
        ".pdf" => FileCategory.Pdf,
        ".xlsx" or ".xls" or ".csv" => FileCategory.Spreadsheet,
        ".avi" or ".mp4" or ".vob" or ".mov" or ".mkv" or ".wmv" => FileCategory.Video,
        ".m4a" or ".mp3" or ".wav" or ".aac" or ".ogg" or ".flac" => FileCategory.Audio,
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".tif" or ".cr2" or ".webp" => FileCategory.Image,
        _ => FileCategory.Unknown
    };
}

public enum FileCategory
{
    Pdf,
    Spreadsheet,
    Video,
    Audio,
    Image,
    Unknown
}
