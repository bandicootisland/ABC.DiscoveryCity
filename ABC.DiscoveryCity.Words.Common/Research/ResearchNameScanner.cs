using System.Text.RegularExpressions;

namespace ABC.DiscoveryCity.Words.Common.Research;

public sealed record NameScanSentence(
    int Ordinal,
    string Text,
    Guid? SentenceId = null,
    int? PageNumber = null);

public sealed record ResearchNameMention(
    string RawMention,
    string NormalizedMention,
    string SourcePattern,
    decimal Confidence,
    int? SentenceOrdinal,
    Guid? SentenceId,
    int? PageNumber,
    int? CharacterStart,
    int? CharacterEnd,
    string? SentenceText);

public sealed record ResearchNameCandidate(
    string DisplayName,
    string NormalizedName,
    decimal Confidence,
    int MentionCount,
    List<string> Sources);

public sealed record ResearchNameScanResult(
    string ExtractorVersion,
    int SentenceCount,
    List<ResearchNameCandidate> Candidates,
    List<ResearchNameMention> Mentions,
    List<string> Terms);

public static class ResearchNameScanner
{
    public const string Version = "research-name-scanner-v1";

    private static readonly Regex HeaderNameRegex = new(
        @"(?:From|To|Cc|Bcc|Sent\s*(?:by)?)\s*:\s*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})",
        RegexOptions.Compiled);

    private static readonly Regex HeaderAddressNameRegex = new(
        @"(?:From|To|Cc|Bcc)\s*:\s*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})\s*<",
        RegexOptions.Compiled);

    private static readonly Regex HeaderListNameRegex = new(
        @"(?:To|Cc|Bcc)\s*:\s*(?:(?:[A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})\s*;\s*)*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})",
        RegexOptions.Compiled);

    private static readonly Regex GreetingNameRegex = new(
        @"\b(?:Dear|Hi|Hello|Attn)\s+([A-Z=][a-z=]+(?:\s+[A-Z=][a-z=]{1,20})?)",
        RegexOptions.Compiled);

    private static readonly Regex KnownContextNameRegex = new(
        @"\b(?:w/|with|meeting\s+with|Appt\s+w/|LUNCH\s+w/)\s+([A-Z=][a-z=]+(?:\s+[A-Z=][a-z=]{1,20}))",
        RegexOptions.Compiled);

    private static readonly Regex CapitalizedNameRegex = new(
        @"\b([A-Z=][a-z=]{2,15}\s+[A-Z=][a-z=]{2,20})\b",
        RegexOptions.Compiled);

    public static ResearchNameScanResult ScanText(string text, int maxCharacters = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty();

        var scoped = maxCharacters > 0 && text.Length > maxCharacters
            ? text[..maxCharacters]
            : text;

        var parts = Regex.Split(scoped, @"(?<=[.!?])\s+|[\r\n]+")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select((s, i) => new NameScanSentence(i + 1, s.Trim()))
            .ToList();

        if (parts.Count == 0)
            parts.Add(new NameScanSentence(1, scoped));

        return ScanSentences(parts);
    }

