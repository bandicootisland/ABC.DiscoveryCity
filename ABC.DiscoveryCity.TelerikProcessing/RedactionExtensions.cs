using System;
using ABC.DiscoveryCity.DocumentProcessing.Shared;
using TelerikPath = Telerik.Windows.Documents.Fixed.Model.Graphics.Path;
using Telerik.Windows.Documents.Fixed.Model.Graphics;

namespace ABC.DiscoveryCity.TelerikProcessing
{
    /// <summary>
    /// Extension methods for detecting and estimating redacted text in PDFs.
    /// Redactions appear as large horizontal gaps between text fragments.
    /// </summary>
    public static class RedactionExtensions
    {
        /// <summary>
        /// Gap threshold as a multiple of font size.
        /// A gap > 3× fontSize is considered a likely redaction (vs normal word spacing ~0.2-0.6×).
        /// </summary>
        public const double REDACTION_GAP_FACTOR = 3.0;

        /// <summary>
        /// Lower gap threshold for email context (after From:, To:, etc.)
        /// Email addresses are often fully redacted, so we use a more sensitive threshold.
        /// </summary>
        public const double EMAIL_GAP_FACTOR = 1.5;

        /// <summary>
        /// Average character width as a fraction of font size.
        /// Used to estimate the number of redacted characters from the gap width.
        /// Typical values: 0.5 for proportional fonts, 0.6 for monospace.
        /// </summary>
        public const double AVG_CHAR_WIDTH_FACTOR = 0.5;

        /// <summary>
        /// Email header keywords that suggest following content may be redacted.
        /// </summary>
        private static readonly string[] EMAIL_KEYWORDS = new[]
        {
            "From:", "To:", "Cc:", "Bcc:", "wrote:", "sent:", "Reply-To:"
        };

