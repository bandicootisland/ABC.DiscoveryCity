using System.Text.RegularExpressions;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline;

public static class MetadataExtractors
{
    public static DateTime? DeduceDateFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Limit scope to first 4000 chars for header dates
        string snippet = text.Length > 4000 ? text.Substring(0, 4000) : text;

        // 1. Standard Patterns (High Confidence)
        var standardPatterns = new[]
        {
            @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4}\b",
            @"\b\d{1,2}[/-]\d{1,2}[/-]\d{4}\b",
            @"\b\d{4}-\d{2}-\d{2}\b"
        };

        foreach (var pattern in standardPatterns)
        {
            var match = Regex.Match(snippet, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (DateTime.TryParse(match.Value, out DateTime date)) return date;
            }
        }

        // 2. Scrappy OCR Patterns (Medium Confidence)
        var scrappyPattern = @"\b(\d{1,2})[^\w\d]{1,5}(\d{1,2})[^\w\d]{1,5}(\d{2,4})\b";
        var matchScrappy = Regex.Match(snippet, scrappyPattern);
        if (matchScrappy.Success)
        {
            int p1 = int.Parse(matchScrappy.Groups[1].Value);
            int p2 = int.Parse(matchScrappy.Groups[2].Value);
            int p3 = int.Parse(matchScrappy.Groups[3].Value);

            int year = p3;
            if (year < 100) year += (year > 30 ? 1900 : 2000);

            int month = p1;
            int day = p2;

            if (month > 12 && day <= 12)
            {
                month = p2;
                day = p1;
            }

            if (month <= 12 && day <= 31)
            {
                try { return new DateTime(year, month, day); } catch { }
            }
        }

        // 3. Last Resort: Just Year (Low Confidence)
        var yearMatch = Regex.Match(snippet, @"\b(19|20)\d{2}\b");
        if (yearMatch.Success)
        {
            return new DateTime(int.Parse(yearMatch.Value), 1, 1);
        }

        return null;
    }

    public static string DeduceTitleFromText(string text, string filename)
    {
        if (string.IsNullOrWhiteSpace(text)) return filename;
        string snippet = text.Length > 2000 ? text.Substring(0, 2000) : text;

        var keywords = new Dictionary<string, string>
        {
            { "IMAGE", "Image" },
            { "MEMORANDUM", "Memorandum" },
            { "REPORT", "Report" },
            { "EMAIL", "Email" },
            { "LETTER", "Letter" },
            { "COURT", "Court Document" },
            { "ORDER", "Court Order" },
            { "MOTION", "Motion" },
            { "SUBPOENA", "Subpoena" },
            { "CASE ID", "Case File" },
            { "FBI", "FBI Document" },
            { "TRANSCRIPT", "Transcript" },
            { "AFFIDAVIT", "Affidavit" },
            { "WARRANT", "Warrant" }
        };

        foreach (var kvp in keywords)
        {
            if (snippet.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                return $"{kvp.Value} - {filename}";
        }

        return filename;
    }

    public static (List<string> Names, List<string> Terms) ExtractNamesAndTerms(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return (new List<string>(), new List<string>());

        string snippet = text.Length > 8000 ? text.Substring(0, 8000) : text;

        // 1. Email header patterns (From:, To:, Cc:, Sent by:) -> Names
        var headerPatterns = new[]
        {
            @"(?:From|To|Cc|Bcc|Sent\s*(?:by)?)\s*:\s*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})",
            @"(?:From|To|Cc|Bcc)\s*:\s*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})\s*<",
            @"(?:To|Cc|Bcc)\s*:\s*(?:(?:[A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})\s*;\s*)*([A-Z=][a-z=]+(?:\s+[A-Z=]\.?)?\s+[A-Z=][a-z=]{1,20})",
        };

        foreach (var pattern in headerPatterns)
        {
            foreach (Match m in Regex.Matches(snippet, pattern))
            {
                var name = CleanExtractedName(m.Groups[1].Value);
                if (IsValidPersonName(name)) names.Add(name);
            }
        }

        // 2. "Dear X" / "Hi X" / "Hello X" patterns -> Names
        foreach (Match m in Regex.Matches(snippet, @"\b(?:Dear|Hi|Hello|Attn)\s+([A-Z=][a-z=]+(?:\s+[A-Z=][a-z=]{1,20})?)", RegexOptions.None))
        {
            var name = CleanExtractedName(m.Groups[1].Value);
            if (IsValidPersonName(name)) names.Add(name);
        }

        // 3. Known-name-context patterns -> Names
        foreach (Match m in Regex.Matches(snippet, @"\b(?:w/|with|meeting\s+with|Appt\s+w/|LUNCH\s+w/)\s+([A-Z=][a-z=]+(?:\s+[A-Z=][a-z=]{1,20}))", RegexOptions.None))
        {
            var name = CleanExtractedName(m.Groups[1].Value);
            if (IsValidPersonName(name)) names.Add(name);
        }

        // 4. Capitalized "Firstname Lastname" sequences -> Terms
        foreach (Match m in Regex.Matches(snippet, @"\b([A-Z=][a-z=]{2,15}\s+[A-Z=][a-z=]{2,20})\b"))
        {
            var candidate = CleanExtractedName(m.Groups[1].Value);
            if (IsValidPersonName(candidate) && !IsCommonPhrase(candidate))
            {
                if (!names.Contains(candidate))
                    terms.Add(candidate);
            }
        }

        return (names.OrderBy(p => p).ToList(), terms.OrderBy(t => t).ToList());
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
            "Attorney Client", "Inside Information", "Strictly Prohibited",
        };

        return nonNames.Contains(candidate);
    }
}
