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
namespace ABC.DiscoveryCity.TestApp.Tests
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

    }
}
