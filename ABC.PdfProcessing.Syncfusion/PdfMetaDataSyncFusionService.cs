using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;

namespace ABC.PdfProcessing.Syncfusion
{
    public class PdfMetaDataSyncFusionService
    {
        private readonly PdfProcessor _processor;

        public PdfMetaDataSyncFusionService()
        {
            _processor = new PdfProcessor();
            
            // Initialize WordSplitter with the dictionary from the API resources
            // We try a few common locations to find the Resources folder
            string[] potentialPaths = {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "words_alpha.txt"),
                Path.Combine(Directory.GetCurrentDirectory(), "Resources", "words_alpha.txt"),
                Path.Combine(Directory.GetCurrentDirectory(), "..", "ABC.DiscoveryCity.API", "Resources", "words_alpha.txt"),
                "h:\\Developer.BookCity\\ABC.DiscoveryCity\\ABC.DiscoveryCity.API\\Resources\\words_alpha.txt"
            };

            foreach (var path in potentialPaths)
            {
                if (File.Exists(path))
                {
                    _processor.InitializeWordSplitter(path);
                    break;
                }
            }
        }

        public string ExtractAbstract(byte[] pdfData, string folder, string filename)
        {
            if (pdfData == null || pdfData.Length == 0) return "[Empty Data]";
            
            string fullText = "";
            try
            {
                fullText = _processor.ExtractText(pdfData);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Syncfusion Extract Error for {filename}: {ex.Message}");
            }

            // Fallback if Syncfusion failed to get meaningful text
            if (string.IsNullOrWhiteSpace(fullText) || fullText.Length < 100)
            {
                string fallback = ExtractTextRawFallback(pdfData);
                if (!string.IsNullOrWhiteSpace(fallback) && fallback.Length > 50)
                {
                    fullText = fallback;
                }
            }

            if (string.IsNullOrWhiteSpace(fullText))
            {
                return "[Extraction Error: Could not extract text via Syncfusion or Fallback]";
            }

            // Create a subfolder for the file (without .pdf) inside the provided folder
            string fileWithoutExt = filename;
            if (fileWithoutExt.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileWithoutExt = fileWithoutExt.Substring(0, fileWithoutExt.Length - 4);

            string targetDir = Path.Combine(folder, fileWithoutExt);

            try
            {
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                // 0. Save the original PDF
                string pdfSavePath = Path.Combine(targetDir, filename);
                // Ensure we don't have directory separators in the filename if it's being flattened
                if (filename.Contains(Path.DirectorySeparatorChar) || filename.Contains(Path.AltDirectorySeparatorChar))
                {
                    pdfSavePath = Path.Combine(targetDir, Path.GetFileName(filename));
                }
                File.WriteAllBytes(pdfSavePath, pdfData);

                // 1. Save full text
                File.WriteAllText(Path.Combine(targetDir, $"{fileWithoutExt}_extracted.txt"), fullText);

                // 2. Save thumbnails
                _processor.ExportThumbnails(pdfData, targetDir, fileWithoutExt);

                // 3. Save all embedded images
                _processor.ExtractAllImages(pdfData, targetDir, fileWithoutExt);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Export error: {ex.Message}");
            }

            return ParseAbstractFromText(fullText);
        }

        private string ExtractTextRawFallback(byte[] data)
        {
            try
            {
                // 1. Try to find XMP Metadata (often contains abstract/description)
                string xmp = ExtractXmpMetadata(data);
                if (!string.IsNullOrEmpty(xmp))
                {
                    var match = Regex.Match(xmp, @"<dc:description[^>]*>.*?<rdf:li[^>]*>(.*?)</rdf:li>.*?</dc:description>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (match.Success) return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);

                    match = Regex.Match(xmp, @"(?i)<(?:dc:description|description|abstract)[^>]*>(.*?)</(?:dc:description|description|abstract)>", RegexOptions.Singleline);
                    if (match.Success) return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                }

                // 2. Brute force: extract printable ASCII strings
                StringBuilder sb = new StringBuilder();
                int start = -1;
                for (int i = 0; i < Math.Min(data.Length, 500000); i++) 
                {
                    byte b = data[i];
                    if (b >= 32 && b <= 126) 
                    {
                        if (start == -1) start = i;
                    }
                    else
                    {
                        if (start != -1)
                        {
                            int len = i - start;
                            if (len > 100)
                            {
                                string s = Encoding.ASCII.GetString(data, start, len);
                                sb.Append(s);
                            }
                            start = -1;
                        }
                    }
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        private string ExtractXmpMetadata(byte[] data)
        {
            try
            {
                int searchLimit = Math.Min(data.Length, 1024 * 1024);
                string head = Encoding.ASCII.GetString(data, 0, searchLimit);
                
                int start = head.IndexOf("<?xpacket begin");
                if (start != -1)
                {
                    int end = head.IndexOf("<?xpacket end", start);
                    if (end != -1)
                    {
                        return head.Substring(start, end - start);
                    }
                }
            }
            catch { }
            return "";
        }

        private string ParseAbstractFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "[Empty Text]";

            text = Regex.Replace(text, @"(\w)-\s*[\r\n]+\s*(\w)", "$1$2");
            string normalized = text.Replace("\r", "").Replace("\n", " ");
            normalized = FixSpacedOutText(normalized);
            normalized = Regex.Replace(normalized, @"\s{2,}", " ");

            var match = Regex.Match(normalized, @"(?i)(?:^|\s|[^a-z0-9])(Abstract|Summary)[:\s\.]*", RegexOptions.Compiled);
            
            if (match.Success)
            {
                int start = match.Index + match.Length;
                if (start < normalized.Length)
                {
                    string remaining = normalized.Substring(start);
                    var nextSectionMatch = Regex.Match(remaining, @"(?i)(?:\s|^)(Introduction|Keywords|Methods|Results|Discussion|References|Background|Conclusion|Acknowledgements|Appendix|1\.\s+|I\.\s+|II\.\s+)", RegexOptions.Compiled);
                    
                    string result;
                    if (nextSectionMatch.Success && nextSectionMatch.Index > 40)
                    {
                        result = remaining.Substring(0, nextSectionMatch.Index).Trim();
                    }
                    else
                    {
                        result = remaining.Length > 3500 ? remaining.Substring(0, 3500).Trim() : remaining.Trim();
                    }

                    result = Regex.Replace(result, @"^[:\s\-\.0-9]+", "");
                    if (!string.IsNullOrWhiteSpace(result) && result.Length > 20) 
                        return result;
                }
            }

            string fallback = normalized.Length > 2000 ? normalized.Substring(0, 2000).Trim() : normalized.Trim();
            return string.IsNullOrWhiteSpace(fallback) ? "[No Content Deducible]" : fallback;
        }

        private string FixSpacedOutText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 10) return text;

            if (Regex.IsMatch(text, @"(?:[A-Za-z]\s){4,}"))
            {
                StringBuilder sb = new StringBuilder(text.Length);
                ReadOnlySpan<char> span = text.AsSpan();
                
                for (int i = 0; i < span.Length; i++)
                {
                    char c = span[i];
                    sb.Append(c);
                    
                    if (c != ' ' && i + 2 < span.Length && span[i+1] == ' ')
                    {
                        char next = span[i+2];
                        if (next != ' ')
                        {
                            bool isSpacedZone = false;
                            if (i + 4 < span.Length && span[i+3] == ' ' && span[i+4] != ' ') isSpacedZone = true;
                            if (i > 1 && span[i-1] == ' ' && span[i-2] != ' ') isSpacedZone = true;
                            
                            if (isSpacedZone)
                            {
                                i++; 
                            }
                        }
                    }
                }
                return sb.ToString();
            }
            return text;
        }
    }
}
