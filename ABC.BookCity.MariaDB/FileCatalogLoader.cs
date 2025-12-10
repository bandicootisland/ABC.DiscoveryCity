using MySqlConnector;
using System.Text;
using System.Text.RegularExpressions;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// Scans folders of book files and registers them in the bookcityfile table.
/// Handles multiple identifier types: MD5, AACID, HTID, etc.
/// Uses bookcitycollection table for collection lookup and processing hints.
/// </summary>
public class FileCatalogLoader
{
    private readonly string _connectionString;
    private readonly int _batchSize;
    
    // Cache: CollectionCode -> (CollectionId, FileType)
    private Dictionary<string, (Guid Id, string FileType)> _collectionCache = new(StringComparer.OrdinalIgnoreCase);

    // File extension to MIME type mapping
    private static readonly Dictionary<string, (string MimeType, string ContentType)> FileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // eBooks
        { ".pdf", ("application/pdf", "book") },
        { ".epub", ("application/epub+zip", "book") },
        { ".mobi", ("application/x-mobipocket-ebook", "book") },
        { ".azw", ("application/vnd.amazon.ebook", "book") },
        { ".azw3", ("application/vnd.amazon.ebook", "book") },
        { ".djvu", ("image/vnd.djvu", "book") },
        { ".fb2", ("application/x-fictionbook+xml", "book") },
        { ".txt", ("text/plain", "book") },
        { ".rtf", ("application/rtf", "book") },
        { ".doc", ("application/msword", "book") },
        { ".docx", ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "book") },
        
        // Comics
        { ".cbr", ("application/x-cbr", "comic") },
        { ".cbz", ("application/x-cbz", "comic") },
        { ".cb7", ("application/x-cb7", "comic") },
        { ".cbt", ("application/x-cbt", "comic") },
        
        // Archives (may contain books/OCR)
        { ".zip", ("application/zip", "archive") },
        { ".rar", ("application/x-rar-compressed", "archive") },
        { ".7z", ("application/x-7z-compressed", "archive") },
        
        // Other
        { ".chm", ("application/vnd.ms-htmlhelp", "reference") },
    };

    public FileCatalogLoader(string connectionString, int batchSize = 500)
    {
        _connectionString = connectionString;
        _batchSize = batchSize;
    }

    /// <summary>
    /// Loads collection info from bookcitycollection table into cache.
    /// </summary>
    public async Task LoadCollectionCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        
        await using var cmd = new MySqlCommand(
            "SELECT CollectionId, CollectionCode, FileType FROM bookcitycollection", connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        
        while (await reader.ReadAsync(cancellationToken))
        {
            var idValue = reader.GetValue(0);
            var id = idValue is Guid g ? g : Guid.Parse(idValue.ToString()!);
            var code = reader.GetString(1);
            var fileType = reader.IsDBNull(2) ? "md5" : reader.GetString(2);
            _collectionCache[code] = (id, fileType);
        }
        
        Console.WriteLine($"[FileCatalog] Loaded {_collectionCache.Count} collections from database");
    }

    /// <summary>
    /// Scans a folder and registers all files in the catalog.
    /// Handles different identifier types based on collection's FileType.
    /// </summary>
    /// <param name="folderPath">Folder to scan</param>
    /// <param name="collection">Collection name (e.g., "libgen", "hathitrust")</param>
    /// <param name="recursive">Scan subfolders</param>
    public async Task<(long scanned, long inserted, long skipped)> ScanFolderAsync(
        string folderPath,
        string collection,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        long scanned = 0;
        long inserted = 0;
        long skipped = 0;

        if (!Directory.Exists(folderPath))
        {
            Console.WriteLine($"[FileCatalog] Folder not found: {folderPath}");
            return (0, 0, 0);
        }

        // Ensure collection cache is loaded
        if (_collectionCache.Count == 0)
        {
            await LoadCollectionCacheAsync(cancellationToken);
        }

        // Get collection info
        if (!_collectionCache.TryGetValue(collection, out var collectionInfo))
        {
            // Try to add collection if it doesn't exist
            Console.WriteLine($"[FileCatalog] Collection '{collection}' not found, creating...");
            var newId = await CreateCollectionAsync(collection, cancellationToken);
            collectionInfo = (newId, "md5"); // Default to md5 for new collections
            _collectionCache[collection] = collectionInfo;
        }

        var (collectionId, fileType) = collectionInfo;

        Console.WriteLine($"[FileCatalog] Scanning: {folderPath}");
        Console.WriteLine($"[FileCatalog] Collection: {collection} (FileType: {fileType})");

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(folderPath, "*", searchOption);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var insertBuilder = new StringBuilder();
        int batchCount = 0;
        long pendingInBatch = 0;

        // Updated INSERT to include SourceId and SourceIdType
        const string insertPrefix = @"INSERT IGNORE INTO bookcityfile 
            (FileId, CollectionId, Md5Hash, SourceId, SourceIdType, FilePath, FileName, FileExtension, FileSize, 
             ResourcePath, MimeType, ContentType) VALUES ";

        foreach (var filePath in files)
        {
            if (cancellationToken.IsCancellationRequested) break;

            scanned++;
            
            var fileInfo = new FileInfo(filePath);
            var fileName = fileInfo.Name;
            var extension = fileInfo.Extension.ToLowerInvariant();

            // Extract identifier based on collection's FileType
            var (sourceId, sourceIdType, md5) = ExtractIdentifier(fileName, fileType);
            
            if (sourceId == null)
            {
                skipped++;
                continue;
            }

            // Get file type info
            var (mimeType, contentType) = GetFileType(extension);
            
            // For AACID/OCR content, override content type
            if (fileType == "aacid" && string.IsNullOrEmpty(extension))
            {
                contentType = "ocr";
                mimeType = "application/zip";
            }

            // Build resource path
            var resourcePath = BuildResourcePath(collection, sourceId, fileName, extension);

            // Build INSERT value
            if (batchCount > 0) insertBuilder.Append(',');

            insertBuilder.Append('(');
            insertBuilder.Append("UUID(),");  // FileId
            insertBuilder.Append('\'').Append(collectionId.ToString()).Append("',");  // CollectionId
            
            // Md5Hash (nullable)
            if (md5 != null)
                insertBuilder.Append('\'').Append(md5.ToUpperInvariant()).Append("',");
            else
                insertBuilder.Append("NULL,");
            
            // SourceId and SourceIdType
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(sourceId)).Append("',");
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(sourceIdType)).Append("',");
            
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(filePath)).Append("',");
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(fileName)).Append("',");
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(extension.TrimStart('.'))).Append("',");
            insertBuilder.Append(fileInfo.Length).Append(',');
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(resourcePath)).Append("',");
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(mimeType)).Append("',");
            insertBuilder.Append('\'').Append(MySqlHelper.EscapeString(contentType)).Append("')");

            batchCount++;
            pendingInBatch++;

            if (batchCount >= _batchSize)
            {
                var sql = insertPrefix + insertBuilder.ToString();
                await using var cmd = new MySqlCommand(sql, connection);
                cmd.CommandTimeout = 300; // 5 minutes
                await cmd.ExecuteNonQueryAsync(cancellationToken);
                inserted += pendingInBatch;

                insertBuilder.Clear();
                batchCount = 0;
                pendingInBatch = 0;

                // Progress every batch
                Console.WriteLine($"[FileCatalog] Progress - Scanned: {scanned:N0}, Inserted: {inserted:N0}, Skipped: {skipped:N0}");
            }
        }

        // Final batch
        if (batchCount > 0)
        {
            var sql = insertPrefix + insertBuilder.ToString();
            await using var cmd = new MySqlCommand(sql, connection);
            cmd.CommandTimeout = 300; // 5 minutes
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            inserted += pendingInBatch;
        }

        // Update collection statistics
        await UpdateCollectionStatsAsync(connection, collectionId, cancellationToken);

        Console.WriteLine($"[FileCatalog] Complete! Scanned: {scanned:N0}, Inserted: {inserted:N0}, Skipped: {skipped:N0}");
        return (scanned, inserted, skipped);
    }
    
    /// <summary>
    /// Extracts identifier from filename based on expected file type.
    /// Returns (sourceId, sourceIdType, md5Hash).
    /// </summary>
    private static (string? SourceId, string SourceIdType, string? Md5) ExtractIdentifier(string fileName, string fileType)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        
        return fileType.ToLower() switch
        {
            "md5" => ExtractMd5Identifier(fileName),
            "aacid" => ExtractAacidIdentifier(fileName),
            "htid" => (nameWithoutExt, "htid", null),
            "ia_id" => (nameWithoutExt, "ia_id", null),
            _ => ExtractMd5Identifier(fileName) // Default to MD5
        };
    }
    
    private static (string? SourceId, string SourceIdType, string? Md5) ExtractMd5Identifier(string fileName)
    {
        var md5 = ExtractMd5FromFileName(fileName);
        if (md5 != null)
        {
            return (md5, "md5", md5);
        }
        return (null, "md5", null);
    }
    
    private static (string? SourceId, string SourceIdType, string? Md5) ExtractAacidIdentifier(string fileName)
    {
        // AACID pattern: aacid__hathitrust_files__20250610T193711Z__227PerkGiuEgQMaYRCWGqq
        // The last segment after __ is the unique ID
        if (fileName.StartsWith("aacid__", StringComparison.OrdinalIgnoreCase))
        {
            // Extract the full AACID as the identifier
            var aacid = Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
            {
                // No extension - use full filename
                aacid = fileName;
            }
            return (aacid, "aacid", null);
        }
        
        // Fall back to MD5 if not AACID pattern
        return ExtractMd5Identifier(fileName);
    }
    /// <summary>
    /// Creates a new collection in the bookcitycollection table.
    /// </summary>
    private async Task<Guid> CreateCollectionAsync(string collectionCode, CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        
        var collectionId = Guid.NewGuid();
        var sql = @"INSERT INTO bookcitycollection (CollectionId, CollectionCode, CollectionName) 
                    VALUES (@id, @code, @name)";
        
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", collectionId.ToString());
        cmd.Parameters.AddWithValue("@code", collectionCode);
        cmd.Parameters.AddWithValue("@name", collectionCode); // Use code as name initially
        
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return collectionId;
    }

    /// <summary>
    /// Updates collection statistics (file count, total size).
    /// </summary>
    private async Task UpdateCollectionStatsAsync(MySqlConnection connection, Guid collectionId, CancellationToken cancellationToken = default)
    {
        var sql = @"
            UPDATE bookcitycollection 
            SET FileCount = (SELECT COUNT(*) FROM bookcityfile WHERE CollectionId = @id),
                TotalSizeBytes = (SELECT COALESCE(SUM(FileSize), 0) FROM bookcityfile WHERE CollectionId = @id)
            WHERE CollectionId = @id";
        
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", collectionId.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Updates catalog entries with metadata from aarecords_codes joins.
    /// Links file catalog entries to titles/authors from metadata tables.
    /// </summary>
    public async Task<long> LinkMetadataAsync(string collectionCode, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[FileCatalog] Linking metadata for collection: {collectionCode}");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Get collection ID
        if (_collectionCache.Count == 0)
        {
            await LoadCollectionCacheAsync(cancellationToken);
        }
        
        if (!_collectionCache.TryGetValue(collectionCode, out var collectionId))
        {
            Console.WriteLine($"[FileCatalog] Collection '{collectionCode}' not found.");
            return 0;
        }

        // This query joins bookcityfile with aarecords to get title/author
        // The exact query depends on which metadata tables are populated
        // For now, try to link with hathitrust_records if available

        string updateSql = collectionCode.ToLower() switch
        {
            "hathitrust" => @"
                UPDATE bookcityfile fc
                JOIN aarecords_codes ac ON ac.code = CONCAT('md5:', fc.Md5Hash)
                JOIN annas_archive_meta__aacid__hathitrust_records hr 
                    ON hr.aacid = SUBSTRING_INDEX(ac.aarecord_id, ':', -1)
                SET fc.Title = JSON_UNQUOTE(JSON_EXTRACT(hr.metadata, '$.title')),
                    fc.Author = JSON_UNQUOTE(JSON_EXTRACT(hr.metadata, '$.author')),
                    fc.IsMetadataLinked = TRUE
                WHERE fc.CollectionId = @collectionId AND fc.IsMetadataLinked = FALSE
                LIMIT 10000",
            
            _ => @"
                UPDATE bookcityfile fc
                SET fc.IsMetadataLinked = TRUE,
                    fc.ResourcePath = CONCAT('http://bookcity/', @collectionCode, '/', fc.Md5Hash, '.', fc.FileExtension)
                WHERE fc.CollectionId = @collectionId AND fc.IsMetadataLinked = FALSE
                LIMIT 10000"
        };

        long totalUpdated = 0;
        int batchUpdated;

        do
        {
            await using var cmd = new MySqlCommand(updateSql, connection);
            cmd.Parameters.AddWithValue("@collectionId", collectionId.ToString());
            cmd.Parameters.AddWithValue("@collectionCode", collectionCode);
            
            batchUpdated = await cmd.ExecuteNonQueryAsync(cancellationToken);
            totalUpdated += batchUpdated;

            if (batchUpdated > 0)
            {
                Console.WriteLine($"[FileCatalog] Linked {totalUpdated:N0} records...");
            }
        }
        while (batchUpdated > 0 && !cancellationToken.IsCancellationRequested);

        Console.WriteLine($"[FileCatalog] Metadata linking complete. Total updated: {totalUpdated:N0}");
        return totalUpdated;
    }

    /// <summary>
    /// Updates resource paths to be more human-friendly using title info.
    /// </summary>
    public async Task<long> UpdateResourcePathsAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[FileCatalog] Updating resource paths with titles...");

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Update resource paths for entries that have titles
        const string sql = @"
            UPDATE bookcityfile f
            JOIN bookcitycollection c ON f.CollectionId = c.CollectionId
            SET f.ResourcePath = CONCAT(
                'http://bookcity/', 
                c.CollectionCode, '/',
                REPLACE(REPLACE(REPLACE(REPLACE(LOWER(SUBSTRING(COALESCE(f.Title, f.Md5Hash), 1, 100)), ' ', '-'), '/', '-'), ':', ''), '''', ''),
                '.', f.FileExtension
            )
            WHERE f.Title IS NOT NULL AND f.Title != ''
            LIMIT 10000";

        long totalUpdated = 0;
        int batchUpdated;

        do
        {
            await using var cmd = new MySqlCommand(sql, connection);
            batchUpdated = await cmd.ExecuteNonQueryAsync(cancellationToken);
            totalUpdated += batchUpdated;

            if (batchUpdated > 0)
            {
                Console.WriteLine($"[FileCatalog] Updated {totalUpdated:N0} resource paths...");
            }
        }
        while (batchUpdated > 0 && !cancellationToken.IsCancellationRequested);

        Console.WriteLine($"[FileCatalog] Resource path update complete. Total: {totalUpdated:N0}");
        return totalUpdated;
    }

    /// <summary>
    /// Extracts MD5 hash from filename.
    /// Handles patterns like: "002d6a691d6ba44177874fbb6ec6d297.pdf" or "md5_002d6a69..."
    /// </summary>
    private static string? ExtractMd5FromFileName(string fileName)
    {
        // Remove extension
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        // Pattern 1: Filename IS the MD5 (32 hex chars)
        if (Regex.IsMatch(nameWithoutExt, @"^[a-fA-F0-9]{32}$"))
        {
            return nameWithoutExt.ToLowerInvariant();
        }

        // Pattern 2: MD5 is at the start
        var match = Regex.Match(nameWithoutExt, @"^([a-fA-F0-9]{32})");
        if (match.Success)
        {
            return match.Groups[1].Value.ToLowerInvariant();
        }

        // Pattern 3: MD5 is somewhere in the name (less reliable)
        match = Regex.Match(nameWithoutExt, @"([a-fA-F0-9]{32})");
        if (match.Success)
        {
            return match.Groups[1].Value.ToLowerInvariant();
        }

        return null;
    }

    private static (string MimeType, string ContentType) GetFileType(string extension)
    {
        if (FileTypes.TryGetValue(extension, out var info))
        {
            return info;
        }
        return ("application/octet-stream", "unknown");
    }

    private static string BuildResourcePath(string collection, string md5, string fileName, string extension)
    {
        // Initial resource path using MD5 - will be updated later with title
        var safeName = SanitizeForUrl(Path.GetFileNameWithoutExtension(fileName));
        return $"http://bookcity/{collection}/{safeName}{extension}";
    }

    private static string SanitizeForUrl(string input)
    {
        if (string.IsNullOrEmpty(input)) return "unknown";
        
        // Convert to lowercase, replace spaces with dashes, remove special chars
        var result = input.ToLowerInvariant()
            .Replace(" ", "-")
            .Replace("/", "-")
            .Replace("\\", "-")
            .Replace(":", "")
            .Replace("'", "")
            .Replace("\"", "")
            .Replace("?", "")
            .Replace("&", "and");

        // Remove consecutive dashes
        result = Regex.Replace(result, @"-+", "-");

        // Trim to reasonable length
        if (result.Length > 100)
        {
            result = result.Substring(0, 100);
        }

        return result.Trim('-');
    }

    /// <summary>
    /// Gets statistics about the catalog.
    /// </summary>
    public async Task PrintStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        Console.WriteLine("\n=== BookCity File Catalog Statistics ===\n");

        // Total count
        await using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM bookcityfile", connection))
        {
            var total = await cmd.ExecuteScalarAsync(cancellationToken);
            Console.WriteLine($"Total files: {total:N0}");
        }

        // By collection
        Console.WriteLine("\nBy Collection:");
        await using (var cmd = new MySqlCommand(@"
            SELECT c.CollectionCode, c.CollectionName, c.FileCount, 
                   ROUND(c.TotalSizeBytes/1024/1024/1024, 2) as SizeGB
            FROM bookcitycollection c
            WHERE c.FileCount > 0
            ORDER BY c.FileCount DESC", connection))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                Console.WriteLine($"  {reader["CollectionCode"]}: {reader["FileCount"]:N0} files ({reader["SizeGB"]} GB)");
            }
        }

        // By content type
        Console.WriteLine("\nBy Content Type:");
        await using (var cmd = new MySqlCommand(@"
            SELECT ContentType, COUNT(*) as cnt
            FROM bookcityfile 
            GROUP BY ContentType 
            ORDER BY cnt DESC", connection))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                Console.WriteLine($"  {reader["ContentType"]}: {reader["cnt"]:N0}");
            }
        }

        // Metadata link status
        Console.WriteLine("\nMetadata Status:");
        await using (var cmd = new MySqlCommand(@"
            SELECT 
                SUM(CASE WHEN IsMetadataLinked THEN 1 ELSE 0 END) as linked,
                SUM(CASE WHEN NOT IsMetadataLinked THEN 1 ELSE 0 END) as unlinked
            FROM bookcityfile", connection))
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                Console.WriteLine($"  Linked to metadata: {reader["linked"]:N0}");
                Console.WriteLine($"  Unlinked: {reader["unlinked"]:N0}");
            }
        }
    }
}