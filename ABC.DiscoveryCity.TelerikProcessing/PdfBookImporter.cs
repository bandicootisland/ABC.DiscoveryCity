using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using ABC.DiscoveryCity.Words.Common;
using ABC.DiscoveryCity.Words.Common.Models;
using Telerik.Windows.Documents.Fixed.FormatProviders.Pdf;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Text;

namespace ABC.DiscoveryCity.TelerikProcessing
{
    // Temporary stack-only struct for sorting. 
    // We don't want these on the heap.
    [StructLayout(LayoutKind.Auto)]
    public readonly struct PdfTextSpan
    {
        public readonly string Text; // Telerik gives us strings, unavoidable allocation here
        public readonly int PageIndex;
        public readonly double X;
        public readonly double Y;
        public readonly double FontSize;

        public PdfTextSpan(TextFragment tf, int pageIndex)
        {
            Text = tf.Text;
            PageIndex = pageIndex;
            // MatrixPosition gives absolute page coordinates
            X = tf.Position.Matrix.OffsetX;
            Y = tf.Position.Matrix.OffsetY;
            FontSize = tf.FontSize;
        }
    }

    public class PdfBookImporter
    {
        // Configuration for "Line Detection"
        private const double LineYThreshold = 2.5; // Pixels tolerance to consider text on same line

        public (ImmutableArray<Word> Words, ImmutableArray<Sentence> Sentences, List<WordLocation> Layout) ImportPdf(Stream pdfStream)
        {
            // 1. Load via Telerik (Memory intensive, but robust)
            var provider = new PdfFormatProvider();
            RadFixedDocument doc = provider.Import(pdfStream);

            var allSpans = new List<PdfTextSpan>();

            // 2. Extraction & Geometry Sorting
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var page = doc.Pages[i];
                var pageSpans = new List<PdfTextSpan>();

                // Iterate CONTENT (Not TextFormatProvider). 
                // This gives us access to Geometry (Position).
                foreach (var element in page.Content)
                {
                    if (element is TextFragment tf)
                    {
                        pageSpans.Add(new PdfTextSpan(tf, i));
                    }
                    // TODO: Handle 'element is Image' for diagrams here later
                }

                // 3. Sort Spans to Reconstruct Reading Order
                // Sort Top-to-Bottom, then Left-to-Right
                pageSpans.Sort((a, b) =>
                {
                    // If Y is roughly the same, sort by X
                    if (Math.Abs(a.Y - b.Y) < LineYThreshold)
                    {
                        return a.X.CompareTo(b.X);
                    }
                    return a.Y.CompareTo(b.Y);
                });

                allSpans.AddRange(pageSpans);
            }

            // 4. Ingestion into WordCity Structures
            return IngestSortedSpans(allSpans);
        }

        private (ImmutableArray<Word> Words, ImmutableArray<Sentence> Sentences, List<WordLocation> Layout) IngestSortedSpans(List<PdfTextSpan> spans)
        {
            // Setup your standard loading context
            var words = new List<Word>();
            var sentences = new List<Sentence>();
            
            // We need a custom SentenceData builder similar to your Load() method
            var currentSentenceWords = new List<Word>();
            var currentSentenceData = new SentenceData { Ordinal = 1 };
            int wordOrdinal = 1;
            int sentenceOrdinal = 1;

            // PARALLEL LAYOUT INDEX (The "Knowledge" Layer)
            // Maps: Word Ordinal -> Location
            var layoutMap = new List<WordLocation>(); 

            foreach (var span in spans)
            {
                // Telerik returns fragments. "Hello W" might be one fragment, "orld" another.
                // We must tokenize the string inside the fragment.
                
                ReadOnlySpan<char> text = span.Text.AsSpan();
                
                // Basic Tokenizer loop (matches your Load() logic)
                int start = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    bool isPunct = char.IsPunctuation(c) || char.IsSymbol(c);
                    bool isSpace = char.IsWhiteSpace(c);

                    if (isPunct || isSpace)
                    {
                        if (i > start)
                        {
                            var wText = text.Slice(start, i - start).ToString();
                            AddWord(wText, span, ref wordOrdinal, currentSentenceWords, currentSentenceData, words, layoutMap);
                        }
                        
                        if (isPunct)
                        {
                            AddWord(c.ToString(), span, ref wordOrdinal, currentSentenceWords, currentSentenceData, words, layoutMap);
                            
                            // Check Sentence End (Simple heuristic)
                            if (c == '.' || c == '!' || c == '?')
                            {
                                // Close Sentence
                                currentSentenceData.EndChar = c.ToString();
                                currentSentenceData.Words = currentSentenceWords.ToArray();
                                sentences.Add(new Sentence(currentSentenceData));
                                
                                currentSentenceWords.Clear();
                                sentenceOrdinal++;
                                currentSentenceData = new SentenceData { Ordinal = sentenceOrdinal };
                            }
                        }
                        start = i + 1;
                    }
                }
                
                // Catch trailing word in fragment
                if (start < text.Length)
                {
                    var wText = text.Slice(start).ToString();
                    AddWord(wText, span, ref wordOrdinal, currentSentenceWords, currentSentenceData, words, layoutMap);
                }
            }

            // Finalize pending sentence
            if (currentSentenceWords.Count > 0)
            {
                 currentSentenceData.Words = currentSentenceWords.ToArray();
                 sentences.Add(new Sentence(currentSentenceData));
            }

            return (words.ToImmutableArray(), sentences.ToImmutableArray(), layoutMap);
        }

        private void AddWord(string text, PdfTextSpan span, ref int ordinal, 
                             List<Word> sentenceBuffer, SentenceData sContext, 
                             List<Word> allWords, List<WordLocation> layoutMap)
        {
            // 1. Create the Word (Business Logic)
            var w = new Word(text, sContext, sentenceBuffer.Count, ordinal);
            allWords.Add(w);
            sentenceBuffer.Add(w);

            // 2. Store the Layout (Parallel Structure)
            // We map the global ordinal to the PDF coordinates
            layoutMap.Add(new WordLocation
            {
                Ordinal = ordinal,
                PageIndex = span.PageIndex,
                X = (float)span.X,
                Y = (float)span.Y,
                // Calculate width roughly or use Telerik Measure() if critical
            });

            ordinal++;
        }
    }
}
