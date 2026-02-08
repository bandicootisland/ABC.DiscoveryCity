namespace ABC.DiscoveryCity.Models
{
    public class DatasetInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ScriptPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public bool IsDownloaded { get; set; }
        public bool IsProcessed { get; set; }
        public bool IsImported { get; set; }
        public string ScriptContent { get; set; } = string.Empty;

        public string CSharpContent { get; set; } = string.Empty;
    }
}
