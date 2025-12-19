using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Telerik.Windows.Documents.Fixed.FormatProviders.Pdf;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Text;

namespace ABC.BookCity.API.Services
{
    public class PdfMetadataService
    {
        public string ExtractAbstract(Stream pdfStream)
        {
            try
            {
                PdfFormatProvider provider = new PdfFormatProvider();
                // Ensure stream is at beginning
                if (pdfStream.CanSeek) pdfStream.Position = 0;
                
                RadFixedDocument document = provider.Import(pdfStream);

                if (document.Pages.Count == 0) return string.Empty;

                StringBuilder sb = new StringBuilder();
                // Extract text from the first 2 pages
                int pagesToScan = Math.Min(document.Pages.Count, 2);
                
                for (int i = 0; i < pagesToScan; i++)
                {
                    var page = document.Pages[i];
                    // Use a simple approach to get text fragments
                    // In a real scenario, we might want to use TextSearcher for better positioning
                    foreach (var element in page.Content)
                    {
                        if (element is Telerik.Windows.Documents.Fixed.Model.Text.TextFragment fragment)
                        {
                            sb.Append(fragment.Text);
                        }
                        else if (element is Telerik.Windows.Documents.Fixed.Model.Common.IContainerElement container)
                        {
                            ExtractTextFromContainer(container, sb);
                        }
                    }
                    sb.AppendLine();
                }

                string fullText = sb.ToString();
                return ParseAbstractFromText(fullText);
            }
            catch (Exception ex)
            {
                // Log error but don't crash the indexer
                return $"[Extraction Error: {ex.Message}]";
            }
        }

        private void ExtractTextFromContainer(Telerik.Windows.Documents.Fixed.Model.Common.IContainerElement container, StringBuilder sb)
        {
            foreach (var element in container.Content)
            {
                if (element is Telerik.Windows.Documents.Fixed.Model.Text.TextFragment fragment)
                {
                    sb.Append(fragment.Text);
                }
                else if (element is Telerik.Windows.Documents.Fixed.Model.Common.IContainerElement subContainer)
                {
                    ExtractTextFromContainer(subContainer, sb);
                }
            }
        }

        private string ParseAbstractFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            // Normalize whitespace
            text = Regex.Replace(text, @"\s+", " ");

            // Look for "Abstract" or "Summary"
            var match = Regex.Match(text, @"(?i)(Abstract|Summary)[:\s]+", RegexOptions.None);
            if (match.Success)
            {
                int start = match.Index + match.Length;
                string remaining = text.Substring(start);
                
                // Look for next section headers
                var nextSectionMatch = Regex.Match(remaining, @"(?i)(Introduction|Keywords|Methods|Results|Discussion|References|1\.\s+|I\.\s+)", RegexOptions.None);
                
                string result;
                if (nextSectionMatch.Success)
                {
                    result = remaining.Substring(0, nextSectionMatch.Index).Trim();
                }
                else
                {
                    result = remaining.Length > 2500 ? remaining.Substring(0, 2500).Trim() : remaining.Trim();
                }

                // Clean up common artifacts
                result = Regex.Replace(result, @"^[:\s\-\.]+", "");
                return result;
            }

            return string.Empty;
        }
    }
}
