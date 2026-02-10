namespace ABC.DiscoveryCity.PostgreSQL;

public class PdfMetadata
{
    public string? FileName { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Producer { get; set; }
    public int PageCount { get; set; }
    public DateTime? CreationDate { get; set; }
    public DateTime? DeducedDate { get; set; }
    public List<string>? Text { get; set; }
    public List<string>? People { get; set; }

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
        People = People
    };
}

/// <summary>
/// Metadata stored in JSONB - excludes Text (stored in DocumentChunks instead)
/// </summary>
public class PdfMetadataForStorage
{
    public string? FileName { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Subject { get; set; }
    public string? Keywords { get; set; }
    public string? Producer { get; set; }
    public int PageCount { get; set; }
    public DateTime? CreationDate { get; set; }
    public DateTime? DeducedDate { get; set; }
    public List<string>? People { get; set; }
}
