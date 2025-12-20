using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using Telerik.Windows.Documents.Fixed.FormatProviders.Pdf;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Text;
using static System.Net.Mime.MediaTypeNames;

namespace ABC.BookCity.API.Services
{
    public class PdfMetadataService
    {
        public string ExtractAbstract(byte[] pdfData, string folder, string filename)
        {
            if (pdfData == null || pdfData.Length == 0) return "[Empty Data]";
            var converter = new DocumentConverter();
            string fullText = "";
            RadFixedDocument? document = null;

            try
            {
                PdfFormatProvider provider = new PdfFormatProvider();
                using var ms = new MemoryStream(pdfData);
                try
                {
                    document = provider.Import(ms, TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Telerik Import Error for {filename}: {ex.Message}");
                }

                if (document != null && document.Pages.Count > 0)
                {
                    try
                    {
                        fullText = converter.ExtractText(document);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Telerik Extract Error for {filename}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"General Telerik Error for {filename}: {ex.Message}");
            }

            // Fallback if Telerik failed to get meaningful text
            if (string.IsNullOrWhiteSpace(fullText) || fullText.Length < 100)
            {
                string fallback = ExtractTextRawFallback(pdfData);
                Console.WriteLine(fallback);
                if (!string.IsNullOrWhiteSpace(fallback) && fallback.Length > 50)
                {
                    fullText = fallback;
                }
            }

            if (string.IsNullOrWhiteSpace(fullText))
            {
                return "[Extraction Error: Could not extract text via Telerik or Fallback]";
            }

            // Create a subfolder for the file (without .pdf) inside the provided folder
            string fileWithoutExt = filename;
            if (fileWithoutExt.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                fileWithoutExt = fileWithoutExt.Substring(0, fileWithoutExt.Length - 4);

            string targetDir = Path.Combine(folder, fileWithoutExt);

            try
            {
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

                // 1. Save full text
                File.WriteAllText(Path.Combine(targetDir, "extracted_text.txt"), fullText);

                // 2. Save thumbnails (images) if document was loaded
                if (document != null && document.Pages.Count > 0)
                {
                    converter.ExportThumbnails(document, Path.Combine(targetDir, "images"));
                }
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
                // We search for the XMP packet which is usually uncompressed
                string xmp = ExtractXmpMetadata(data);
                if (!string.IsNullOrEmpty(xmp))
                {
                    // Look for description or abstract tags
                    var match = Regex.Match(xmp, @"<dc:description[^>]*>.*?<rdf:li[^>]*>(.*?)</rdf:li>.*?</dc:description>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    if (match.Success) return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);

                    match = Regex.Match(xmp, @"(?i)<(?:dc:description|description|abstract)[^>]*>(.*?)</(?:dc:description|description|abstract)>", RegexOptions.Singleline);
                    if (match.Success) return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
                }

                // 2. Brute force: extract printable ASCII strings
                // This is a last resort. We look for long sequences of printable characters.
                StringBuilder sb = new StringBuilder();
                int start = -1;
                for (int i = 0; i < Math.Min(data.Length, 500000); i++) // Limit to first 500KB for performance
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
                                // Only keep strings that look like sentences or contain keywords
                                //if (s.Contains(" ") && (s.Contains("Abstract") || s.Contains("abstract") || s.Contains("Introduction") || sb.Length > 0))
                                //{
                                    sb.Append(s);
                                //}
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
                // Search for XMP packet markers in the first 1MB
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

        private void ExtractTextBasic(RadFixedPage page, StringBuilder sb)
        {
            List<TextFragment> fragments = new List<TextFragment>();
            try 
            {
                GetAllTextFragments(page, fragments);
            }
            catch (Exception) { return; }

            if (fragments.Count == 0) return;

            var sortedFragments = fragments
                .OrderBy(f => {
                    try { return Math.Round(f.Position.Matrix.OffsetY, 0); } catch { return 0.0; }
                })
                .ThenBy(f => {
                    try { return f.Position.Matrix.OffsetX; } catch { return 0.0; }
                })
                .ToList();

            double lastY = -1;
            double lastX = -1;
            double lastWidth = 0;

            foreach (var fragment in sortedFragments)
            {
                try 
                {
                    double currentY = Math.Round(fragment.Position.Matrix.OffsetY, 0);
                    double currentX = fragment.Position.Matrix.OffsetX;
                    string text = fragment.Text;

                    if (string.IsNullOrEmpty(text)) continue;

                    if (lastY != -1 && Math.Abs(currentY - lastY) > 5)
                    {
                        sb.AppendLine();
                        lastX = -1; 
                    }
                    else if (lastX != -1)
                    {
                        double gap = currentX - (lastX + lastWidth);
                        double spaceThreshold = fragment.FontSize * 0.22; 
                        if (gap > spaceThreshold && !text.StartsWith(" ") && !EndsWithSpace(sb))
                        {
                            sb.Append(" ");
                        }
                    }

                    sb.Append(text);

                    lastY = currentY;
                    lastX = currentX;
                    lastWidth = text.Length * fragment.FontSize * 0.48; 
                }
                catch (Exception) { continue; }
            }
            sb.AppendLine();
        }

        private bool EndsWithSpace(StringBuilder sb)
        {
            if (sb.Length == 0) return true;
            return char.IsWhiteSpace(sb[sb.Length - 1]);
        }

        private void GetAllTextFragments(Telerik.Windows.Documents.Fixed.Model.Common.IContainerElement container, List<TextFragment> fragments)
        {
            if (container == null) return;
            foreach (var element in container.Content)
            {
                try 
                {
                    if (element is TextFragment fragment)
                    {
                        fragments.Add(fragment);
                    }
                    else if (element is Telerik.Windows.Documents.Fixed.Model.Common.IContainerElement subContainer)
                    {
                        GetAllTextFragments(subContainer, fragments);
                    }
                }
                catch (Exception) { continue; }
            }
        }

        private string ParseAbstractFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "[Empty Text]";

            // 1. Hyphenation fix
            text = Regex.Replace(text, @"(\w)-\s*[\r\n]+\s*(\w)", "$1$2");
            
            // 2. Normalize whitespace
            string normalized = text.Replace("\r", "").Replace("\n", " ");
            
            // 3. Fix spaced out text
            normalized = FixSpacedOutText(normalized);

            // 4. Collapse multiple spaces
            normalized = Regex.Replace(normalized, @"\s{2,}", " ");

            // 5. Search for Abstract/Summary
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

            // Fallback: Take a chunk from the beginning
            string fallback = normalized.Length > 2000 ? normalized.Substring(0, 2000).Trim() : normalized.Trim();
            
            return string.IsNullOrWhiteSpace(fallback) ? "[No Content Deducible]" : fallback;
        }

        private string FixSpacedOutText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 10) return text;

            // Heuristic: if many single letters followed by spaces
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
                            // Check if we are in a "spaced out" zone
                            bool isSpacedZone = false;
                            if (i + 4 < span.Length && span[i+3] == ' ' && span[i+4] != ' ') isSpacedZone = true;
                            if (i > 1 && span[i-1] == ' ' && span[i-2] != ' ') isSpacedZone = true;
                            
                            if (isSpacedZone)
                            {
                                i++; // Skip the space
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
