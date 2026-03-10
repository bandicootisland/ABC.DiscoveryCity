using ABC.DiscoveryCity.DocumentProcessing.Shared;
using ABC.DiscoveryCity.TelerikProcessing;
using ABC.DiscoveryCity.Words.Common;

using ABC.WordCity.Words.Common;
using ABC.WordCity.Words.Common.Layers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ABC.DiscoveryCity.TelerikProcessing
{
    public class CorpusIngestor
    {
        public List<Sentence> ResultSentences { get; } = new List<Sentence>();
        public List<Word> ResultWords { get; } = new List<Word>();

        private readonly List<Word> _currentSentenceBuffer = new List<Word>(50);
        private SentenceData _currentSentenceData;
        private int _wordOrdinal = 1;
        private int _sentenceOrdinal = 1;
        private static readonly string StringSpace = " ";
        private WordLayers _corpusLayers = WordLayers.Empty; // Output Layers (Word-based)
        private WordLayers _tokenLayers = WordLayers.Empty;  // Input Layers (Token-based)
        private ArtifactMarker[] _artifacts = Array.Empty<ArtifactMarker>(); // Detected visual artifacts
        private LayoutToken[] _layout = Array.Empty<LayoutToken>(); // Current layout for dynamic char width

        public void Parse(BookCorpus corpus)
        {
            _tokenLayers = corpus.Layers ?? WordLayers.Empty;
            _corpusLayers = new WordLayers(); // Create new output layers
            _artifacts = corpus.Artifacts ?? Array.Empty<ArtifactMarker>();
            Parse(new BookRawBuffer { 
                Content = corpus.Content, 
                Layout = corpus.Layout,
                Artifacts = _artifacts
            });
        }

        public void Parse(BookRawBuffer buffer)
        {
            ResultSentences.Clear();
            ResultWords.Clear();
            _currentSentenceBuffer.Clear();
            _wordOrdinal = 1;
            _sentenceOrdinal = 1;
            _currentSentenceData = new SentenceData { Ordinal = _sentenceOrdinal, Layers = _corpusLayers };
            _artifacts = buffer.Artifacts ?? Array.Empty<ArtifactMarker>();
            _layout = buffer.Layout ?? Array.Empty<LayoutToken>();

            ReadOnlyMemory<char> allText = new ReadOnlyMemory<char>(buffer.Content);
            int lastTokenIndex = -1; // tracks previous non-skipped token for gap detection

            for (int i = 0; i < buffer.Layout.Length; i++)
            {
                LayoutToken curr = buffer.Layout[i];

                // [IMAGE HANDLING]
                // Check if this token corresponds to an image in the physical layer
                var imgMeta = _tokenLayers.GetImage(i);
                if (!imgMeta.IsEmpty)
                {
                    // Use the token text which contains DSL format [image.sz(...)] from SmartFragmentBuilder
                    string imageMarker = new string(allText.Slice(curr.TextOffset, curr.TextLength).Span);
                    if (string.IsNullOrEmpty(imageMarker)) imageMarker = "[image.sz(0,0,0,0,?,0KB,p0)]";
                    AddWord(imageMarker);
                    if (ResultWords.Count > 0)
                    {
                        var w = ResultWords[ResultWords.Count - 1];
                        _corpusLayers.AddImage(w.Ordinal, imgMeta);
                    }
                    lastTokenIndex = i;
                    continue;
                }

                // Skip text fragments hidden under redaction bars.
                // This prevents stray characters (e.g. HTML tags from email source)
                // from leaking into output, and lets gap detection see the full
                // redacted span so it can insert a proper [redact.char(N)] marker.
                if (IsUnderRedaction(curr))
                    continue;

                if (lastTokenIndex >= 0)
                {
                    LayoutToken prev = buffer.Layout[lastTokenIndex];

                    // Force sentence break at page boundaries - the last text on a page
                    // ends the current sentence (no punctuation added, text stays as-is)
                    if (prev.PageIndex != curr.PageIndex)
                    {
                        CloseSentence();
                    }

                    // Get previous token text for email context detection
                    string prevTokenText = new string(allText.Slice(prev.TextOffset, prev.TextLength).Span);
                    DetectFormattingAndSpace(prev, curr, i, prevTokenText);
                }

                ReadOnlyMemory<char> tokenText = allText.Slice(curr.TextOffset, curr.TextLength);
                ProcessTextFragment(tokenText);
                lastTokenIndex = i;
            }

            CloseSentence();
        }

        private void DetectFormattingAndSpace(LayoutToken prev, LayoutToken curr, int tokenIndex, string? previousTokenText)
        {
            // [Layout Logic Same as Previous]
            bool isPageBreak = prev.PageIndex != curr.PageIndex;
            double yDiff = curr.Y - prev.Y;
            double lineHeight = prev.FontSize;

            FormatType detectedFormat = FormatType.None;
            if (isPageBreak) detectedFormat = FormatType.SectionBreak;
            else if (yDiff > (lineHeight * 1.4)) detectedFormat = FormatType.ParagraphBreak;
            else if (yDiff > (lineHeight * 0.5)) detectedFormat = FormatType.LineBreak;

            if (detectedFormat != FormatType.None && ResultWords.Count > 0)
            {
                var lastWord = ResultWords[ResultWords.Count - 1];
                _corpusLayers.AddFormatting(lastWord.Ordinal, detectedFormat);
            }

            bool addSpace = false;
            if (detectedFormat != FormatType.None)
            {
                if (ResultWords.Count > 0)
                {
                    var lastWord = ResultWords[ResultWords.Count - 1];
                    bool endsInHyphen = lastWord.text.EndsWith("-") || lastWord.text.EndsWith("\u00AD");
                    if (!endsInHyphen) addSpace = true;
                }
                else addSpace = true;
            }
            else
            {
                double prevRight = prev.X + prev.Width;
                double gap = curr.X - prevRight;
                
                // Check for email context to use lower threshold
                string prevText = previousTokenText ?? "";
                bool isEmailContext = RedactionExtensions.IsEmailContext(prevText);
                
                // Check for redaction based on gap (with email context sensitivity)
                if (RedactionExtensions.IsLikelyRedaction(gap, curr.FontSize, isEmailContext))
                {
                    bool hasRedactionArtifacts = _artifacts.Length > 0 && 
                        _artifacts.Any(a => a.Type == ArtifactType.Redaction);
                    
                    if (hasRedactionArtifacts)
                    {
                        // We have detected redaction bars - require overlap for confirmation
                        var overlappingArtifact = RedactionExtensions.FindOverlappingArtifact(
                            prevRight, curr.X, curr.Y, curr.PageIndex, _artifacts);
                        
                        if (overlappingArtifact.HasValue)
                        {
                            int charCount = RedactionExtensions.EstimateRedactedCharCount(gap, curr.FontSize, _layout, tokenIndex);
                            string marker = isEmailContext 
                                ? RedactionExtensions.CreateRedactionMarker(charCount, "email") 
                                : RedactionExtensions.CreateRedactionMarker(charCount);
                            AddWord(marker);
                            addSpace = true;
                        }
                        else if (gap > (curr.FontSize * 0.2))
                        {
                            addSpace = true;
                        }
                    }
                    else
                    {
                        // No redaction artifacts detected - use gap-based heuristic only
                        int charCount = RedactionExtensions.EstimateRedactedCharCount(gap, curr.FontSize, _layout, tokenIndex);
                        string marker = isEmailContext 
                            ? RedactionExtensions.CreateRedactionMarker(charCount, "email") 
                            : RedactionExtensions.CreateRedactionMarker(charCount);
                        AddWord(marker);
                        addSpace = true;
                    }
                }
                else if (gap > (curr.FontSize * 0.2))
                {
                    addSpace = true;
                }
            }

            if (addSpace && _currentSentenceBuffer.Count > 0)
            {
                AddWord(StringSpace);
            }
        }

        private void ProcessTextFragment(ReadOnlyMemory<char> fragmentMem)
        {
            ReadOnlySpan<char> span = fragmentMem.Span;
            int start = 0;

            for (int k = 0; k < span.Length; k++)
            {
                char c = span[k];
                bool isPunct = char.IsPunctuation(c) || char.IsSymbol(c);
                bool isSpace = char.IsWhiteSpace(c);

                if (isPunct || isSpace)
                {
                    if (k > start) AddWord(fragmentMem.Slice(start, k - start));

                    if (isPunct)
                    {
                        AddWord(fragmentMem.Slice(k, 1));

                        char? nextChar = (k + 1 < span.Length) ? span[k + 1] : (char?)null;

                        // 1. Check Sentence End
                        CheckSentenceEnd(c, nextChar);

                        // 2. [NEW] Force Space Logic (Institute,publishing -> Institute, publishing)
                        // If punctuation is comma/colon/semicolon/dot AND next char is a Letter...
                        if (ShouldForceSpaceAfterPunct(c, nextChar))
                        {
                            if (_currentSentenceBuffer.Count > 0) AddWord(StringSpace);
                        }
                    }
                    else if (isSpace)
                    {
                        if (_currentSentenceBuffer.Count > 0)
                        {
                            AddWord(StringSpace);
                        }
                    }

                    start = k + 1;
                }
            }

            if (start < span.Length) AddWord(fragmentMem.Slice(start));
        }

        // [NEW HELPER]
        private bool ShouldForceSpaceAfterPunct(char c, char? nextChar)
        {
            // If there is no next char, or it's already whitespace, we don't need to force anything.
            if (!nextChar.HasValue || char.IsWhiteSpace(nextChar.Value)) return false;

            // Only force space if the NEXT thing is a Letter. 
            // This avoids splitting "1,000" (digit) or "http://google" (symbol)
            if (!char.IsLetter(nextChar.Value)) return false;

            // Check specific punctuation marks that usually require spacing
            return c == ',' || c == '.' || c == ';' || c == ':' || c == '!' || c == '?';
        }

        private void CheckSentenceEnd(char punct, char? nextChar)
        {
            if (punct == '!' || punct == '?')
            {
                CloseSentence(punct.ToString());
                return;
            }
            if (punct == '.')
            {
                // Lookahead: Refined Rule "Digit (3.5) or Lower (google.com)"
                if (nextChar.HasValue)
                {
                    // If next char is ANY non-whitespace, the original code returned.
                    // But we only want to skip if it's a DIGIT or LOWER case letter.
                    // "End.Start" (Upper) -> Should Split.
                    // "3.5" (Digit) -> Should Join.
                    // "google.com" (Lower) -> Should Join.
                    if (char.IsDigit(nextChar.Value) || char.IsLower(nextChar.Value)) return;
                }

                // Lookbehind: Initial check
                if (_currentSentenceBuffer.Count >= 2)
                {
                    var lastWord = _currentSentenceBuffer[_currentSentenceBuffer.Count - 2];
                    if (ABC.DiscoveryCity.Words.Common.Grammar.GrammarRules.IsAbbreviation(lastWord.span)) return;

                    // Middle Initial Check (Mary D.)
                    if (lastWord.span.Length == 1 && char.IsUpper(lastWord.span[0])) return;
                }
                CloseSentence(".");
            }
        }

        private void AddWord(ReadOnlyMemory<char> textMemory)
        {
            if (_currentSentenceBuffer.Count == 0 && textMemory.Span.IsWhiteSpace()) return;
            // Fidelity Mode: We accept " The" if the source has it.
            var w = new Word(textMemory, _currentSentenceData, _currentSentenceBuffer.Count, _wordOrdinal++);
            _currentSentenceBuffer.Add(w);
            ResultWords.Add(w);
        }

        private void AddWord(string text)
        {
            if (_currentSentenceBuffer.Count == 0 && string.IsNullOrWhiteSpace(text)) return;
            var w = new Word(text, _currentSentenceData, _currentSentenceBuffer.Count, _wordOrdinal++);
            _currentSentenceBuffer.Add(w);
            ResultWords.Add(w);
        }

        /// <summary>
        /// Returns true if the token's center falls within a detected redaction rectangle.
        /// Text under redaction bars is hidden visually but Telerik still extracts it
        /// (e.g. HTML tags from email source, partial email addresses).
        /// Suppressing these prevents stray text leaking and lets gap detection
        /// see the full redacted span for proper [redact.char(N)] markers.
        /// </summary>
        private bool IsUnderRedaction(LayoutToken token)
        {
            if (_artifacts.Length == 0) return false;

            double tokenCenterX = token.X + (token.Width / 2.0);

            foreach (var art in _artifacts)
            {
                if (art.Type != ArtifactType.Redaction) continue;
                if (art.PageIndex != token.PageIndex) continue;

                // Token center must be within the redaction bar's horizontal span
                if (tokenCenterX >= art.X && tokenCenterX <= art.Right && art.ContainsY(token.Y))
                    return true;
            }
            return false;
        }

        private void CloseSentence() => CloseSentence("");

        private void CloseSentence(string endChar)
        {
            if (_currentSentenceBuffer.Count == 0) return;

            _currentSentenceData.EndChar = endChar;
            _currentSentenceData.Words = _currentSentenceBuffer.ToArray();
            _currentSentenceData.Layers = _corpusLayers;

            ResultSentences.Add(new Sentence(_currentSentenceData));

            _currentSentenceBuffer.Clear();
            _sentenceOrdinal++;
            _currentSentenceData = new SentenceData
            {
                Ordinal = _sentenceOrdinal,
                Layers = _corpusLayers
            };
        }
    }

    
}