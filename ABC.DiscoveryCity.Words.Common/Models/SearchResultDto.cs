using System;
using System.Collections.Generic;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common.Models
{
    public class SearchResultDto
    {
        public string HtmlPreview { get; set; } = "";
        public double Score { get; set; }

     
        public string PageLabel { get; set; } = ""; // e.g., "Page 249"
        public int BookId { get; set; }             // Which book?
        public int Ordinal { get; set; }            // Where in the book?
    }
}
