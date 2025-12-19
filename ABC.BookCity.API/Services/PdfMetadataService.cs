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
        public string ExtractAbstract(byte[] pdfData)
        {
            if (pdfData == null || pdfData.Length == 0) return "[Empty Data]";

            try
            {
                PdfFormatProvider provider = new PdfFormatProvider();
                using var ms = new MemoryStream(pdfData);

                RadFixedDocument document;
                try 
                {
                    // Use the recommended overload with timeout
                    document = provider.Import(ms, TimeSpan.FromSeconds(30));
                }
                catch (Exception ex)
                {
                    return $"[Extraction Error: {ex.Message}]";
                }

                if (document.Pages.Count == 0) return "[No Pages Found]";

                StringBuilder sb = new StringBuilder();
                int pagesToScan = Math.Min(document.Pages.Count, 5);
                
                for (int i = 0; i < pagesToScan; i++)
                {
                    try 
                    {
                        var page = document.Pages[i];
                        ExtractTextBasic(page, sb);
                    }
                    catch (Exception) { /* Skip problematic page */ }

                    if (sb.Length > 15000) break;
                }

                string fullText = sb.ToString();

                // --- TESTING BLOCK ---
                bool enableDebugExport = false; // Set to true to enable file exports for debugging
                if (enableDebugExport)
                {
                    try
                    {
                        string debugDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug_exports");
                        if (!Directory.Exists(debugDir)) Directory.CreateDirectory(debugDir);
                        
                        string fileName = $"extract_{DateTime.Now.Ticks}";
                        
                        // 1. Export raw extracted text
                        File.WriteAllText(Path.Combine(debugDir, $"{fileName}.txt"), fullText);

                        // 2. Export to HTML (Requires Telerik.Documents.Fixed.FormatProviders.Html NuGet)
                        // var htmlProvider = new Telerik.Windows.Documents.Fixed.FormatProviders.Html.HtmlFormatProvider();
                        // using (var htmlStream = File.Create(Path.Combine(debugDir, $"{fileName}.html")))
                        // {
                        //     htmlProvider.Export(document, htmlStream);
                        // }
                    }
                    catch (Exception) { /* Ignore debug export errors */ }
                }
                // ----------------------

                return ParseAbstractFromText(fullText);
            }
            catch (Exception ex)
            {
                return $"[Extraction Error: {ex.Message}]";
            }
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
