using System.Collections.Generic;

namespace ABC.DiscoveryCity.Words.Common.Models
{
    public class BookProcessingResult
    {
        public string FileName { get; set; } = string.Empty;
        public Guid SessionId { get; set; } // Handle for fetching paged data/pdf
        public int TotalWords { get; set; }
        public int TotalSentences { get; set; }
        public int TotalLayoutItems { get; set; }
    }
}
