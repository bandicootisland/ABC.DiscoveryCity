using System.IO.Packaging;
using ABC.DiscoveryCity.PostgreSQL;
using ABC.DiscoveryCity.Words.Common.Processing;
using Npgsql;

namespace ABC.DiscoveryCity.DocumentIngestionProcessing.Pipeline.Steps;

/// <summary>
/// Dictionary-based word splitting pass on extracted text.
/// Fixes run-together words from DevExpress PDF extraction
/// (e.g. "andMichael" → "and Michael", "OverviewofInvestigation" → "Overview of Investigation").
/// Loads headword strings from the OED binary in PostgreSQL.
/// Ported from BookCity's WordSplitStep.
/// </summary>
public class WordSplitStep : IIngestionStep
{
    private static WordSplitter? _splitter;
    private static readonly object _lock = new();

    private const string DictionaryName = "oed_cd_v4";
    private const int MAGIC = 0x4F454443; // "OEDC"

    public string Name => "WordSplit";

    public Task ExecuteAsync(IngestionContext ctx)
    {
        if (ctx.Category != FileCategory.Pdf) return Task.CompletedTask;

        EnsureSplitterInitialized(ctx.DbService);

        if (string.IsNullOrEmpty(ctx.FullText))
        {
            Console.WriteLine("  [WordSplit] No text to split");
            return Task.CompletedTask;
        }

        if (_splitter == null)
        {
            Console.WriteLine("  [WordSplit] Dictionary not available — skipping");
            return Task.CompletedTask;
        }

        int originalLen = ctx.FullText.Length;
        string corrected = _splitter.Process(ctx.FullText);

        int charsAdded = corrected.Length - originalLen;
        if (charsAdded > 0)
        {
            ctx.FullText = corrected;
            Console.WriteLine($"  [WordSplit] Fixed run-together words: +{charsAdded} chars ({charsAdded} spaces inserted)");
        }
        else
        {
            Console.WriteLine("  [WordSplit] No run-together words detected");
        }

        return Task.CompletedTask;
    }

    private static void EnsureSplitterInitialized(DbService dbService)
    {
        if (_splitter != null) return;

        lock (_lock)
        {
            if (_splitter != null) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Load XLSX blob from PostgreSQL via DbService's connection
            byte[]? xlsxData = null;
            try
            {
                using var conn = dbService.CreateConnection();
                conn.Open();
                using var cmd = new NpgsqlCommand(
                    "SELECT package_data FROM dictionary_packages WHERE dictionary_name = @name;", conn);
                cmd.Parameters.AddWithValue("name", DictionaryName);
                var result = cmd.ExecuteScalar();
                if (result is byte[] data)
                    xlsxData = data;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [WordSplit] Could not load dictionary: {ex.Message}");
                return;
            }

            if (xlsxData == null)
            {
                Console.WriteLine($"  [WordSplit] Dictionary '{DictionaryName}' not found in database. Use --import-dictionary to import from BookCity.");
                return;
            }

            Console.WriteLine($"  [WordSplit] Loaded dictionary blob ({xlsxData.Length / 1024.0 / 1024.0:F2} MB, {sw.ElapsedMilliseconds:N0} ms)");

            // Extract headwords from OED binary
            var headwordStrings = ReadHeadwordsOnly(xlsxData);
            xlsxData = null; // release the blob

            if (headwordStrings == null || headwordStrings.Length == 0)
            {
                Console.WriteLine("  [WordSplit] No headwords extracted from dictionary package");
                return;
            }

            Console.WriteLine($"  [WordSplit] OED headwords extracted: {headwordStrings.Length:N0} ({sw.ElapsedMilliseconds:N0} ms)");

            _splitter = new WordSplitter(headwordStrings);

            // Share dictionary with TextEnhancer's StrayEqualsPass for intelligent letter recovery
            TextEnhancer.SetDictionary(_splitter);

            Console.WriteLine($"  [WordSplit] Initialized with {WordSplitter.DictionaryCount:N0} dictionary entries ({sw.ElapsedMilliseconds:N0} ms total)");
        }
    }

