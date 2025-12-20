using System;
using System.Diagnostics;
using Telerik.Documents.Fixed.FormatProviders.Image.Skia;
using Telerik.Documents.Primitives;
using Telerik.Imaging.Svg;
using Telerik.Windows.Documents.Common.FormatProviders;
using Telerik.Windows.Documents.Fixed.FormatProviders.Text;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Common;
using Telerik.Windows.Documents.Fixed.Model.Editing;
using Telerik.Windows.Documents.Fixed.Utilities.Rendering;
using Telerik.Windows.Documents.Flow.FormatProviders.Docx;
using Telerik.Windows.Documents.Flow.FormatProviders.Html;
using Telerik.Windows.Documents.Flow.FormatProviders.Pdf;
using Telerik.Windows.Documents.Flow.FormatProviders.Rtf;
using Telerik.Windows.Documents.Flow.FormatProviders.Txt;
using Telerik.Windows.Documents.Flow.Model;

namespace ABC.BookCity.API.Services
{
    public class DocumentConverter
    {        

        private readonly List<IFormatProvider<RadFlowDocument>> providers;

        
        public DocumentConverter()
        {
            this.providers = new List<IFormatProvider<RadFlowDocument>>()
            {
                new DocxFormatProvider(),
                new HtmlFormatProvider(),
                new PdfFormatProvider(),
                new RtfFormatProvider(),
                new TxtFormatProvider()
            };
        }
        public string ExtractText(RadFixedDocument document)
        {
            TextFormatProvider textFormatProvider =new TextFormatProvider();
            
            string documentAsText = textFormatProvider.Export(document, TextFormatProviderSettings.Default, TimeSpan.FromSeconds(30));
            return documentAsText;
        }
        public RadFlowDocument LoadDocument(Stream inputStream, string format)
        {
            IFormatProvider<RadFlowDocument> formatProvider = null;
            switch (format)
            {
                case "docx":
                    formatProvider = new DocxFormatProvider();
                    break;
                case "html":
                    formatProvider = new HtmlFormatProvider();
                    break;
                case "rtf":
                    formatProvider = new RtfFormatProvider();
                    break;
                case "txt":
                    formatProvider = new TxtFormatProvider();
                    break;
                case "pdf":
                    formatProvider = new PdfFormatProvider();
                    break;
            }
            if (formatProvider == null)
            {
                Console.WriteLine("Not supported document format.");
                return null;
            }
            RadFlowDocument document = formatProvider.Import(inputStream, TimeSpan.FromSeconds(30));
            Console.WriteLine("Document loaded.");
            return document;
        }


        public void ConvertDocument(RadFlowDocument document, string convertToFormat)
        {                        
            IFormatProvider<RadFlowDocument> formatProvider = null;
            switch (convertToFormat)
            {
                case "docx":
                    formatProvider = new DocxFormatProvider();
                    break;
                case "html":
                    formatProvider = new HtmlFormatProvider();
                    break;
                case "rtf":
                    formatProvider = new RtfFormatProvider();
                    break;
                case "txt":
                    formatProvider = new TxtFormatProvider();
                    break;
                case "pdf":
                    formatProvider = new PdfFormatProvider();
                    break;
            }

            if (formatProvider == null)
            {
                Console.WriteLine("Not supported document format.");
                return;
            }
                        
            using (MemoryStream stream = new MemoryStream())
            {
                formatProvider.Export(document, stream, TimeSpan.FromSeconds(30));
            }

            Console.WriteLine("Document converted.");

        }
        public void ExportThumbnails(RadFixedDocument document,string dir)
        {
            SkiaImageFormatProvider imageProvider = new SkiaImageFormatProvider();

            foreach (RadFixedPage page in document.Pages)
            {
                byte[] resultImage = imageProvider.Export(page, TimeSpan.FromSeconds(30));
                int pageNumber = document.Pages.IndexOf(page) + 1;                
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllBytes(dir + @"\Page_" + pageNumber + ".png", resultImage);
            }
        }
    }
}