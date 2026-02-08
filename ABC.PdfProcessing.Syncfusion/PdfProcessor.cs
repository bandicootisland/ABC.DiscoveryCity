using System;
using System.Collections.Generic;
using System.IO;
using Syncfusion.Pdf;
using Syncfusion.Pdf.Parsing;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.PdfToImageConverter;
using Syncfusion.Pdf.Exporting;

namespace ABC.PdfProcessing.Syncfusion
{
    public class PdfProcessor
    {
        private WordSplitter? _wordSplitter;

        public void InitializeWordSplitter(string dictionaryPath)
        {
            _wordSplitter = new WordSplitter(dictionaryPath);
        }

        public string ExtractText(byte[] pdfData)
        {
            try
            {
                using var ms = new MemoryStream(pdfData);
                using PdfLoadedDocument loadedDocument = new PdfLoadedDocument(ms);
                string text = "";
                foreach (PdfLoadedPage page in loadedDocument.Pages)
                {
                    text += page.ExtractText();
                }

                if (_wordSplitter != null)
                {
                    text = _wordSplitter.Process(text);
                }

                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Syncfusion ExtractText Error: {ex.Message}");
                return string.Empty;
            }
        }

        public void ExportThumbnails(byte[] pdfData, string dir, string prefix = "")
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            try
            {
                using var ms = new MemoryStream(pdfData);
                
                using PdfToImageConverter imageConverter = new PdfToImageConverter();
                imageConverter.Load(ms);

                int pagesToRender = Math.Min(imageConverter.PageCount, 2);
                
                for (int i = 0; i < pagesToRender; i++)
                {
                    // The signature for Convert in .NET Core is Convert(pageIndex, isThumbnail, isHighQuality)
                    using Stream imageStream = imageConverter.Convert(i, false, true);
                    imageStream.Position = 0;
                    string fileName = string.IsNullOrEmpty(prefix) ? $"Page_{i + 1}.png" : $"{prefix}_Page_{i + 1}.png";
                    string outputPath = Path.Combine(dir, fileName);
                    using FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                    imageStream.CopyTo(fileStream);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Syncfusion ExportThumbnails Error: {ex.Message}");
            }
        }

        public void ExtractAllImages(byte[] pdfData, string dir, string prefix = "")
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            try
            {
                using var ms = new MemoryStream(pdfData);
                using PdfLoadedDocument loadedDocument = new PdfLoadedDocument(ms);
                
                int imageCount = 1;
                foreach (PdfLoadedPage page in loadedDocument.Pages)
                {
                    Stream[] images = page.ExtractImages();
                    if (images != null)
                    {
                        foreach (Stream imageStream in images)
                        {
                            string fileName = string.IsNullOrEmpty(prefix) ? $"Image_{imageCount++}.png" : $"{prefix}_Image_{imageCount++}.png";
                            string outputPath = Path.Combine(dir, fileName);
                            using FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                            imageStream.Position = 0;
                            imageStream.CopyTo(fileStream);
                            imageStream.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Syncfusion ExtractAllImages Error: {ex.Message}");
            }
        }

        public void ConvertDocument(Stream inputStream, string fromFormat, Stream outputStream, string toFormat)
        {
            try
            {
                FormatType fromType = GetFormatType(fromFormat);
                FormatType toType = GetFormatType(toFormat);

                using WordDocument document = new WordDocument(inputStream, fromType);
                document.Save(outputStream, toType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Syncfusion ConvertDocument Error: {ex.Message}");
            }
        }

        private FormatType GetFormatType(string format)
        {
            return format.ToLower() switch
            {
                "docx" => FormatType.Docx,
                "rtf" => FormatType.Rtf,
                "txt" => FormatType.Txt,
                "html" => FormatType.Html,
                "dotx" => FormatType.Dotx,
                _ => FormatType.Docx
            };
        }
    }
}