    /// <summary>
    /// Reads headword strings AND variant form words from the OED binary payload.
    /// Identical to BookCity's WordSplitStep.ReadHeadwordsOnly.
    /// </summary>
    private static string[]? ReadHeadwordsOnly(byte[] xlsxData)
    {
        using var packageStream = new MemoryStream(xlsxData, writable: false);
        using var package = Package.Open(packageStream, FileMode.Open, FileAccess.Read);

        var binUri = PackUriHelper.CreatePartUri(new Uri("data/dictionary.bin", UriKind.Relative));
        if (!package.PartExists(binUri))
            return null;

        var binPart = package.GetPart(binUri);
        using var partStream = binPart.GetStream(FileMode.Open, FileAccess.Read);
        using var ms = new MemoryStream();
        partStream.CopyTo(ms);
        ms.Position = 0;

        using var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        int magic = reader.ReadInt32();
        if (magic != MAGIC)
            throw new InvalidDataException($"Invalid dictionary binary magic: 0x{magic:X8}");

        int version = reader.ReadInt32();
        if (version != 1)
            throw new InvalidDataException($"Unsupported dictionary version: {version}");

        int entryCount = reader.ReadInt32();
        var allWords = new HashSet<string>(entryCount * 2, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < entryCount; i++)
        {
            // Read headword
            string headword = reader.ReadString();
            if (!string.IsNullOrEmpty(headword))
                allWords.Add(headword);

            // Skip pos, homonym, etymology
            reader.ReadString(); // pos
            reader.ReadString(); // homonym
            reader.ReadString(); // etymology

            // Read variantForms — extract individual words
            string variantForms = reader.ReadString();
            if (!string.IsNullOrEmpty(variantForms))
                ExtractWordsFromText(variantForms, allWords);

            // Skip usageLabel, revisionNote
            reader.ReadString(); // usageLabel
            reader.ReadString(); // revisionNote

            // Skip pronunciations
            int pronCount = reader.ReadInt32();
            for (int p = 0; p < pronCount; p++)
                reader.ReadString();

            // Skip cross references
            int xrefCount = reader.ReadInt32();
            for (int x = 0; x < xrefCount; x++)
                reader.ReadString();

            // Read senses — extract words from definition text
            int senseCount = reader.ReadInt32();
            for (int s = 0; s < senseCount; s++)
                ReadSenseWords(reader, allWords);
        }

        return allWords.ToArray();
    }

    private static void ExtractWordsFromText(string text, HashSet<string> words)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            while (i < len && !char.IsLetter(text[i]))
                i++;

            if (i >= len) break;

            int start = i;
            while (i < len && (char.IsLetter(text[i]) || (text[i] == '-' && i + 1 < len && char.IsLetter(text[i + 1]))))
                i++;

            int wordLen = i - start;

            if (wordLen >= 3 && !(i < len && text[i] == '.'))
            {
                string word = text[start..i];
                if (!word.All(char.IsUpper))
                    words.Add(word);
            }
        }
    }

    private static void ReadSenseWords(BinaryReader reader, HashSet<string> words)
    {
        reader.ReadString(); // id
        reader.ReadString(); // primaryNumber

        string definitionText = reader.ReadString();
        if (!string.IsNullOrEmpty(definitionText))
            ExtractWordsFromText(definitionText, words);

        int labelCount = reader.ReadInt32();
        for (int l = 0; l < labelCount; l++)
            reader.ReadString();

        int xrefCount = reader.ReadInt32();
        for (int x = 0; x < xrefCount; x++)
            reader.ReadString();

        int quotCount = reader.ReadInt32();
        for (int q = 0; q < quotCount; q++)
        {
            reader.ReadString();
            reader.ReadString();
            reader.ReadString();
        }
    }
}
