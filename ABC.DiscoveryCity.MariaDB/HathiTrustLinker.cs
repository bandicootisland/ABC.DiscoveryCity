using MySqlConnector;
using System.IO.Compression;
using System.Text;

namespace ABC.DiscoveryCity.MariaDB;

/// <summary>
/// Links HathiTrust ZIP files to their catalog entries by extracting HTIDs from file contents.
/// Updates bookcityfile and bookcityfilesource tables with the linking information.
/// </summary>
public class HathiTrustLinker
{
    private readonly string _connectionString;
    private readonly int _batchSize;

    public HathiTrustLinker(string connectionString, int batchSize = 100)
    {
        _connectionString = connectionString;
        _batchSize = batchSize;
    }

    /// <summary>
    /// Extracts the HTID from a HathiTrust ZIP file by reading the folder name inside.
    /// Converts from encoded format (ark+=13960=xxx) to standard format (ark:/13960/xxx).
    /// </summary>
    public static string? ExtractHtidFromZip(string zipFilePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFilePath);
            if (zip.Entries.Count == 0) return null;

            // Get the first entry's full path to extract the folder name
            var firstEntry = zip.Entries[0].FullName;
            var folderName = firstEntry.Split('/')[0];

            // Convert encoded HTID to standard format
            // ark+=13960=t76t3z38s -> ark:/13960/t76t3z38s
            // Also handles formats like: mdp.39015... or uc1.b... (no ark prefix)
            return DecodeHtid(folderName);
        }
        catch
        {
            return null;
        }
    }

    // Mapping of numeric prefixes to HathiTrust institution codes
    // Based on analysis of hathitrust_records table
    private static readonly Dictionary<string, string> NumericPrefixToInstitution = new()
    {
        { "39015", "mdp" },   // Michigan Digital Project (University of Michigan)
        { "39076", "mdp" },   // Michigan (alternate)
        { "49015", "mdp" },   // Michigan (alternate)
        { "31951", "umn" },   // University of Minnesota
        { "30112", "uiug" },  // University of Illinois
        { "31924", "coo" },   // Cornell University
        { "32435", "osu" },   // Ohio State University
        { "31822", "uc1" },   // University of California
        { "32106", "uc1" },   // University of California (alternate)
        { "31210", "uc1" },   // University of California (alternate)
        { "31175", "uc1" },   // University of California (alternate)
        { "31158", "uc1" },   // University of California (alternate)
        { "32044", "hvd" },   // Harvard University
        { "30000", "inu" },   // Indiana University
        { "39000", "inu" },   // Indiana (alternate)
        { "33433", "nyp" },   // New York Public Library
        { "05917", "txu" },   // University of Texas
        { "31858", "iau" },   // University of Iowa
        { "32101", "njp" },   // New Jersey / Princeton
        { "35556", "ien" },   // Northwestern University
        { "31262", "ufl" },   // University of Florida
        { "32754", "pur1" },  // Purdue University
        { "31293", "msu" },   // Michigan State University
        { "32108", "uga1" },  // University of Georgia
    };

    /// <summary>
    /// Decodes an encoded HTID folder name to standard HTID format.
    /// Handles both ark-style (ark+=13960=xxx) and numeric-style (39015xxxxx) encodings.
    /// </summary>
    public static string DecodeHtid(string encoded)
    {
        // Handle ark encoded format: ark+=13960=xxx -> not directly usable, return as-is for now
        // These don't work in the HathiTrust viewer, we need the institutional ID
        if (encoded.StartsWith("ark+=") || encoded.StartsWith("ark:/"))
        {
            // ARK identifiers don't work in HathiTrust viewer - return null to indicate we can't decode
            return encoded; // Keep for reference but won't generate valid URLs
        }

        // Handle numeric institutional IDs like "39015t00227848q" -> "mdp.3901500227848q"
        // Pattern: [5-digit prefix][single letter][rest of ID]
        // The single letter (like 't') is removed and replaced with '0'
        
        // Try to match a known numeric prefix
        foreach (var (numPrefix, instCode) in NumericPrefixToInstitution)
        {
            if (encoded.StartsWith(numPrefix))
            {
                var remainder = encoded.Substring(numPrefix.Length);
                
                // If remainder starts with a letter, remove it (it's a separator)
                // e.g., "39015t00227848q" -> prefix="39015", remainder="t00227848q" -> "mdp.3901500227848q"
                if (remainder.Length > 0 && char.IsLetter(remainder[0]))
                {
                    remainder = "0" + remainder.Substring(1); // Replace letter with '0'
                }
                
                return $"{instCode}.{numPrefix}{remainder}";
            }
        }

        // If no known prefix, try generic + replacement
        return encoded.Replace("+", ".");
    }

    /// <summary>
    /// Checks if the decoded HTID is valid for HathiTrust viewer URLs.
    /// ARK-style identifiers don't work in the viewer.
    /// </summary>
    public static bool IsValidViewerHtid(string htid)
    {
        // ARK identifiers don't work in the HathiTrust viewer
        if (htid.StartsWith("ark:") || htid.StartsWith("ark+="))
            return false;
        
        // Valid HTIDs should have format like "mdp.39015xxxxx"
        return htid.Contains('.') && !htid.Contains('/');
    }

    /// <summary>
    /// Gets the page count from a HathiTrust ZIP file.
    /// </summary>
    public static int GetPageCount(string zipFilePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFilePath);
            return zip.Entries.Count(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Generates the public HathiTrust URL for viewing a book (opens at page 1).
    /// </summary>
    public static string? GetPublicUrl(string htid)
    {
        if (!IsValidViewerHtid(htid)) return null;
        return $"https://babel.hathitrust.org/cgi/pt?id={Uri.EscapeDataString(htid)}&seq=1";
    }

    /// <summary>
    /// Generates the public HathiTrust URL for viewing a specific page.
    /// Returns null if the HTID is not valid for the viewer (e.g., ARK identifiers).
    /// </summary>
    public static string? GetPublicPageUrl(string htid, int pageNumber)
    {
        if (!IsValidViewerHtid(htid)) return null;
        return $"https://babel.hathitrust.org/cgi/pt?id={Uri.EscapeDataString(htid)}&seq={pageNumber}";
    }

    /// <summary>
    /// Generates the URL for fetching a specific page image from HathiTrust.
    /// Returns null if the HTID is not valid for the viewer.
    /// </summary>
    public static string? GetPageImageUrl(string htid, int pageNumber, string size = "full")
    {
        if (!IsValidViewerHtid(htid)) return null;
        return $"https://babel.hathitrust.org/cgi/imgsrv/image?id={Uri.EscapeDataString(htid)};seq={pageNumber};size={size}";
    }

    /// <summary>
    /// Links HathiTrust files to their catalog entries.
    /// Extracts HTID from each ZIP and updates the LibraryId field.
    /// </summary>
    /// <param name="relinkAll">If true, re-processes all files. If false, only processes files without LibraryId.</param>
    /// <param name="progress">Optional progress callback (current, total, linked, failed, skipped)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<(int linked, int failed, int skipped)> LinkFilesAsync(
        bool relinkAll = false,
        Action<int, int, int, int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int linked = 0, failed = 0, skipped = 0;

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Get HathiTrust collection ID
        var collectionId = await GetHathiTrustCollectionIdAsync(connection, cancellationToken);
        if (collectionId == null)
        {
            Console.WriteLine("[HathiTrustLinker] ERROR: HathiTrust collection not found!");
            return (0, 0, 0);
        }

        Console.WriteLine($"[HathiTrustLinker] Found HathiTrust collection: {collectionId}");

        // Get files to process
        var files = relinkAll 
            ? await GetAllFilesAsync(connection, collectionId.Value, cancellationToken)
            : await GetUnlinkedFilesAsync(connection, collectionId.Value, cancellationToken);
        Console.WriteLine($"[HathiTrustLinker] Found {files.Count:N0} files to process (relinkAll={relinkAll})");

        var updates = new List<(Guid fileId, string htid, int pageCount)>();

        foreach (var (fileId, filePath) in files)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (!File.Exists(filePath))
            {
                skipped++;
                continue;
            }

            var htid = ExtractHtidFromZip(filePath);
            if (string.IsNullOrEmpty(htid))
            {
                failed++;
                continue;
            }

            // Only store if it's a valid HathiTrust viewer HTID (not ark format)
            if (!IsValidViewerHtid(htid))
            {
                skipped++; // Skip ark-format IDs that won't work in viewer
                continue;
            }

            var pageCount = GetPageCount(filePath);
            updates.Add((fileId, htid, pageCount));

            if (updates.Count >= _batchSize)
            {
                var batchLinked = await UpdateBatchAsync(connection, updates, cancellationToken);
                linked += batchLinked;
                var processed = linked + failed + skipped;
                progress?.Invoke(processed, files.Count, linked, failed, skipped);
                Console.WriteLine($"[HathiTrustLinker] Progress: {processed:N0}/{files.Count:N0} - Linked {linked:N0}, Failed {failed:N0}, Skipped {skipped:N0}");
                updates.Clear();
            }
        }

        // Final batch
        if (updates.Count > 0)
        {
            var batchLinked = await UpdateBatchAsync(connection, updates, cancellationToken);
            linked += batchLinked;
        }

        var totalProcessed = linked + failed + skipped;
        progress?.Invoke(totalProcessed, files.Count, linked, failed, skipped);
        Console.WriteLine($"[HathiTrustLinker] Complete! Linked: {linked:N0}, Failed: {failed:N0}, Skipped: {skipped:N0}");
        return (linked, failed, skipped);
    }

    private async Task<Guid?> GetHathiTrustCollectionIdAsync(MySqlConnection connection, CancellationToken ct)
    {
        const string sql = "SELECT CollectionId FROM bookcitycollection WHERE CollectionCode = 'hathitrust'";
        await using var cmd = new MySqlCommand(sql, connection);
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result == null || result == DBNull.Value) return null;
        return result is Guid g ? g : Guid.Parse(result.ToString()!);
    }

    private async Task<List<(Guid fileId, string filePath)>> GetUnlinkedFilesAsync(
        MySqlConnection connection, Guid collectionId, CancellationToken ct)
    {
        // Get files where we haven't extracted the Library HTID yet
        // We detect this by checking if LibraryId is NULL
        const string sql = @"
            SELECT FileId, FilePath 
            FROM bookcityfile 
            WHERE CollectionId = @collectionId 
              AND LibraryId IS NULL
            ORDER BY FileId";

        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@collectionId", collectionId.ToString());
        cmd.CommandTimeout = 300;

        var files = new List<(Guid, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fileIdVal = reader.GetValue(0);
            var fileId = fileIdVal is Guid g ? g : Guid.Parse(fileIdVal.ToString()!);
            var filePath = reader.GetString(1);
            files.Add((fileId, filePath));
        }
        return files;
    }

    private async Task<List<(Guid fileId, string filePath)>> GetAllFilesAsync(
        MySqlConnection connection, Guid collectionId, CancellationToken ct)
    {
        // Get ALL files in the collection for re-linking
        const string sql = @"
            SELECT FileId, FilePath 
            FROM bookcityfile 
            WHERE CollectionId = @collectionId
            ORDER BY FileId";

        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@collectionId", collectionId.ToString());
        cmd.CommandTimeout = 300;

        var files = new List<(Guid, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var fileIdVal = reader.GetValue(0);
            var fileId = fileIdVal is Guid g ? g : Guid.Parse(fileIdVal.ToString()!);
            var filePath = reader.GetString(1);
            files.Add((fileId, filePath));
        }
        return files;
    }

    private async Task<int> UpdateBatchAsync(
        MySqlConnection connection,
        List<(Guid fileId, string htid, int pageCount)> updates,
        CancellationToken ct)
    {
        if (updates.Count == 0) return 0;

        // Build a single UPDATE with CASE statements for efficiency
        // Updates LibraryId (external viewer ID) not SourceId (Anna's Archive ID)
        var sql = new StringBuilder();
        sql.AppendLine("UPDATE bookcityfile SET");
        sql.AppendLine("  LibraryId = CASE FileId");
        foreach (var (fileId, htid, _) in updates)
        {
            sql.AppendLine($"    WHEN '{fileId}' THEN '{MySqlHelper.EscapeString(htid)}'");
        }
        sql.AppendLine("  END,");
        sql.AppendLine("  LibraryIdType = 'htid',");
        sql.AppendLine("  PageCount = CASE FileId");
        foreach (var (fileId, _, pageCount) in updates)
        {
            sql.AppendLine($"    WHEN '{fileId}' THEN {pageCount}");
        }
        sql.AppendLine("  END");
        sql.AppendLine("WHERE FileId IN (");
        sql.AppendLine(string.Join(",", updates.Select(u => $"'{u.fileId}'")));
        sql.AppendLine(")");

        await using var cmd = new MySqlCommand(sql.ToString(), connection);
        cmd.CommandTimeout = 300;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Gets book information from both the file catalog and HathiTrust metadata.
    /// </summary>
    public async Task<HathiTrustBookInfo?> GetBookInfoAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT 
                f.FileId, f.FilePath, f.FileName, f.FileSize, f.SourceId, f.SourceIdType,
                f.LibraryId, f.LibraryIdType,
                h.htid, h.title, h.author, h.imprint, h.isbn, h.oclc_num, h.lang, h.access,
                h.rights, h.pub_place, h.bib_fmt
            FROM bookcityfile f
            LEFT JOIN hathitrust_records h ON f.LibraryId = h.htid
            WHERE f.FileId = @fileId";

        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fileId", fileId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var filePath = reader.GetString(1);
        var libraryId = reader.IsDBNull(6) ? null : reader.GetString(6);
        var htid = reader.IsDBNull(8) ? libraryId : reader.GetString(8);

        return new HathiTrustBookInfo
        {
            FileId = fileId,
            FilePath = filePath,
            FileName = reader.GetString(2),
            FileSize = reader.GetInt64(3),
            Htid = htid,
            Title = reader.IsDBNull(9) ? null : reader.GetString(9),
            Author = reader.IsDBNull(10) ? null : reader.GetString(10),
            Imprint = reader.IsDBNull(11) ? null : reader.GetString(11),
            Isbn = reader.IsDBNull(12) ? null : reader.GetString(12),
            OclcNum = reader.IsDBNull(13) ? null : reader.GetString(13),
            Language = reader.IsDBNull(14) ? null : reader.GetString(14),
            Access = reader.IsDBNull(15) ? null : reader.GetString(15),
            Rights = reader.IsDBNull(16) ? null : reader.GetString(16),
            PublicUrl = htid != null ? GetPublicUrl(htid) : null,
            PageCount = File.Exists(filePath) ? GetPageCount(filePath) : 0
        };
    }

    /// <summary>
    /// Reads a specific page's OCR text from a HathiTrust ZIP file.
    /// </summary>
    public static string? ReadPage(string zipFilePath, int pageNumber)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFilePath);
            
            // Find the entry for this page (e.g., 00000001.txt for page 1)
            var pageName = $"{pageNumber:D8}.txt";
            var entry = zip.Entries.FirstOrDefault(e => e.Name == pageName);
            
            if (entry == null)
            {
                // Try finding by index if numbered naming doesn't work
                var txtEntries = zip.Entries
                    .Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Name)
                    .ToList();
                
                if (pageNumber > 0 && pageNumber <= txtEntries.Count)
                {
                    entry = txtEntries[pageNumber - 1];
                }
            }

            if (entry == null) return null;

            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the list of all pages in a HathiTrust ZIP file.
    /// </summary>
    public static List<string> GetPageList(string zipFilePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFilePath);
            return zip.Entries
                .Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Name)
                .OrderBy(n => n)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}

/// <summary>
/// Information about a HathiTrust book combining file and metadata.
/// </summary>
public class HathiTrustBookInfo
{
    public Guid FileId { get; set; }
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string? Htid { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Imprint { get; set; }
    public string? Isbn { get; set; }
    public string? OclcNum { get; set; }
    public string? Language { get; set; }
    public string? Access { get; set; }
    public string? Rights { get; set; }
    public string? PublicUrl { get; set; }
    public int PageCount { get; set; }
}
