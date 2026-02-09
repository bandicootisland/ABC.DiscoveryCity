using ABC.WordCity.Words.Common;
using ABC.WordCity.Words.Common.Layers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Telerik.Windows.Documents.Fixed.Model;
using Telerik.Windows.Documents.Fixed.Model.Annotations;
using Telerik.Windows.Documents.Fixed.Model.Collections;
using Telerik.Windows.Documents.Fixed.Model.Common;
using Telerik.Windows.Documents.Fixed.Model.Graphics;
using Telerik.Windows.Documents.Fixed.Model.Objects;
using Telerik.Windows.Documents.Fixed.Model.Text;

namespace ABC.DiscoveryCity.TelerikProcessing
{
    // =========================================================================
    // 1. DATA STRUCTURES
    // =========================================================================

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public readonly struct LayoutToken
    {
        public readonly int TextOffset;
        public readonly int TextLength;
        public readonly int PageIndex;
        public readonly double X;
        public readonly double Y;
        public readonly double Width;
        public readonly double FontSize;

        public LayoutToken(int offset, int len, int page, double x, double y, double w, double fs)
        {
            TextOffset = offset; TextLength = len; PageIndex = page;
            X = x; Y = y; Width = w; FontSize = fs;
        }
    }

    public class BookCorpus
    {
        public char[] Content { get; }
        public LayoutToken[] Layout { get; }
        public WordLayers Layers { get; }
        public ArtifactMarker[] Artifacts { get; }

        public BookCorpus(BookRawBuffer buffer, WordLayers layers)
        {
            Content = buffer.Content ?? Array.Empty<char>();
            Layout = buffer.Layout ?? Array.Empty<LayoutToken>();
            Artifacts = buffer.Artifacts ?? Array.Empty<ArtifactMarker>();
            Layers = layers ?? WordLayers.Empty;
        }
    }

    public class BookRawBuffer
    {
        public char[] Content { get; set; } = Array.Empty<char>();
        public LayoutToken[] Layout { get; set; } = Array.Empty<LayoutToken>();
        public ArtifactMarker[] Artifacts { get; set; } = Array.Empty<ArtifactMarker>();
    }

    public class CorpusBuilder
    {
        private WordLayers _layers = new WordLayers();
        private List<ArtifactMarker> _artifacts = new List<ArtifactMarker>();
        private int _wordOrdinalCounter = 1;
        private const int MAX_RECURSION_DEPTH = 10; // Safety Guard

        public BookCorpus Process(RadFixedDocument doc)
        {
            _layers = new WordLayers();
            _artifacts = new List<ArtifactMarker>();
            _wordOrdinalCounter = 1;

            var allFragments = new List<ExtractedFragment>(doc.Pages.Count * 300);
            int totalCharCount = 0;

            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var page = doc.Pages[i];
                int pageIndex = i + 1;

                // 1. Stream Builder
                var builder = new SmartFragmentBuilder(allFragments, pageIndex);

                // 2. Recursive Stream Walk
                ExtractStream(page.Content, builder, 0, pageIndex); // Start at depth 0
                
                builder.Flush();

                // 3. Annotations
                ExtractAnnotations(page.Annotations, allFragments, pageIndex);
            }

            // 4. Flatten
            totalCharCount = allFragments.Sum(f => f.Text.Length);

            char[] bigBuffer = new char[totalCharCount];
            LayoutToken[] layoutMap = new LayoutToken[allFragments.Count];
            int currentOffset = 0;

            for (int i = 0; i < allFragments.Count; i++)
            {
                var item = allFragments[i];

                if (item.IsImage)
                {
                    // Use Token Index (i) as the key for Layer 0 (Physical). 
                    // The Ingestor will map this to the Word Ordinal (Layer 1).
                    _layers.AddImage(i, new ImageMetadata(item.X, item.Y, "img_" + i));
                }

                string s = item.Text;
                s.CopyTo(0, bigBuffer, currentOffset, s.Length);

                layoutMap[i] = new LayoutToken(
                    offset: currentOffset,
                    len: s.Length,
                    page: item.PageIndex,
                    x: item.X,
                    y: item.Y,
                    w: item.Width,
                    fs: item.FontSize
                );
                currentOffset += s.Length;
            }

            return new BookCorpus(
                new BookRawBuffer { 
                    Content = bigBuffer, 
                    Layout = layoutMap, 
                    Artifacts = _artifacts.ToArray() 
                },
                _layers
            );
        }

        // --- RECURSIVE STREAM WALKER (With Depth Guard) ---

        private void ExtractStream(ContentElementCollection elements, SmartFragmentBuilder builder, int recursionDepth, int pageIndex)
        {
            if (recursionDepth > MAX_RECURSION_DEPTH)
            {
                // Safety valve: stop recursing to prevent stack overflow on malformed PDFs
                return;
            }

            foreach (var element in elements)
            {
                if (element is TextFragment tf)
                {
                    builder.AddText(tf);
                    
                }
                else if (element is Image image)
                {
                    image.Height = 0;
                    image.Width = 0;
                    builder.Flush();
                    builder.AddImage(image);
                }
                else if (element is Telerik.Windows.Documents.Fixed.Model.Graphics.Path path)
                {
                    // Check if it's a black-filled rectangle (redaction bar)
                    if (RedactionExtensions.IsBlackFilledRectangle(path))
                    {
                        _artifacts.Add(RedactionExtensions.CreateRedactionMarkerFromPath(path, pageIndex));
                    }
                    builder.Flush();
                }
                else if (element is Form form && form.FormSource != null)
                {
                    ExtractStream(form.FormSource.Content, builder, recursionDepth + 1, pageIndex);
                }
                else if (element is IContainerElement container)
                {
                    ExtractStream(container.Content, builder, recursionDepth + 1, pageIndex);
                }
            }
        }

        private void ExtractAnnotations(AnnotationCollection annotations, List<ExtractedFragment> bucket, int pageIndex)
        {
            foreach (var annotation in annotations)
            {
                if (annotation is MarkupAnnotation markup && !string.IsNullOrWhiteSpace(markup.Contents))
                {
                    bucket.Add(new ExtractedFragment
                    {
                        Text = markup.Contents,
                        X = markup.Rect.Left,
                        Y = markup.Rect.Top,
                        Width = markup.Rect.Width,
                        FontSize = 12,
                        PageIndex = pageIndex
                    });
                }
            }
        }

 



 
    }
}