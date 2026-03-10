using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Telerik.Windows.Documents.Fixed.Model.Objects;
using Telerik.Windows.Documents.Fixed.Model.Text;

namespace ABC.DiscoveryCity.TelerikProcessing
{
    public struct ExtractedFragment
    {
        public string Text;
        public double X;
        public double Y;
        public double Width;
        public double Height;
        public double FontSize;
        public int PageIndex;
        public bool IsImage;
    }

    public class SmartFragmentBuilder
    {
        private readonly List<ExtractedFragment> _targetList;
        private readonly int _pageIndex;
        private readonly StringBuilder _buffer;

        // Run State
        private double _startX;
        private double _startY;
        private double _fontSize;
        private double _lastRightEdge;
        private bool _hasContent;

        // --- TUNING ---
        private const double Y_TOLERANCE_FACTOR = 0.5;

        // [FINAL SETTING] 0.6
        // Safe setting. Keeps "Cha tGPT" together. 
        // We accept "itsfounding" as a necessary casualty to preserve headers.
        private const double BREAK_FACTOR = 0.6;

        private const double WIDTH_ESTIMATE_FACTOR = 0.55;

        public SmartFragmentBuilder(List<ExtractedFragment> targetList, int pageIndex)
        {
            _targetList = targetList;
            _pageIndex = pageIndex;
            _buffer = new StringBuilder(100);
        }

        public void AddText(TextFragment tf)
        {
            string rawText = tf.Text;
            if (string.IsNullOrEmpty(rawText)) return;

            // [STEP 1] Clean Internal Glue (e.g. "CooperUnion" inside one fragment)
            string txt = CleanInternalGlue(rawText);

            double tfX = tf.Position.Matrix.OffsetX;
            double tfY = tf.Position.Matrix.OffsetY;
            double tfSize = tf.FontSize;

            if (_hasContent)
            {
                bool isFontChange = Math.Abs(tfSize - _fontSize) > 1.0;
                bool isLineBreak = Math.Abs(tfY - _startY) > (_fontSize * Y_TOLERANCE_FACTOR);

                double gap = tfX - _lastRightEdge;
                bool isWideGap = gap > (_fontSize * BREAK_FACTOR);

                if (isFontChange || isLineBreak || isWideGap)
                {
                    Flush(isLineBreak: isLineBreak);
                }
                else
                {
                    // [STEP 2] Check Boundary Glue (Buffer ends, NewText starts)
                    if (_buffer.Length > 0)
                    {
                        char left = _buffer[_buffer.Length - 1];

                        // Context look-behind (critical for Norton's)
                        char leftPrev = (_buffer.Length > 1) ? _buffer[_buffer.Length - 2] : ' ';

                        char right = txt[0];

                        if (IsGlue(leftPrev, left, right))
                        {
                            _buffer.Append(' ');
                        }
                    }

                    _buffer.Append(txt);

                    double estimatedWidth = txt.Length * (tfSize * WIDTH_ESTIMATE_FACTOR);
                    _lastRightEdge = tfX + estimatedWidth;
                    return;
                }
            }

            // New Run
            if (!_hasContent)
            {
                _startX = tfX;
                _startY = tfY;
                _fontSize = tfSize;
                _hasContent = true;
                _buffer.Clear();
                _buffer.Append(txt);

                double estimatedWidth = txt.Length * (tfSize * WIDTH_ESTIMATE_FACTOR);
                _lastRightEdge = tfX + estimatedWidth;
            }
        }

        // --- THE GLUE DETECTOR ---

        private string CleanInternalGlue(string text)
        {
            if (text.Length < 2) return text;

            StringBuilder sb = null;

            for (int i = 1; i < text.Length; i++)
            {
                char left = text[i - 1];
                char right = text[i];
                char prev = (i > 1) ? text[i - 2] : ' ';

                if (IsGlue(prev, left, right))
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(text.Length + 5);
                        sb.Append(text, 0, i);
                    }
                    sb.Append(' ');
                }

                if (sb != null) sb.Append(right);
            }

