using ABC.DiscoveryCity.Words.Common;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;

namespace ABC.DiscoveryCity.Words.Common
{
    // The Model
    public class DefinitionSense
    {
        // The Compound ID (e.g., "1", "1:a", "4:b")
        public string Id { get; set; }

        // The parsed content
        public Sentence Text { get; set; }

        public string PartOfSpeech { get; set; }
    }

    // The Entry Container (e.g., for the word "and")
    public class DictionaryEntry
    {
        public string Headword { get; set; } = string.Empty;
        public List<DefinitionSense> Senses { get; set; } = new();
    }

    // The Library Storage
    public static class DefinitionLibrary
    {

        // 1. The Lookup (O(1) Access)
        public static Dictionary<string, DictionaryEntry> Store = new(StringComparer.OrdinalIgnoreCase);
                

        // 2. The Scan List (Fast Iteration)
        public static ImmutableArray<DictionaryEntry> LinearStore;
        public static void Load(string headword, string rawText)
        {
            var entry = ParseDefinition(headword, rawText);
            Store[headword] = entry;
            FinalizeStore();
        }
        // Call this once after loading all 102k definitions
        public static void FinalizeStore()
        {
            // Sort by Headword for binary search potential, or just keep flat
            var builder = ImmutableArray.CreateBuilder<DictionaryEntry>(Store.Count);

            foreach (var kvp in Store)
            {
                builder.Add(kvp.Value);
            }

            // Sorting makes "Starts With" queries even faster later (Binary Search)
            builder.Sort((a, b) => string.CompareOrdinal(a.Headword, b.Headword));

            LinearStore = builder.ToImmutable();
        }
        // Helper to retrieve specific sentence
        public static Sentence? GetDefinition(string key)
        {
            // Key format: "headword:senseId" (e.g. "and:3")
            // Handle colon in headword? Assuming simple format for now.
            // Split by last colon?
            
            // Simple approach as per prompt
            var parts = key.Split(':'); 
            // Warning: Split behavior if headword has colon. 
            // Taking limit 2 might not be enough if headword has colon.
            // Let's assume standard dictionary headwords.
            
            if (parts.Length < 2) return null;
            
            string head = parts[0]; 
            // If multiple parts, maybe headword had colon? 
            // "and:3" -> ["and", "3"]
            
            string id = parts[1]; // And assuming id is the rest or just 2nd part?
            // The prompt implies a simple format.

            if (Store.TryGetValue(head, out var entry))
            {
                var sense = entry.Senses.FirstOrDefault(s => s.Id == id);
                return sense?.Text;
            }
            return null;
        }





public static DictionaryEntry ParseDefinition(string headword, string rawContent)
    {
        var entry = new DictionaryEntry { Headword = headword };
        var lines = rawContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // ROBUST REGEX:
        // 1. ([a-z])\.     Matches " a. "
        // 2. \(([a-z])\)   Matches " (a) "
        // 3. ([A-Z])\.     Matches " A. "
        // 4. ([IVX]+)\.    Matches " I. " or " IV. "
        // Note: We wrap the whole thing in \s+ ... \s+ to ensure it's a standalone marker, not part of a word.
        Regex subSenseRegex = new Regex(@"\s+(?:([a-z])\.|(?:\(([a-z])\))|([A-Z])\.|([IVX]+)\.)\s+", RegexOptions.Compiled);

        foreach (var line in lines)
        {
            // 1. Primary Split (e.g. "4. () ...")
            var match = Regex.Match(line, @"^(\d+)\.\s*\(\)\s*(.*)");
            if (!match.Success) continue;

            string mainId = match.Groups[1].Value;
            string content = match.Groups[2].Value;

            // Clean HTML
            content = System.Net.WebUtility.HtmlDecode(content).Replace("&#038;", "&");

            // 2. Split by ANY detected sub-marker
            var subParts = subSenseRegex.Split(content);

            // If subParts has more than 1 element, we found markers.
            // Array structure will be:
            // [0] = Preamble text
            // [1] = Captured Group (The Marker Letter/Number) - NOTE: Regex.Split includes captures!
            // [2] = The Text following the marker
            // ... repeating ...

            if (subParts.Length > 1)
            {
                // A. Handle Preamble (Text before the first sub-marker)
                if (!string.IsNullOrWhiteSpace(subParts[0]))
                {
                    AddSense(entry, mainId, subParts[0]);
                }

                // B. Handle Sub-Senses
                // We iterate by 2 because Regex.Split inserts the Capture Group between the text chunks.
                // CAUTION: Regex.Split with multiple groups returns EMPTY strings for the groups that didn't match.
                // We need to find which group actually captured the value.

                int textIndex = 1;
                while (textIndex < subParts.Length)
                {
                    // Find the marker (it will be one of the next few items because of the 4 capture groups)
                    // We need to skip empty captures to find the actual marker string.
                    string marker = "";

                    // Regex.Split puts ALL groups in the array. 
                    // Since we have 4 groups, we might see: "", "", "A", "" (if Group 3 matched).
                    // We scan forward until we find the text chunk, collecting the marker along the way.

                    int safety = 0;
                    while (string.IsNullOrEmpty(marker) && textIndex < subParts.Length && safety < 5)
                    {
                        marker = subParts[textIndex];
                        textIndex++;
                        safety++;
                    }

                    if (textIndex >= subParts.Length) break;

                    string text = subParts[textIndex];
                    textIndex++;

                    // Build ID: "4:a" or "4:I"
                    string compoundId = $"{mainId}:{marker}";
                    AddSense(entry, compoundId, text);
                }
            }
            else
            {
                // No sub-markers found, just add normally
                AddSense(entry, mainId, content);
            }
        }

        return entry;
    }

