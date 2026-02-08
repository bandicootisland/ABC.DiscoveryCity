namespace ABC.DiscoveryCity.Words.Common.Models
{
    public class WordLayoutItem
    {
        public int Ordinal { get; set; }
        public int PageIndex { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public string Text { get; set; } = string.Empty;
    }
}
