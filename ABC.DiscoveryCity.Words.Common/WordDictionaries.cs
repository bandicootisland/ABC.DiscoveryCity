using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ABC.DiscoveryCity.Words.Common;

namespace ABC.DiscoveryCity.Words.Common
{
    public record InflectionData(string Text, string PartOfSpeech);
    public class WordGrammar
    {
        public Word Headword { get; set; }
        public List<string> Inflections { get; set; } = new List<string>();
        public List<DictionaryDefinition> Definitions { get; set; } = new List<DictionaryDefinition>();

        public List<InflectionData> InflectionData { get; set; }
    }

    public class DictionaryDefinition
    {
        public string DefinitionNumber { get; set; } = "";
        public string PartOfSpeech { get; set; } = "";
        public string PartOfSpeechExpanded => WordDictionaries.ExpandAbbreviation(PartOfSpeech);
        public Sentence Text { get; set; }
        public List<Sentence> Synonyms { get; set; } = new List<Sentence>();
        public List<Sentence> Examples { get; set; } = new List<Sentence>();
        public bool IsIdiom { get; set; }
        public string IdiomLabel { get; set; } = "";
    }

    public class WordDictionaries
    {
        private static readonly Dictionary<string, string> Abbreviations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "adj", "adjective" },
            { "adv", "adverb" },
            { "archaeol", "archaeology" },
            { "archit", "architecture" },
            { "astrol", "astrology" },
            { "astron", "astronomy" },
            { "Austral", "Australian" },
            { "bacteriol", "bacteriology" },
            { "biochem", "biochemistry" },
            { "biol", "biology" },
            { "Brit", "Britain, British" },
            { "Canad", "Canadian" },
            { "cap", "capital (letter)" },
            { "chem", "chemistry" },
            { "conj", "conjunction" },
            { "E", "East(ern)" },
            { "econ", "economics" },
            { "esp.", "especially" },
            { "etc.", "et cetera" },
            { "fem", "feminine" },
            { "foll.", "followed" },
            { "geog", "geography" },
            { "geol", "geology" },
            { "geom", "geometry" },
            { "interj", "interjection" },
            { "kg", "kilogram(s)" },
            { "km", "kilometre(s)" },
            { "masc", "masculine" },
            { "maths", "mathematics" },
            { "med", "medicine" },
            { "meteorol", "meteorology" },
            { "myth", "mythology" },
            { "n", "noun" },
            { "N", "North(ern)" },
            { "naut", "nautical" },
            { "NZ", "New Zealand" },
            { "ornithol", "ornithology" },
            { "pathol", "pathology" },
            { "pharmacol", "pharmacology" },
            { "physiol", "physiology" },
            { "pl", "plural" },
            { "prep", "preposition" },
            { "pron", "pronoun" },
            { "psychol", "psychology" },
            { "RC", "Roman Catholic" },
            { "S", "South(ern)" },
            { "Scot", "Scottish" },
            { "sing", "singular" },
            { "sociol", "sociology" },
            { "theol", "theology" },
            { "US", "United States" },
            { "vb", "verb" },
            { "W", "West(ern)" },
            { "zool", "zoology" }
        };

        public static string ExpandAbbreviation(string abbr)
        {
            if (string.IsNullOrEmpty(abbr)) return "";
            return Abbreviations.TryGetValue(abbr, out var expanded) ? expanded : abbr;
        }

        //public List<WordGrammar> Parse(ImmutableArray<Word> words)
        //{
        //    var entries = new List<WordGrammar>();
            
        //    // Dictionary Word Boundary <font size="+1" color="#0038A8"><b>agamogenesis</b></font>
        //    // Optimized zero-allocation loop
        //    ReadOnlySpan<char> boundaryMarker = "<font size=\"+1\" color=\"#0038A8\"".AsSpan();
        //    ReadOnlySpan<char> boldTag = "<b>".AsSpan();

        //    for (int i = 0; i < words.Length - 1; i++)
        //    {
        //        // Check for boundary marker and subsequent bold tag
        //        if (words[i].span.Contains(boundaryMarker, StringComparison.Ordinal) && 
        //            words[i + 1].span.SequenceEqual(boldTag))
        //        {
        //            // Found boundary
        //            // Calculate the next boundary
        //            int j = words.Length;
        //            for (int k = i + 1; k < words.Length - 1; k++)
        //            {
        //                if (words[k].span.Contains(boundaryMarker, StringComparison.Ordinal) && 
        //                    words[k + 1].span.SequenceEqual(boldTag))
        //                {
        //                    j = k;
        //                    break;
        //                }
        //            }
                    
        //            // Trim j to exclude next entry's container tags (e.g. <p><span>)
        //            int entryEnd = j;
        //            if (entryEnd > i)
        //            {
        //                // Check for preceding <span>
        //                if (entryEnd > 0 && words[entryEnd - 1].text.StartsWith("<span", StringComparison.OrdinalIgnoreCase))
        //                {
        //                    entryEnd--;
        //                }
        //                // Check for preceding <p>
        //                if (entryEnd > 0 && words[entryEnd - 1].text.StartsWith("<p", StringComparison.OrdinalIgnoreCase))
        //                {
        //                    entryEnd--;
        //                }
        //            }

        //            var entry = ParseEntry(words,i, entryEnd);
        //            if (!string.IsNullOrEmpty(entry.Headword.text))
        //            {
        //                entries.Add(entry);
        //            }
                    
        //            // Move i to j - 1 because the loop will increment i
        //            i = j - 1;
        //        }
        //    }

        //    return entries;
        //}

        //public List<WordGrammar> ParseDebug(string[] targetWords, ImmutableArray<Word> words)
        //{
        //    var entries = new List<WordGrammar>();
        //    var targets = new HashSet<string>(targetWords, StringComparer.OrdinalIgnoreCase);
        //    var grammerparser = new CollinsDictionaryParser();
        //    // Dictionary Word Boundary <font size="+1" color="#0038A8"><b>agamogenesis</b></font>
        //    ReadOnlySpan<char> boundaryMarker = "<font size=\"+1\" color=\"#0038A8\"".AsSpan();
        //    ReadOnlySpan<char> boldTag = "<b>".AsSpan();

        //    for (int i = 0; i < words.Length - 1; i++)
        //    {
        //        // Check for boundary marker and subsequent bold tag
        //        if (words[i].span.Contains(boundaryMarker, StringComparison.Ordinal) && 
        //            words[i + 1].span.SequenceEqual(boldTag))
        //        {
        //            // Found boundary
        //            // Calculate the next boundary
        //            int j = words.Length;
        //            for (int k = i + 1; k < words.Length - 1; k++)
        //            {
        //                if (words[k].span.Contains(boundaryMarker, StringComparison.Ordinal) && 
        //                    words[k + 1].span.SequenceEqual(boldTag))
        //                {
        //                    j = k;
        //                    break;
        //                }
        //            }
                    
        //            // Trim j to exclude next entry's container tags (e.g. <p><span>)
        //            int entryEnd = j;
        //            if (entryEnd > i)
        //            {
        //                // Check for preceding <span>
        //                if (entryEnd > 0 && words[entryEnd - 1].text.StartsWith("<span", StringComparison.OrdinalIgnoreCase))
        //                {
        //                    entryEnd--;
        //                }
        //                // Check for preceding <p>
        //                if (entryEnd > 0 && words[entryEnd - 1].text.StartsWith("<p", StringComparison.OrdinalIgnoreCase))
        //                {
        //                    entryEnd--;
        //                }
        //            }

        //            // Check if this is a target word
        //            if (i + 2 < entryEnd)
        //            {
        //                string headword = words[i + 2].text;
        //                if (targets.Contains(headword))
        //                {
        //                    Console.WriteLine($"[ParserDebug] Found target '{headword}' at index {i}. Range: {i}-{entryEnd}");
        //                    var entry = ParseEntry(words,i,entryEnd);
        //                    if (!string.IsNullOrEmpty(entry.Headword.text))
        //                    {
        //                        entries.Add(entry);
        //                        Console.WriteLine($"[ParserDebug] Parsed '{headword}' with {entry.Definitions.Count} definitions.");
        //                    }
        //                }
        //                else
        //                {
        //                    // Skip this entry
        //                    i = j - 1;
        //                }
        //            }
        //            else
        //            {
        //                i = j - 1;
        //            }
        //        }
        //    }

        //    return entries;
        //}

        
        //private WordGrammar ParseEntry(System.Collections.Immutable.ImmutableArray<Word> words, int i,int to)
        //{
        //    var entry = new WordGrammar();
        //    string currentPos = "";

        //    var grammarparser = new CollinsDictionaryParser();
        //    return grammarparser.ParseEntry(words, i, to);

        //    // Scan until </article>
        ////    while (i < words.Length)
        ////    {
        ////        var w = words[i];
        ////        string s = w.text;

        ////        if (s.StartsWith("</article>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            break;
        ////        }

        ////        // Headword: <dfn>word</dfn>
        ////        if (s.StartsWith("<dfn>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            // The next word(s) until </dfn> is the headword
        ////            // Usually it's just one word or a phrase inside
        ////            // But Words.LoadHtml splits tags. So <dfn> is one word, content is next.
                    
        ////            // Check if <dfn> contains the word directly (e.g. if LoadHtml didn't split perfectly or if it's <dfn>word</dfn>)
        ////            // But LoadHtml splits on < and >.
        ////            // So we expect: "<dfn>", "word", "</dfn>"
                    
        ////            // Actually, let's look ahead
        ////            int j = i + 1;
        ////            while (j < words.Length && !words[j].text.StartsWith("</dfn>", StringComparison.OrdinalIgnoreCase))
        ////            {
        ////                if (!words[j].text.StartsWith("<"))
        ////                {
        ////                    entry.Headword = words[j]; // Take the first text word as the main headword link
        ////                    break; 
        ////                }
        ////                j++;
        ////            }
        ////        }

        ////        // SKM: <!--SKM:...-->
        ////        if (s.StartsWith("<!--SKM:", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            var match = Regex.Match(s, @"<!--SKM:([^|]+)\|([^>]*)-->");
        ////            if (match.Success)
        ////            {
        ////                string formsRaw = match.Groups[2].Value;
        ////                var forms = formsRaw.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        ////                entry.Inflections.AddRange(forms);
        ////            }
        ////        }

        ////        // Part of Speech: <i>vb</i>
        ////        if (s.StartsWith("<i>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            // Peek content
        ////            int j = i + 1;
        ////            var posBuilder = new StringBuilder();
        ////            while (j < words.Length && !words[j].text.StartsWith("</i>", StringComparison.OrdinalIgnoreCase))
        ////            {
        ////                if (!words[j].text.StartsWith("<"))
        ////                {
        ////                    posBuilder.Append(words[j].text);
        ////                }
        ////                j++;
        ////            }
        ////            string pos = posBuilder.ToString().Trim();
        ////            if (!string.IsNullOrEmpty(pos))
        ////            {
        ////                currentPos = pos;
        ////            }
        ////        }

        ////        // Definition Number: <b>1</b> or <b>A</b>
        ////        // Note: <b>falling</b> is also bold. We need to distinguish numbers.
        ////        bool isNumberedDef = false;
        ////        if (s.StartsWith("<b>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            // Check content
        ////            int j = i + 1;
        ////            string content = "";
        ////            while (j < words.Length && !words[j].text.StartsWith("</b>", StringComparison.OrdinalIgnoreCase))
        ////            {
        ////                content += words[j].text;
        ////                j++;
        ////            }
                    
        ////            // Is it a number or single letter?
        ////            if (Regex.IsMatch(content, @"^\d+$") || (content.Length == 1 && char.IsUpper(content[0])))
        ////            {
        ////                isNumberedDef = true;
        ////                // Start of a definition
        ////                var def = new DictionaryDefinition
        ////                {
        ////                    DefinitionNumber = content,
        ////                    PartOfSpeech = currentPos
        ////                };
                        
        ////                // Parse definition body
        ////                // Advance i to end of </b>
        ////                while (i < words.Length && !words[i].text.StartsWith("</b>", StringComparison.OrdinalIgnoreCase)) i++;
                        
        ////                ParseDefinitionBodyWithSentences(words, ref i, def, ref currentPos);
        ////                entry.Definitions.Add(def);
        ////                continue; // ParseDefinitionBody advances i
        ////            }
        ////        }

        ////        // Unnumbered definition?
        ////        if (!string.IsNullOrEmpty(currentPos) && !isNumberedDef)
        ////        {
        ////            bool isContent = !s.StartsWith("<") || 
        ////                             s.StartsWith("<small>", StringComparison.OrdinalIgnoreCase) ||
        ////                             (s.StartsWith("<span", StringComparison.OrdinalIgnoreCase) && s.Contains("class=\"ex\""));

        ////            if (isContent)
        ////            {
        ////                var def = new DictionaryDefinition
        ////                {
        ////                    DefinitionNumber = "",
        ////                    PartOfSpeech = currentPos
        ////                };
                        
        ////                // ParseDefinitionBodyWithSentences expects to start AFTER the tag that triggered it (usually </b>).
        ////                // It does i++ at the start.
        ////                // So we pass i-1 so that it starts processing at i.
        ////                int startI = i - 1;
        ////                ParseDefinitionBodyWithSentences(words, ref startI, def, ref currentPos);
        ////                i = startI;
                        
        ////                if (!def.Text.words.IsDefaultOrEmpty || def.Synonyms.Count > 0 || def.Examples.Count > 0)
        ////                {
        ////                    entry.Definitions.Add(def);
        ////                }
        ////                continue;
        ////            }
        ////        }

        ////        i++;
        ////    }

        ////    return entry;
        ////}

        ////private void ParseDefinitionBodyWithSentences(System.Collections.Immutable.ImmutableArray<Word> words, ref int i, DictionaryDefinition def, ref string currentPos)
        ////{
        ////    // Read until next <b> (start of next def) or </article> or <small>SYNONYMS</small>
        ////    // Actually synonyms are part of the definition block.
            
        ////    var textWords = new List<Word>();
            
        ////    // We are currently at </b>. Move to next.
        ////    i++; 

        ////    while (i < words.Length)
        ////    {
        ////        var w = words[i];
        ////        string s = w.text;

        ////        if (s.StartsWith("</article>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            i--; // Back up so the outer loop sees it
        ////            break;
        ////        }
                
        ////        // Check for next definition start: <b>number</b>
        ////        if (s.StartsWith("<b>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            // Check if it's a number/letter
        ////            int j = i + 1;
        ////            string content = "";
        ////            while (j < words.Length && !words[j].text.StartsWith("</b>", StringComparison.OrdinalIgnoreCase))
        ////            {
        ////                content += words[j].text;
        ////                j++;
        ////            }
        ////            if (Regex.IsMatch(content, @"^\d+$") || (content.Length == 1 && char.IsUpper(content[0])))
        ////            {
        ////                i--; // Back up
        ////                break;
        ////            }
        ////        }

        ////        // Check for Part of Speech change: <i>vb</i>
        ////        if (s.StartsWith("<i>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            // Peek content
        ////            int j = i + 1;
        ////            var posBuilder = new StringBuilder();
        ////            while (j < words.Length && !words[j].text.StartsWith("</i>", StringComparison.OrdinalIgnoreCase))
        ////            {
        ////                if (!words[j].text.StartsWith("<"))
        ////                {
        ////                    posBuilder.Append(words[j].text);
        ////                }
        ////                j++;
        ////            }
        ////            string pos = posBuilder.ToString().Trim();
                    
        ////            // If it's a known abbreviation, treat as POS change
        ////            // Or if it's short and looks like one.
        ////            // The dictionary has "n", "vb", "adj", etc.
        ////            if (Abbreviations.ContainsKey(pos) || pos == "n" || pos == "vb") 
        ////            {
        ////                currentPos = pos;
        ////                // Skip this tag and content
        ////                i = j + 1; // j is at </i>, so j+1 is next
        ////                // Also skip preceding "▸" if it exists?
        ////                // It might have been added to textBuilder already.
        ////                // If the last char in textBuilder is '▸', remove it.
        ////                // But '▸' might be a separate word.
        ////                continue;
        ////            }
        ////        }

        ////        // Synonyms: <small>SYNONYMS</small>
        ////        if (s.StartsWith("<small>", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            // Check content
        ////            int j = i + 1;
        ////            string content = "";
        ////            while (j < words.Length && !words[j].text.StartsWith("</small>", StringComparison.OrdinalIgnoreCase))
        ////            {
        ////                content += words[j].text;
        ////                j++;
        ////            }
        ////            if (content == "SYNONYMS")
        ////            {
        ////                // Parse synonyms
        ////                // Skip </small> and :
        ////                while (i < words.Length && !words[i].text.StartsWith(":", StringComparison.OrdinalIgnoreCase)) i++;
        ////                i++; // Skip :
                        
        ////                var synWords = new List<Word>();
        ////                while (i < words.Length)
        ////                {
        ////                    if (words[i].text.StartsWith("<br", StringComparison.OrdinalIgnoreCase) || 
        ////                        words[i].text.StartsWith("<b>", StringComparison.OrdinalIgnoreCase) ||
        ////                        words[i].text.StartsWith("</article>", StringComparison.OrdinalIgnoreCase))
        ////                    {
        ////                        i--;
        ////                        break;
        ////                    }
                            
        ////                    // Split by comma
        ////                    if (words[i].text == ",")
        ////                    {
        ////                        if (synWords.Count > 0)
        ////                        {
        ////                            def.Synonyms.Add(new Sentence(synWords));
        ////                            synWords.Clear();
        ////                        }
        ////                    }
        ////                    else if (!words[i].text.StartsWith("<"))
        ////                    {
        ////                        synWords.Add(words[i]);
        ////                    }
        ////                    i++;
        ////                }
        ////                // Add last synonym
        ////                if (synWords.Count > 0)
        ////                {
        ////                    def.Synonyms.Add(new Sentence(synWords));
        ////                }
        ////                continue;
        ////            }
        ////        }

        ////        // Examples: <span class="ex">
        ////        if (s.StartsWith("<span", StringComparison.OrdinalIgnoreCase) && s.Contains("class=\"ex\""))
        ////        {
        ////            var exWords = new List<Word>();
        ////            i++; // Skip span tag
        ////            while (i < words.Length && !words[i].text.StartsWith("</span>", StringComparison.OrdinalIgnoreCase))
        ////            {
        ////                if (!words[i].text.StartsWith("<"))
        ////                {
        ////                    exWords.Add(words[i]);
        ////                }
        ////                i++;
        ////            }
        ////            if (exWords.Count > 0)
        ////            {
        ////                def.Examples.Add(new Sentence(exWords));
        ////            }
        ////            continue;
        ////        }

        ////        // Normal text
        ////        if (!s.StartsWith("<"))
        ////        {
        ////            textWords.Add(w);
        ////        }
        ////        else if (s.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
        ////        {
        ////            // Break might mean end of def text?
        ////            // Often synonyms follow <br>
        ////            // textBuilder.Append(" ");
        ////        }

        ////        i++;
        ////    }

        ////    if (textWords.Count > 0)
        ////    {
        ////        def.Text = new Sentence(textWords);
        ////    }
        //}
    }
}