        /// <summary>
        /// Checks if text ends with an email keyword (case-insensitive).
        /// </summary>
        public static bool EndsWithEmailKeyword(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var trimmed = text.TrimEnd();
            foreach (var kw in EMAIL_KEYWORDS)
            {
                if (trimmed.EndsWith(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if text ends with an email bracket pattern like "Name <" 
        /// where the email address is likely redacted.
        /// </summary>
        public static bool IsEmailBracketPattern(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var trimmed = text.TrimEnd();
            // Pattern: ends with "<" (start of email in "Name <email@domain>")
            return trimmed.EndsWith("<") || trimmed.EndsWith("< ");
        }

        /// <summary>
        /// Determines if context suggests we're in an email field (lower threshold applies).
        /// </summary>
        public static bool IsEmailContext(string previousText)
        {
            return EndsWithEmailKeyword(previousText) || IsEmailBracketPattern(previousText);
        }

        /// <summary>
        /// Determines if a horizontal gap indicates a likely redaction.
        /// </summary>
        public static bool IsLikelyRedaction(double gap, double fontSize)
        {
            if (fontSize <= 0) return false;
            return gap > (fontSize * REDACTION_GAP_FACTOR);
        }

        /// <summary>
        /// Determines if a gap indicates a likely redaction, considering email context.
        /// Uses lower threshold when following email keywords.
        /// </summary>
        public static bool IsLikelyRedaction(double gap, double fontSize, bool isEmailContext)
        {
            if (fontSize <= 0) return false;
            double factor = isEmailContext ? EMAIL_GAP_FACTOR : REDACTION_GAP_FACTOR;
            return gap > (fontSize * factor);
        }

        /// <summary>
        /// Estimates redacted char count using fixed factor. Use layout-aware overload for better accuracy.
        /// </summary>
        public static int EstimateRedactedCharCount(double gap, double fontSize)
        {
            if (fontSize <= 0) return 1;
            double charWidth = fontSize * AVG_CHAR_WIDTH_FACTOR;
            return Math.Max(1, (int)Math.Round(gap / charWidth));
        }

        /// <summary>
        /// Estimates redacted char count using dynamic char width from nearby layout tokens.
        /// </summary>
        public static int EstimateRedactedCharCount(double gap, double fontSize, LayoutToken[] layout, int currentIndex)
        {
            double charWidth = CalculateDynamicCharWidth(layout, currentIndex, fontSize);
            return Math.Max(1, (int)Math.Round(gap / charWidth));
        }

        /// <summary>
        /// Calculates average character width from nearby layout tokens.
        /// Samples tokens before and after currentIndex with similar font size.
        /// Falls back to fixed factor if no usable samples found.
        /// </summary>
        public static double CalculateDynamicCharWidth(LayoutToken[] layout, int currentIndex, double fontSize)
        {
            if (layout == null || layout.Length == 0)
                return fontSize * AVG_CHAR_WIDTH_FACTOR;

            // Sample window: 5 tokens before and after current position
            int start = Math.Max(0, currentIndex - 5);
            int end = Math.Min(layout.Length, currentIndex + 5);

            double totalWidth = 0;
            int totalChars = 0;

            for (int i = start; i < end; i++)
            {
                var token = layout[i];
                
                // Only include tokens with actual text and similar font size (within 20%)
                if (token.TextLength > 0 && token.Width > 0)
                {
                    double sizeDiff = Math.Abs(token.FontSize - fontSize) / fontSize;
                    if (sizeDiff < 0.2)
                    {
                        totalWidth += token.Width;
                        totalChars += token.TextLength;
                    }
                }
            }

            if (totalChars > 0)
            {
                return totalWidth / totalChars;
            }

            // Fallback to fixed factor
            return fontSize * AVG_CHAR_WIDTH_FACTOR;
        }

        /// <summary>
        /// Creates the redaction marker string in the format [redact.char(N)].
        /// </summary>
        public static string CreateRedactionMarker(int charCount)
            => $"[redact.char({charCount})]";

        /// <summary>
        /// Creates the redaction marker with context, e.g. [redact.email.char(N)] for email fields.
        /// </summary>
        public static string CreateRedactionMarker(int charCount, string contextType)
            => $"[redact.{contextType}.char({charCount})]";

        /// <summary>
        /// Full analysis: checks if gap is a redaction and returns the marker if so.
        /// </summary>
        public static string? DetectRedaction(double gap, double fontSize)
        {
            if (!IsLikelyRedaction(gap, fontSize))
                return null;

            int charCount = EstimateRedactedCharCount(gap, fontSize);
            return CreateRedactionMarker(charCount);
        }

        // =========================================================================
        // PATH ANALYSIS (Detect Black-Filled Rectangles)
        // =========================================================================

        /// <summary>
        /// Checks if a Path element is a black-filled rectangle (typical redaction bar).
        /// </summary>
        public static bool IsBlackFilledRectangle(TelerikPath path)
        {
            if (path == null) return false;

            // Check for solid fill (not just stroke)
            if (!path.IsFilled) return false;

            var fill = path.Fill;
            if (fill == null) return false;

            // Check the type name to determine how to extract color
            var typeName = fill.GetType().Name;
            
            // Handle GrayColor (single gray value: 0 = black, 1 = white)
            if (typeName == "GrayColor")
            {
                try
                {
                    var grayProp = fill.GetType().GetProperty("Gray");
                    if (grayProp != null)
                    {
                        var grayValue = Convert.ToDouble(grayProp.GetValue(fill));
                        // Gray = 0 is black, Gray < 0.1 is near-black
                        return grayValue < 0.1;
                    }
                }
                catch { }
            }
            
            // Handle RgbColor
            if (typeName == "RgbColor")
            {
                try
                {
                    var rProp = fill.GetType().GetProperty("R");
                    var gProp = fill.GetType().GetProperty("G");
                    var bProp = fill.GetType().GetProperty("B");

                    if (rProp != null && gProp != null && bProp != null)
                    {
                        var r = Convert.ToInt32(rProp.GetValue(fill));
                        var g = Convert.ToInt32(gProp.GetValue(fill));
                        var b = Convert.ToInt32(bProp.GetValue(fill));
                        return r < 30 && g < 30 && b < 30;
                    }
                }
                catch { }
            }

            return false;
        }

        /// <summary>
        /// Creates an ArtifactMarker from a Path's geometry bounds.
        /// </summary>
        public static ArtifactMarker CreateRedactionMarkerFromPath(TelerikPath path, int pageIndex)
        {
            var bounds = path.Geometry.Bounds;
            return new ArtifactMarker(
                ArtifactType.Redaction,
                pageIndex,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height
            );
        }

        /// <summary>
        /// Checks if any artifact overlaps with the gap between two layout positions.
        /// </summary>
        public static ArtifactMarker? FindOverlappingArtifact(
            double prevRight, double currX, double y, int page,
            ReadOnlySpan<ArtifactMarker> artifacts)
        {
            foreach (var art in artifacts)
            {
                if (art.PageIndex != page) continue;
                if (art.Type != ArtifactType.Redaction) continue;

                // Check horizontal overlap with the gap
                if (art.OverlapsHorizontally(prevRight, currX) && art.ContainsY(y))
                {
                    return art;
                }
            }
            return null;
        }
    }
}

