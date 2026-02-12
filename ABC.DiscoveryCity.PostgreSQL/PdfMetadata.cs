namespace ABC.DiscoveryCity.PostgreSQL;

public class PdfMetadata
{
    public string FileName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Producer { get; set; } = "";
    public int PageCount { get; set; }
    public DateTime CreationDate { get; set; }
    public DateTime DeducedDate { get; set; }
    public List<string> Text { get; set; } = new();
    public List<string> People { get; set; } = new();

    // --- Enriched metadata (added for JSONB searchability) ---
    public string DataSetName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string OriginalFilePath { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public DateTime IngestedAtUtc { get; set; }
    public long FileSizeBytes { get; set; }
    public int WordCount { get; set; }
    public string ImageColorSpace { get; set; } = "";

    /// <summary>
    /// Returns metadata for JSONB storage (excludes Text to avoid redundancy with DocumentChunks)
    /// </summary>
    public PdfMetadataForStorage ToStorageDto() => new()
    {
        FileName = FileName,
        Title = Title,
        Author = Author,
        Subject = Subject,
        Keywords = Keywords,
        Producer = Producer,
        PageCount = PageCount,
        CreationDate = CreationDate,
        DeducedDate = DeducedDate,
        People = People,
        DataSetName = DataSetName,
        SourceName = SourceName,
        OriginalFilePath = OriginalFilePath,
        SourceFolder = SourceFolder,
        IngestedAtUtc = IngestedAtUtc,
        FileSizeBytes = FileSizeBytes,
        WordCount = WordCount,
        ImageColorSpace = ImageColorSpace
    };
}

/// <summary>
/// Metadata stored in JSONB - excludes Text (stored in DocumentChunks instead)
/// </summary>
public class PdfMetadataForStorage
{
    public string FileName { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Keywords { get; set; } = "";
    public string Producer { get; set; } = "";
    public int PageCount { get; set; }
    public DateTime CreationDate { get; set; }
    public DateTime DeducedDate { get; set; }
    public List<string> People { get; set; } = new();

    // --- Enriched metadata ---
    public string DataSetName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string OriginalFilePath { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public DateTime IngestedAtUtc { get; set; }
    public long FileSizeBytes { get; set; }
    public int WordCount { get; set; }
    public string ImageColorSpace { get; set; } = "";
}
