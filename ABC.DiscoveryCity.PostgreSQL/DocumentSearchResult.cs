namespace ABC.DiscoveryCity.PostgreSQL;

public class DocumentSearchResult
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string? FilePath { get; set; }
    public string? PdfFolder { get; set; }
    public string? ImageFolder { get; set; }
    public string? ThumbnailFileName { get; set; }
    public string? FullImageFileName { get; set; }
    public string Text { get; set; } = "";
    public double Distance { get; set; }
    public DateTime? Date { get; set; }
    public int PageCount { get; set; }
    public string? SourceName { get; set; }
    public string? DataSetName { get; set; }
    public string? Names { get; set; }
    public string? Terms { get; set; }
    public string? SnippetSource { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string? SourceUrl { get; set; }
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailFileName);
    public bool HasFullImage => !string.IsNullOrEmpty(FullImageFileName);
    public string? ResolvedFilePath => 
        DbService.BuildPath(PdfFolder, FileName) 
        ?? (FilePath != null ? DbService.ResolveFilePathForCurrentOs(FilePath) : null);
    public string? ResolvedThumbnailPath => 
        DbService.BuildPath(ImageFolder ?? PdfFolder, ThumbnailFileName);
    public string? ResolvedFullImagePath => 
        DbService.BuildPath(ImageFolder ?? PdfFolder, FullImageFileName);
}
