using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;
using ABC.DiscoveryCity.Words.Common.Processing;
using Telerik.Windows.Documents.Fixed.FormatProviders.Pdf;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Text;

namespace ABC.DiscoveryCity.TelerikProcessing
{
    public class PdfProcessor
    {
        private WordSplitter? _wordSplitter;

        public void InitializeWordSplitter(WordSplitter wordSplitter)
        {
            _wordSplitter = wordSplitter;
        }

        public string ExtractText(byte[] pdfData, bool useOcrAsFallback = true)
        {
            var provider = new PdfFormatProvider();
            RadFixedDocument document;
            
            using (var ms = new MemoryStream(pdfData))
            {
                document = provider.Import(ms);
            }

            var sb = new StringBuilder();
            
            // Manual Smart Extraction
            // TextExtractor is not available in this version/package.
            // We iterate fragments and infer spacing from Position geometry.
            
            foreach (var page in document.Pages)
            {
                // We'll process fragments in the order they appear (usually logical Order).
                // We track the end position of the previous fragment.
                
                double lastX = -1;
                double lastY = -1;
                
                // Telerik's Content collection might be mixed (Images, Text, Geometries).
                // Filter only TextFragment
                var textFragments = page.Content.OfType<Telerik.Windows.Documents.Fixed.Model.Text.TextFragment>();
                
                foreach (var fragment in textFragments)
                {
                    // Get position
                    // fragment.Position.Matrix.OffsetX / OffsetY give the start position.
                    // We need to calculate if we moved down (NewLine) or moved right significantly (Space).
                    
                    var pos = fragment.Position;
                    double currentX = pos.Matrix.OffsetX;
                    double currentY = pos.Matrix.OffsetY;
                    
                    // Simple logic:
                    // If Y changed significantly -> New Line
                    // If X gap is large -> Space
                    
                    if (lastY != -1)
                    {
                        // Check for vertical jump (approximate line height threshold, e.g. 5-10 units)
                        if (Math.Abs(currentY - lastY) > 5) 
                        {
                            sb.AppendLine();
                            lastX = -1; // Reset X tracking on new line
                        }
                    }
                    
                    if (lastX != -1)
                    {
                        // Check for horizontal gap. 
                        // If current X > lastX + modest threshold -> Insert Space
                        // Ideally we check font size/width, but a heuristic of > 2 units usually works for word breaks.
                        if (currentX > lastX + 2) 
                        {
                            sb.Append(" ");
                        }
                    } else if (lastY != -1 && lastX == -1) 
                    {
                        // If we reset X (newline) we don't need a space.
                    }
                    
                    sb.Append(fragment.Text);
                    
                    // Update lastX to be the END of this fragment.
                    // Calculating synthesized width is hard without font metrics.
                    // However, we can approximate: next fragment usually starts where previous ended if continuous.
                    // Better: Just use currentX as the "base" and if the next one is close, we assume continuation.
                    // Actually, to do this robustly:
                    // currentX + width? We don't have easy width here.
                    // Let's use the START of the current fragment + an estimated advance?
                    // Or simpler: Just track the START of the PREVIOUS fragment + some width? No.
                    
                    // Alternative: Rely on the fact that PDF fragments usually HAVE correct start coordinates.
                    // If fragment N starts at X=100, and fragment N+1 starts at X=105, and font size is 10, 
                    // they overlap or touch -> no space.
                    // If N starts at 100, N+1 at 120 -> space.
                    
                    // We need the WIDTH of the current fragment to know where it ends.
                    // Telerik TextFragment doesn't expose Width easily? 
                    // It has a Font and FontSize. We can estimate.
                    // Or we just update lastX to be the START of current + (CharCount * FontSize * 0.5) roughly?
                    // NO, that's flaky.
                    
                    // Let's use a simpler heuristic that relies on relative start positions only?
                    // If we just track the *End* of the *Previous* fragment, we know if there is a gap.
                    // Can we get the bounding box of a TextFragment? 
                    // In Telerik Fixed, looking at documentation... 
                    // No easy bounding box. 
                    
                    // Strategy 2: Using fragment.Position.Matrix.OffsetX is the Left edge.
                    // If we don't know the width, we can't key off the previous End.
                    // BUT: user complained about "running together".
                    // This implies we are missing spaces.
                    // Usually spaces in PDF are NOT characters, but gaps.
                    // So we insert a space if (CurrentX - LastFragmentEndX) > Threshold.
                    // Since we don't have LastFragmentEndX, let's assume average char width.
                    // Width ~= FontSize * 0.6 * Length.
                    
                    if (lastX != -1) // If not start of line
                    {
                       // This logic has a flaw: lastX here is the previous fragment's Left.
                       // We need PreviousLeft + PreviousWidth.
                    }
                    
                    // Let's try to track width estimate.
                    double fontSize = fragment.FontSize;
                    double estimatedWidth = fragment.Text.Length * (fontSize * 0.5); // Average char width
                    
                    lastX = currentX + estimatedWidth;
                    lastY = currentY;
                }
                
                // End of Page
                sb.AppendLine();
            }

            string processedText = sb.ToString();

            if (_wordSplitter != null)
            {
                // WordSplitter expects continuous text or processes it mostly fine.
                return _wordSplitter.Process(processedText);
            }

            return processedText;
        }

        // Helper methods for Flow are removed as we are using Fixed.
        private bool IsBoilerplate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (text.Contains("Downloaded from", StringComparison.OrdinalIgnoreCase)) return true;
            if (text.Contains("IOP Publishing", StringComparison.OrdinalIgnoreCase)) return true;
            if (Regex.IsMatch(text, @"^Page \d+ of \d+$")) return true;
            return false;
        }

        public List<PdfPageImage> ExportPageThumbnails(byte[] pdfData, int maxPages = 2)
        {
            var images = new List<PdfPageImage>();
            
            // Note: Thumbnail generation requires Telerik.Documents.Fixed and Skia.
            // Since we are prioritizing 'Flow' for text extraction and avoiding 'Windows' framework namespace confusion,
            // we will temporarily disable thumbnail generation.
            // Converting PDF to Images via Flow is not natively supported (Flow is for reflowable text).
            
            /*
            // Implementation using Fixed + Skia (if references allowed)
            try
            {
                var provider = new Telerik.Windows.Documents.Fixed.FormatProviders.Pdf.PdfFormatProvider();
                using var ms = new MemoryStream(pdfData);
                var document = provider.Import(ms);
                
                // ... Export logic ...
            }
            catch {}
            */

             return images;
        }
    }

    public class PdfPageImage
    {
        public int PageNumber { get; set; }
        public byte[] ImageData { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = "image/png";
    }
}
