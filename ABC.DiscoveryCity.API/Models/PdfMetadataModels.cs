namespace ABC.DiscoveryCity.API.Models
{
    public class PdfExtractRequest
    {
        public string Folder { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public byte[] PdfBytes { get; set; } = Array.Empty<byte>();
    }
}
