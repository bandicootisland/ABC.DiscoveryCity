using System.Runtime.InteropServices;
using ABC.WordCity.Words.Common.Layers;

namespace ABC.DiscoveryCity.DocumentProcessing.Shared;

// =========================================================================
// LAYOUT TOKEN — positional mapping of text fragments on a PDF page
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
    public readonly bool IsLineStart;
    public readonly byte StyleId;

    public LayoutToken(int offset, int len, int page, double x, double y, double w, double fs)
    {
        TextOffset = offset; TextLength = len; PageIndex = page;
        X = x; Y = y; Width = w; FontSize = fs;
        IsLineStart = false; StyleId = 0;
    }

    public LayoutToken(int offset, int len, int page, double x, double y, double w, double fs,
                       bool isLineStart, byte styleId = 0)
    {
        TextOffset = offset; TextLength = len; PageIndex = page;
        X = x; Y = y; Width = w; FontSize = fs;
        IsLineStart = isLineStart; StyleId = styleId;
    }
}

// =========================================================================
// ARTIFACT MARKER — redactions, highlights, signatures detected in PDFs
// =========================================================================

public enum ArtifactType
{
    Redaction,
    Image,
    Signature,
    Stamp,
    Highlight
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ArtifactMarker
{
    public readonly ArtifactType Type;
    public readonly int PageIndex;
    public readonly double X, Y, Width, Height;

    public ArtifactMarker(ArtifactType type, int page, double x, double y, double w, double h)
    {
        Type = type; PageIndex = page;
        X = x; Y = y; Width = w; Height = h;
    }

    public double Right => X + Width;

    public bool OverlapsHorizontally(double startX, double endX, double tolerance = 2.0)
        => (X - tolerance) < endX && (Right + tolerance) > startX;

    public bool ContainsY(double y, double tolerance = 5.0)
        => y >= (Y - tolerance) && y <= (Y + Height + tolerance);
}

// =========================================================================
// BOOK RAW BUFFER — intermediate extraction result
// =========================================================================

public class BookRawBuffer
{
    public char[] Content { get; init; } = Array.Empty<char>();
    public LayoutToken[] Layout { get; init; } = Array.Empty<LayoutToken>();
    public ArtifactMarker[] Artifacts { get; init; } = Array.Empty<ArtifactMarker>();
}

// =========================================================================
// BOOK CORPUS — the complete physical extraction from a document
// =========================================================================

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
