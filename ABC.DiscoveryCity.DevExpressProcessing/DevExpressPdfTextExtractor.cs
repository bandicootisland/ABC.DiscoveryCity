using System.Text;
using ABC.DiscoveryCity.DocumentProcessing.Shared;
using ABC.WordCity.Words.Common.Layers;
using DevExpress.Pdf;

namespace ABC.DiscoveryCity.DevExpressProcessing;

public struct DevExpressFragment
{
    public string Text;
    public double X;
    public double Y;
    public double Width;
    public double Height;
    public int PageIndex;
}

public readonly struct GeometricComparer : IComparer<DevExpressFragment>
{
    private const double LineTolerance = 2.5;

    public int Compare(DevExpressFragment a, DevExpressFragment b)
    {
        if (Math.Abs(a.Y - b.Y) > LineTolerance)
            return a.Y.CompareTo(b.Y);
        return a.X.CompareTo(b.X);
    }
}

public class DevExpressPdfTextExtractor
{
    /// <summary>
    /// Extracts text with full layout tokens, producing a BookCorpus
    /// suitable for CorpusIngestor parsing into Sentence structs.
    /// Matches BookCity's PdfTextExtractor.Process() pattern.
    /// </summary>
    public BookCorpus Process(byte[] pdfBytes)
    {
        var layers = new WordLayers();
        var allFragments = new List<LayoutToken>();
        var globalTextBuffer = new StringBuilder(10_000);
        var artifacts = new List<ArtifactMarker>();

        using var msPdf = new MemoryStream(pdfBytes);
        using var processor = new PdfDocumentProcessor();
        processor.LoadDocument(msPdf);

        var pageFragments = new List<DevExpressFragment>();
        int currentScanningPage = -1;
        double currentPageHeight = 0;

        PdfPageWord? currentWord = processor.NextWord();

        while (currentWord != null)
        {
            int rawPageIndex = currentWord.PageNumber - 1;

            if (rawPageIndex != currentScanningPage)
            {
                if (pageFragments.Count > 0)
                {
                    ProcessPageFragments(currentScanningPage + 1, pageFragments, allFragments, globalTextBuffer);
                    pageFragments.Clear();
                }

                currentScanningPage = rawPageIndex;
                currentPageHeight = processor.Document.Pages[rawPageIndex].CropBox.Height;
            }

            for (int i = 0; i < currentWord.Rectangles.Count; i++)
            {
                var segmentText = currentWord.Segments[i].Text;
                var rect = currentWord.Rectangles[i];

                pageFragments.Add(new DevExpressFragment
                {
                    Text = segmentText,
                    X = rect.Left,
                    Y = currentPageHeight - rect.Top,
                    Width = rect.Width,
                    Height = rect.Height,
                    PageIndex = currentScanningPage + 1
                });
            }

            currentWord = processor.NextWord();
        }

        if (pageFragments.Count > 0)
            ProcessPageFragments(currentScanningPage + 1, pageFragments, allFragments, globalTextBuffer);

        return new BookCorpus(
            new BookRawBuffer
            {
                Content = globalTextBuffer.ToString().ToCharArray(),
                Layout = allFragments.ToArray(),
                Artifacts = artifacts.ToArray()
            },
            layers
        );
    }

    /// <summary>
    /// Legacy method — returns flat text string. Prefer Process() for structured output.
    /// </summary>
    public (string fullText, int pageCount) ExtractText(byte[] pdfBytes)
    {
        var corpus = Process(pdfBytes);
        return (new string(corpus.Content), corpus.Layout.Length > 0
            ? corpus.Layout.Max(t => t.PageIndex)
            : 0);
    }

    private static void ProcessPageFragments(int pageIndex, List<DevExpressFragment> pageFragments,
        List<LayoutToken> allFragments, StringBuilder globalTextBuffer)
    {
        pageFragments.Sort(new GeometricComparer());

        double lastY = -1;
        double lastRight = -1;

        foreach (var frag in pageFragments)
        {
            bool isLineStart = false;

            if (Math.Abs(frag.Y - lastY) > frag.Height * 0.3)
            {
                isLineStart = true;
                lastY = frag.Y;
                lastRight = -1;
            }
            else if (lastRight >= 0)
            {
                double gap = frag.X - lastRight;
                double spaceThreshold = frag.Height * 0.15;

                if (gap > spaceThreshold && globalTextBuffer.Length > 0 && globalTextBuffer[^1] != ' ')
                {
                    int spaceOffset = globalTextBuffer.Length;
                    globalTextBuffer.Append(' ');

                    allFragments.Add(new LayoutToken(
                        offset: spaceOffset,
                        len: 1,
                        page: pageIndex,
                        x: lastRight,
                        y: frag.Y,
                        w: Math.Max(1, gap),
                        fs: frag.Height,
                        isLineStart: false,
                        styleId: 0
                    ));
                }
            }

            int textOffset = globalTextBuffer.Length;
            globalTextBuffer.Append(frag.Text);

            allFragments.Add(new LayoutToken(
                offset: textOffset,
                len: frag.Text.Length,
                page: pageIndex,
                x: frag.X,
                y: frag.Y,
                w: frag.Width,
                fs: frag.Height,
                isLineStart: isLineStart,
                styleId: 0
            ));

            lastRight = frag.X + frag.Width;
        }
    }
}