    public static ResearchNameScanResult ScanSentences(IReadOnlyList<NameScanSentence> sentences, int maxCharacters = 0)
    {
        if (sentences.Count == 0)
            return Empty();

        var mentions = new List<ResearchNameMention>();
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumedCharacters = 0;

        foreach (var sentence in sentences)
        {
            if (string.IsNullOrWhiteSpace(sentence.Text))
                continue;

            if (maxCharacters > 0 && consumedCharacters >= maxCharacters)
                break;

            var text = sentence.Text;
            if (maxCharacters > 0 && consumedCharacters + text.Length > maxCharacters)
                text = text[..Math.Max(0, maxCharacters - consumedCharacters)];

            AddMentions(mentions, terms, sentence with { Text = text });
            consumedCharacters += text.Length;
        }

        var candidates = mentions
            .GroupBy(m => m.NormalizedMention, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var best = g.OrderByDescending(m => m.Confidence).ThenBy(m => m.RawMention.Length).First();
                return new ResearchNameCandidate(
                    best.RawMention,
                    best.NormalizedMention,
                    g.Max(m => m.Confidence),
                    g.Count(),
                    g.Select(m => m.SourcePattern).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList());
            })
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.MentionCount)
            .ThenBy(c => c.DisplayName)
            .ToList();

        return new ResearchNameScanResult(
            Version,
            sentences.Count,
            candidates,
            mentions
                .OrderBy(m => m.SentenceOrdinal ?? int.MaxValue)
                .ThenBy(m => m.CharacterStart ?? int.MaxValue)
                .ToList(),
            terms.OrderBy(t => t).ToList());
    }

    public static (List<string> Names, List<string> Terms) ExtractNamesAndTerms(string text)
    {
        var scan = ScanText(text, maxCharacters: 8000);
        var names = scan.Mentions
            .Where(m => !m.SourcePattern.Equals("capitalized", StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.NormalizedMention, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(m => m.Confidence).First().RawMention)
            .OrderBy(n => n)
            .ToList();

        return (names, scan.Terms);
    }

    public static string CleanExtractedName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        name = name.Replace("\n", " ").Replace("\r", " ");
        name = Regex.Replace(name, @"[\d<>\[\]@.,;:!?\-_/\\()]+$", "").Trim();
        name = Regex.Replace(name, @"^[\d<>\[\]@.,;:!?\-_/\\()]+", "").Trim();
        name = name.Replace("=", "");
        name = Regex.Replace(name, @"\s{2,}", " ").Trim();
        name = Regex.Replace(name, @"\s+(Sent|From|To|Cc|Subject|Date|Re|Fwd|Mon|Tue|Wed|Thu|Fri|Sat|Sun)$", "", RegexOptions.IgnoreCase).Trim();
        return name;
    }

    public static string NormalizeName(string name)
    {
        var cleaned = CleanExtractedName(name).ToLowerInvariant();
        cleaned = Regex.Replace(cleaned, @"[^a-z0-9\s']", " ");
        return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
    }

    public static bool IsValidPersonName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 4) return false;
        if (!name.Contains(' ')) return false;
        if (Regex.IsMatch(name, @"[\d@#$%^&*(){}|<>\n\r]")) return false;

        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (parts[0].Length < 2 || parts[^1].Length < 2) return false;

        var rejectFirstWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sent", "Hello", "Dear", "Hey", "The", "This", "That", "Your", "Our", "My",
            "From", "Date", "Subject", "Reply", "Forward", "Original", "Attachment",
            "Please", "Thanks", "Thank", "Best", "Kind", "Warm", "Good", "Look",
            "Flight", "Stem", "Med", "Image", "File", "Case", "Help", "Earth",
            "Click", "View", "Open", "Read", "Copy", "Save", "Print", "Delete",
            "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
            "San", "New", "South", "North", "East", "West", "Los", "Santa", "Palm"
        };
        if (rejectFirstWords.Contains(parts[0])) return false;

        return true;
    }

    public static bool IsCommonPhrase(string candidate)
    {
        var nonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sent from", "Sent From", "Original Message", "Court Order", "Court Document",
            "New York", "Los Angeles", "San Francisco", "Santa Monica", "Palm Beach",
            "United States", "South Florida", "Southern District", "Northern District",
            "Dear Sir", "Dear Madam", "Good Morning", "Good Afternoon", "Good Evening",
            "Best Regards", "Kind Regards", "Warm Regards", "Many Thanks",
            "Please Note", "For Immediate", "Private Communication", "All Rights",
            "Rights Reserved", "Jeffrey Epstein",
            "East Street", "West Street", "North Street", "South Street",
            "Monday Morning", "Tuesday Morning", "Wednesday Morning", "Thursday Morning",
            "Friday Morning", "Saturday Morning", "Sunday Morning",
            "January February", "February March", "Unauthorized Use",
            "Your Email", "This Email", "This Message", "Earth Link",
            "Flash Player", "Internet Explorer", "Microsoft Office", "Google Chrome",
            "Apple Inc", "Subject Line", "Read Receipt", "Return Receipt",
            "Thank You", "Look Forward", "Property List", "Attachment Name",
            "Cell Number", "Phone Number", "Office Number",
            "Image Format", "File Size", "File Name", "Date Received",
            "Help Save", "Feminine Care", "Gillette Blade",
            "Building Entrance", "Front Door",
            "Attorney Client", "Inside Information", "Strictly Prohibited"
        };

        return nonNames.Contains(candidate);
    }

    private static ResearchNameScanResult Empty()
        => new(Version, 0, new List<ResearchNameCandidate>(), new List<ResearchNameMention>(), new List<string>());

    private static void AddMentions(List<ResearchNameMention> mentions, HashSet<string> terms, NameScanSentence sentence)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddMatches(mentions, used, sentence, HeaderNameRegex, "email-header", 0.94m);
        AddMatches(mentions, used, sentence, HeaderAddressNameRegex, "email-header", 0.94m);
        AddMatches(mentions, used, sentence, HeaderListNameRegex, "email-header-list", 0.90m);
        AddMatches(mentions, used, sentence, GreetingNameRegex, "greeting", 0.84m);
        AddMatches(mentions, used, sentence, KnownContextNameRegex, "known-context", 0.88m);

        foreach (Match match in CapitalizedNameRegex.Matches(sentence.Text))
        {
            var candidate = CleanExtractedName(match.Groups[1].Value);
            if (!IsValidPersonName(candidate) || IsCommonPhrase(candidate))
                continue;

            terms.Add(candidate);
            AddMention(mentions, used, sentence, match, candidate, "capitalized", 0.66m);
        }
    }

    private static void AddMatches(
        List<ResearchNameMention> mentions,
        HashSet<string> used,
        NameScanSentence sentence,
        Regex regex,
        string sourcePattern,
        decimal confidence)
    {
        foreach (Match match in regex.Matches(sentence.Text))
        {
            var candidate = CleanExtractedName(match.Groups[1].Value);
            if (IsValidPersonName(candidate))
                AddMention(mentions, used, sentence, match, candidate, sourcePattern, confidence);
        }
    }

    private static void AddMention(
        List<ResearchNameMention> mentions,
        HashSet<string> used,
        NameScanSentence sentence,
        Match match,
        string candidate,
        string sourcePattern,
        decimal confidence)
    {
        var normalized = NormalizeName(candidate);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var capture = match.Groups.Count > 1 ? match.Groups[1] : match.Groups[0];
        var key = $"{normalized}|{capture.Index}|{capture.Length}";
        if (!used.Add(key))
            return;

        mentions.Add(new ResearchNameMention(
            candidate,
            normalized,
            sourcePattern,
            confidence,
            sentence.Ordinal,
            sentence.SentenceId,
            sentence.PageNumber,
            capture.Index,
            capture.Index + capture.Length,
            TrimSentence(sentence.Text)));
    }

    private static string TrimSentence(string text)
        => text.Length <= 600 ? text : text[..600];
}
