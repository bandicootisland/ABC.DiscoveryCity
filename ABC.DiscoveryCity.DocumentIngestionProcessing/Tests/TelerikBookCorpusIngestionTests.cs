using ABC.DiscoveryCity.DocumentProcessing.Shared;
using ABC.DiscoveryCity.Services;
using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.Words.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Telerik.Windows.Documents.Fixed.FormatProviders.Pdf;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Objects;
using Telerik.Windows.Documents.Fixed.Model.Text;
namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Tests
{
    public class TelerikBookCorpusIngestionTests
    {
        
        public static Tuple<DigitalBook, RadFixedDocument> RunParseBook(string pdfPath)
        {

            // 1. Load via Telerik (The "Heavy" Lift)
            // This is the only part that generates garbage.
            // Once this step is done, we extract what we need and can GC the document.
            RadFixedDocument telerikDoc;
            var provider = new PdfFormatProvider();

            using (Stream input = File.OpenRead(pdfPath))
            {
                Console.WriteLine($"Loading PDF {pdfPath}");
                telerikDoc = provider.Import(input, TimeSpan.FromSeconds(5*60));
                
            }
            var digitalbook = BookLoader.Load(telerikDoc);

            return new(digitalbook, telerikDoc);
        }
        public static string  RunQuickStats(string pdfPath)
        {

            RadFixedDocument telerikDoc;
            var provider = new PdfFormatProvider();

            using (Stream input = File.OpenRead(pdfPath))
            {
                Console.WriteLine("Loading PDF DOM...");
                telerikDoc = provider.Import(input, TimeSpan.FromSeconds(5 * 60));
            }
            var sb = new StringBuilder();
            int pageIndex = 1;

            foreach (var page in telerikDoc.Pages)
            {
                int textCount = 0, imageCount = 0, formCount = 0, pathCount = 0;
                
                foreach (var element in page.Content)
                {
                    if (element is TextFragment) textCount++;
                    else if (element is Image) imageCount++;
                    else if (element is Telerik.Windows.Documents.Fixed.Model.Graphics.Path) pathCount++; // Vector graphics
                    else if (element is Form) formCount++; // <--- THE SUSPECT
                }

                sb.AppendLine($"Page {pageIndex}: {textCount} Text, {imageCount} Images, {formCount} Forms, {pathCount} Paths");
                pageIndex++;
                //if (pageIndex > 3) break; // Only check first 3 pages
            }
            return sb.ToString();
        }

        public static string TestRedactionDetection(string pdfPath)
        {
            var (book, doc) = RunParseBook(pdfPath);
            var sb = new StringBuilder();
            sb.AppendLine($"=== Redaction Detection Test ===");
            sb.AppendLine($"File: {Path.GetFileName(pdfPath)}");
            sb.AppendLine($"Total Sentences: {book.Sentences.Count}");
            sb.AppendLine($"Total Words: {book.Words.Count}");
            
            // Show artifact diagnostics
            sb.AppendLine($"\n=== Artifacts Detected by CorpusBuilder ===");
            sb.AppendLine($"Total Artifacts: {book.Source.Artifacts.Length}");
            foreach (var art in book.Source.Artifacts.Take(20))
            {
                sb.AppendLine($"  - {art.Type} on page {art.PageIndex}: X={art.X:F1}, Y={art.Y:F1}, W={art.Width:F1}, H={art.Height:F1}");
            }
            
            // Raw path analysis
            sb.AppendLine($"\n=== Raw Path Analysis (First Page) ===");
            if (doc.Pages.Count > 0)
            {
                int pathCount = 0;
                int filledCount = 0;
                foreach (var elem in doc.Pages[0].Content)
                {
                    if (elem is Telerik.Windows.Documents.Fixed.Model.Graphics.Path path)
                    {
                        pathCount++;
                        if (path.IsFilled) filledCount++;
                        if (pathCount <= 10)
                        {
                            var fillInfo = "null";
                            if (path.Fill != null)
                            {
                                var typeName = path.Fill.GetType().Name;
                                fillInfo = typeName;
                                
                                // Try to get Gray value
                                var grayProp = path.Fill.GetType().GetProperty("Gray");
                                if (grayProp != null)
                                {
                                    var gVal = grayProp.GetValue(path.Fill);
                                    fillInfo += $" (Gray={gVal})";
                                }
                            }
                            var bounds = path.Geometry?.Bounds;
                            var boundsInfo = bounds.HasValue ? $"W={bounds.Value.Width:F0} H={bounds.Value.Height:F0}" : "no bounds";
                            sb.AppendLine($"  Path {pathCount}: Filled={path.IsFilled}, Fill={fillInfo}, {boundsInfo}");
                        }
                    }
                }
                sb.AppendLine($"  Total: {pathCount} paths, {filledCount} filled");
            }
            
            // Find all redaction markers in output
            var redactions = book.Words.Where(w => w.text.StartsWith("[redact.")).ToList();
            sb.AppendLine($"\n=== Redaction Markers in Output ===");
            sb.AppendLine($"Redactions Found: {redactions.Count}");
            
            foreach (var r in redactions)
            {
                sb.AppendLine($"  - {r.text} at word ordinal {r.Ordinal}");
            }
            
            // Show first few sentences for context
            sb.AppendLine("\n=== Sample Sentences ===");
            foreach (var sent in book.Sentences.Take(10))
            {
                sb.AppendLine(sent.ToString());
            }
            
            return sb.ToString();
        }
    }
}