            return sb == null ? text : sb.ToString();
        }

        private bool IsGlue(char prev, char left, char right)
        {
            // 1. CAMEL CASE (CooperUnion -> r + U)
            if (char.IsLower(left) && char.IsUpper(right)) return true;

            // 2. DOT INITIALS (Mary D.Herter -> . + H)
            if (left == '.' && char.IsUpper(right)) return true;

            // 3. PUNCTUATION GLUE (Institute,publishing -> , + p)
            if (char.IsPunctuation(left))
            {
                // Apostrophe Handling
                if (left == '\'' || left == '’')
                {
                    // "People's Institute" -> ' + I (Upper) = Split
                    if (char.IsUpper(right)) return true;
                    // "People's" -> ' + s (Lower) = Keep
                    return false;
                }

                // Normal Punctuation (Comma, Colon, etc.) -> Letter = Split
                // Exception: Dots followed by digits (3.5) or URLs (google.com)
                if (left == '.' && (char.IsDigit(right) || char.IsLower(right))) return false;

                if (char.IsLetter(right)) return true;
            }

            // 4. POSSESSIVE GLUE (Norton’spublishing -> s + p)
            // If we have "s" followed by lower, check if it was preceded by an apostrophe.
            if (left == 's' && char.IsLower(right))
            {
                if (prev == '\'' || prev == '’') return true;
            }

            return false;
        }

        public void AddImage(Image img)
        {
            Flush(isLineBreak: true);

            // Document-unit dimensions (layout size on page)
            int w = (int)Math.Round(img.Width);
            int h = (int)Math.Round(img.Height);

            // Extract pixel dimensions and byte size from ImageSource
            int pw = 0, ph = 0;
            long dataBytes = 0;
            string fmt = "?";
            try
            {
                if (img.ImageSource != null)
                {
                    pw = (int)img.ImageSource.Width;
                    ph = (int)img.ImageSource.Height;
                    var encoded = img.ImageSource.GetEncodedImageData();
                    if (encoded?.Data != null)
                    {
                        dataBytes = encoded.Data.Length;
                        var filter = encoded.Filters?.FirstOrDefault();
                        fmt = filter switch
                        {
                            "DCTDecode" => "jpeg",
                            "FlateDecode" => "png",
                            "CCITTFaxDecode" => "tiff",
                            "JBIG2Decode" => "jbig2",
                            "JPXDecode" => "jp2",
                            _ => filter ?? "raw"
                        };
                    }
                }
            }
            catch { /* ImageSource may not be available in all builds */ }

            // DSL: [image.sz(docW,docH,pixW,pixH,format,sizeKB,page)]
            string sizeLabel = dataBytes >= 1024 * 1024
                ? $"{dataBytes / 1024 / 1024}MB"
                : $"{Math.Max(1, dataBytes / 1024)}KB";
            int page = _pageIndex + 1; // 1-based for display

            _targetList.Add(new ExtractedFragment
            {
                Text = $"[image.sz({w},{h},{pw},{ph},{fmt},{sizeLabel},p{page})]",
                IsImage = true,
                X = img.Position.Matrix.OffsetX,
                Y = img.Position.Matrix.OffsetY,
                Width = img.Width,
                Height = img.Height,
                FontSize = 0,
                PageIndex = _pageIndex
            });
        }

        public void Flush(bool isLineBreak = true)
        {
            if (!_hasContent) return;

            string finalString = _buffer.ToString();

            if (isLineBreak) Console.WriteLine(finalString);
            else Console.Write(finalString + " ");

            if (string.IsNullOrWhiteSpace(finalString))
            {
                _hasContent = false;
                return;
            }

            double totalWidth = _lastRightEdge - _startX;

            _targetList.Add(new ExtractedFragment
            {
                Text = finalString,
                X = _startX,
                Y = _startY,
                Width = totalWidth,
                FontSize = _fontSize,
                PageIndex = _pageIndex,
                IsImage = false
            });

            _hasContent = false;
        }
    }
}