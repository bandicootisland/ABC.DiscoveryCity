using ABC.DiscoveryCity.Words.Common;
using System.Collections.Generic;

namespace ABC.WordCity.Words.Common.Layers
{
    // =========================================================================
    // 1. DATA TYPES (Formatting Enums)
    // =========================================================================

    public enum FormatType : byte
    {
        None = 0,
        LineBreak = 1,      // Standard new line
        ParagraphBreak = 2, // Larger gap (deduced paragraph)
        SectionBreak = 3    // Page break or massive gap
    }

    public readonly struct ImageMetadata
    {
        public readonly double X, Y;
        public readonly string SourceId;
        public readonly bool IsEmpty;

        public static readonly ImageMetadata Empty = new ImageMetadata(0, 0, string.Empty, true);

        private ImageMetadata(double x, double y, string id, bool empty)
        {
            X = x; Y = y; SourceId = id; IsEmpty = empty;
        }

        public ImageMetadata(double x, double y, string id) : this(x, y, id, false) { }
    }

    // =========================================================================
    // 2. THE LAYERS CONTAINER
    // =========================================================================

    public class WordLayers
    {
        public static readonly WordLayers Empty = new WordLayers();

        // Layer 1: Images (Sparse)
        private readonly Dictionary<int, ImageMetadata> _layer1_Images = new Dictionary<int, ImageMetadata>();

        // Layer 2: Formatting (Sparse - only for words at end of lines)
        private readonly Dictionary<int, FormatType> _layer2_Formatting = new Dictionary<int, FormatType>();

        // --- LAYER 1: IMAGES ---
        public void AddImage(int wordOrdinal, ImageMetadata data) => _layer1_Images[wordOrdinal] = data;

        public ImageMetadata GetImage(int wordOrdinal)
        {
            return _layer1_Images.TryGetValue(wordOrdinal, out var img) ? img : ImageMetadata.Empty;
        }

        // --- LAYER 2: FORMATTING ---
        public void AddFormatting(int wordOrdinal, FormatType type) => _layer2_Formatting[wordOrdinal] = type;

        public FormatType GetFormatting(int wordOrdinal)
        {
            return _layer2_Formatting.TryGetValue(wordOrdinal, out var type) ? type : FormatType.None;
        }
    }

    
    // =========================================================================
    // 4. DSL EXTENSIONS (Usage: word.br())
    // =========================================================================

    public static class LayerExtensions
    {
        /// <summary>
        /// Layer 1: Image Accessor
        /// </summary>
        public static ImageMetadata _1(this Word w)
        {
            return w.Data.Layers.GetImage(w.Ordinal);
        }

        /// <summary>
        /// Layer 2: Line Break Detector.
        /// Returns true if this word is followed by a visual line break.
        /// </summary>
        public static bool br(this Word w)
        {
            var fmt = w.Data.Layers.GetFormatting(w.Ordinal);
            return fmt == FormatType.LineBreak || fmt == FormatType.ParagraphBreak || fmt == FormatType.SectionBreak;
        }

        /// <summary>
        /// Layer 2: Paragraph Break Detector.
        /// Returns true if this word is followed by a visual paragraph break.
        /// </summary>
        public static bool p(this Word w)
        {
            var fmt = w.Data.Layers.GetFormatting(w.Ordinal);
            return fmt == FormatType.ParagraphBreak || fmt == FormatType.SectionBreak;
        }
    }
}