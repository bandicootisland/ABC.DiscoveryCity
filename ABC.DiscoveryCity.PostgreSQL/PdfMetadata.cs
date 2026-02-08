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
}
