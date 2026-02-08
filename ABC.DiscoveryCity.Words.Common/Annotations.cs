using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace ABC.DiscoveryCity.Words.Common
{
    /// <summary>
    /// Represents a robust locator for a specific range of text within the corpus.
    /// It uses relative positioning and context to remain valid even if minor edits occur elsewhere.
    /// </summary>
    public record TextLocator
    {
        // The primary anchor: The ordinal of the first word in the selection.
        public int StartOrdinal { get; init; }
        
        // The length of the selection in words.
        public int Length { get; init; }

        // Context for validation/recovery
        public string SelectedTextHash { get; init; } // Hash of the selected words (ignoring punctuation)
        public string StartContextHash { get; init; } // Hash of the N words preceding the selection
        public string EndContextHash { get; init; }   // Hash of the N words following the selection
        
        // Sentence context for broader validation
        public int SentenceIndex { get; init; }       // The index of the sentence containing the start word
        public int WordIndexInSentence { get; init; } // The index of the start word within its sentence

        private const int ContextWindowSize = 5;

        public TextLocator(int startOrdinal, int length)
        {
            StartOrdinal = startOrdinal;
            Length = length;

            // Validate range
            if (startOrdinal < 1 || startOrdinal > WordStructs.words.Length)
                throw new ArgumentOutOfRangeException(nameof(startOrdinal));
            
            if (length < 1 || startOrdinal + length - 1 > WordStructs.words.Length)
                throw new ArgumentOutOfRangeException(nameof(length));

            // Calculate Hashes
            SelectedTextHash = ComputeHash(startOrdinal, length);
            StartContextHash = ComputeHash(startOrdinal - ContextWindowSize, ContextWindowSize);
            EndContextHash = ComputeHash(startOrdinal + length, ContextWindowSize);

            // Calculate Sentence Context
            var startWord = WordStructs.words[startOrdinal - 1];
            //SentenceIndex = startWord.SentenceOrdinal - 1;
            
            // Find word index in sentence
            var sentence = startWord.ToSentence();
            if (!sentence.words.IsEmpty)
            {
                for (int i = 0; i < sentence.words.Length; i++)
                {
                    if (sentence.words[i].Ordinal == startOrdinal)
                    {
                        WordIndexInSentence = i;
                        break;
                    }
                }
            }
        }

        private string ComputeHash(int startOrdinal, int count)
        {
            if (count <= 0) return string.Empty;

            var sb = new StringBuilder();
            int end = startOrdinal + count;
            
            for (int i = startOrdinal; i < end; i++)
            {
                if (i < 1 || i > WordStructs.words.Length) continue;
                
                var w = WordStructs.words[i - 1];
                if (!w.IsPunctuation)
                {
                    sb.Append(w.text);
                }
            }
            // Simple hash for demonstration; in production, use a stable hash algorithm
            return sb.ToString().GetHashCode().ToString("X");
        }

        /// <summary>
        /// Verifies if this locator is still valid against the current text corpus.
        /// </summary>
        public bool IsValid()
        {
            // 1. Check exact ordinal match
            if (StartOrdinal + Length - 1 <= WordStructs.words.Length)
            {
                string currentHash = ComputeHash(StartOrdinal, Length);
                if (currentHash == SelectedTextHash) return true;
            }

            // 2. Fallback: Try to find by context (Not implemented fully, but this is where the "smart" logic goes)
            // e.g., Scan nearby sentences for matching context hashes
            
            return false;
        }
    }

    /// <summary>
    /// Represents an annotation attached to a specific text range.
    /// </summary>
    public record Annotation
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public TextLocator Locator { get; init; }
        public string Content { get; init; }
        public string Type { get; init; } // e.g., "Note", "Grammar", "Highlight"
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        public Annotation(TextLocator locator, string content, string type = "Note")
        {
            Locator = locator;
            Content = content;
            Type = type;
        }
    }
}