        private static void AddSense(DictionaryEntry entry, string id, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var buffer = new List<Word>();
            int ordinal = 1;

            // --- STEP 1: Reconstruct the Label from the ID ---
            // ID "2:I:a" becomes words: "2. I. a. "
            // This ensures the definition text is self-contained.

            var parts = id.Split(':');
            foreach (var part in parts)
            {
                // Add the Marker (e.g. "2")
                buffer.Add(new Word(part, null, buffer.Count, ordinal++));

                // Add the Dot (".")
                buffer.Add(new Word(".", null, buffer.Count, ordinal++));

                // Add a Space (" ")
                buffer.Add(new Word(" ", null, buffer.Count, ordinal++));
            }

            // --- STEP 2: Parse the Content ---
            text.AsSpan().Tokenize(buffer, ref ordinal, context: null);

            // --- STEP 3: Store ---
            entry.Senses.Add(new DefinitionSense
            {
                Id = id,
                Text = new Sentence(buffer, takeOwnership: false),
                PartOfSpeech = DetectPOS(text)
            });
        }
        // Helper to bridge string -> Word[]
        private static void ParseStringIntoBuffer(string text, List<Word> buffer)
        {
            // Use a temp ordinal counter just for this definition
            int tempOrdinal = 1;

            // Call the shared logic
            // We pass 'null' for context so Words use their "Backpack" logic (or pass a specific Def Context)
            WordTextParser.Tokenize(text.AsSpan(), buffer, ref tempOrdinal, context: null);
        }

        private static string DetectPOS(string content)
        {
            // Simple heuristic
            if (content.Contains("conjunction.", StringComparison.OrdinalIgnoreCase)) return "conjunction";
            if (content.Contains("noun.", StringComparison.OrdinalIgnoreCase)) return "noun";
            if (content.Contains("verb.", StringComparison.OrdinalIgnoreCase)) return "verb";
            if (content.Contains("adjective.", StringComparison.OrdinalIgnoreCase)) return "adjective";
            if (content.Contains("adverb.", StringComparison.OrdinalIgnoreCase)) return "adverb";
            return "unknown";
        }
    }
}
