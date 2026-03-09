using System.Text.RegularExpressions;
using ABC.DiscoveryCity.Words.Common.Processing;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline;

public static class TextCleaner
{
    public static List<string> CleanSentences(List<string> input)
    {
        // Phase 1: Clean text (unicode fixes, EFTA splits)
        var cleaned = new List<string>();
        foreach (var line in input)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // 1. Remove/Replace Unicodes
            string s = line.Replace("\u25A0", "-").Replace("\"", "'");

            // NOTE: Do NOT clean MIME artifacts from sentences - raw text is evidence.

            // 2. Fix OCR text artifacts: bracket spaces "( M"->"(M", URL spaces
            s = SentencePostProcessor.CleanTextArtifacts(s);

            // 3. Split on EFTA file IDs - they appear at page headers/footers
            //    and the OCR runs them into the next text: "EFTA0033210Original message"
            //    becomes separate entries: "EFTA0033210", "Original message"
            if (Regex.IsMatch(s, @"EFTA\d{8,}"))
            {
                var parts = Regex.Split(s, @"(EFTA\d{8,})");
                foreach (var part in parts)
                {
                    var p = part.Trim();
                    if (!string.IsNullOrWhiteSpace(p))
                        cleaned.Add(p);
                }
            }
            else
            {
                cleaned.Add(s.Trim());
            }
        }

        // Phase 1.5: Merge ellipsis fragments
        cleaned = SentencePostProcessor.MergeEllipsisSentences(cleaned);

        // Phase 2: Filter junk BEFORE numbering (no gaps in sequence)
        cleaned = SentencePostProcessor.FilterJunkStrings(cleaned);

        // Phase 3: Number sequentially - [1], [2], [3]... with no gaps
        for (int i = 0; i < cleaned.Count; i++)
        {
            cleaned[i] = $"\n[{i + 1}] {cleaned[i]}";
        }
        return cleaned;
    }

    public static string CleanMimeArtifacts(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // 1. Decode proper =XX hex sequences FIRST (e.g., =20 -> space, =3D -> '=')
        text = Regex.Replace(text, @"=([0-9A-Fa-f]{2})", m =>
        {
            int charCode = Convert.ToInt32(m.Groups[1].Value, 16);
            char decoded = (char)charCode;
            if (charCode == 0x0D || charCode == 0x0A) return " "; // CR/LF -> space
            if (charCode >= 0x20 && charCode <= 0x7E) return decoded.ToString();
            return ""; // Strip non-printable
        });

        // 2. Remove soft line breaks: = at end of line (MIME continuation)
        text = Regex.Replace(text, @"=\r?\n", "");

        // 3. Remove remaining '=' between letters (the "replacing a character" artifact)
        text = Regex.Replace(text, @"(?<=[a-zA-Z])=(?=[a-zA-Z])", "");

        // 4. Remove '=' at start of a word (before letters, e.g., =ddressee)
        text = Regex.Replace(text, @"(?<=\s|^)=(?=[a-zA-Z])", "");

        // 5. Remove '=' before punctuation within words (e.g., co=] -> co])
        text = Regex.Replace(text, @"(?<=[a-zA-Z])=(?=[)\]}>.,;:!?/])", "");

        return text;
    }
}
