using MySqlConnector;

namespace ABC.BookCity.MariaDB;

/// <summary>
/// Service for retrieving book information and page content for the viewer.
/// </summary>
public class BookViewerService
{
    private readonly string _connectionString;

    public BookViewerService(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>
    /// Gets a paginated list of HathiTrust books.
    /// </summary>
    public async Task<List<HathiTrustBookListItem>> GetHathiTrustBooksAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        bool linkedOnly = false,
        CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var offset = (page - 1) * pageSize;

        var sql = @"
            SELECT 
                f.FileId, f.FileName, f.FileSize, f.SourceId, f.PageCount, f.ResourcePath,
                h.title, h.author, h.lang, h.access,
                CASE WHEN h.htid IS NOT NULL THEN 1 ELSE 0 END AS HasMetadata,
                f.LibraryId, f.LibraryIdType
            FROM bookcityfile f
            INNER JOIN bookcitycollection c ON f.CollectionId = c.CollectionId
            LEFT JOIN hathitrust_records h ON f.LibraryId = h.htid
            WHERE c.CollectionCode = 'hathitrust'";

        if (linkedOnly)
        {
            sql += " AND f.LibraryId IS NOT NULL";
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            sql += @" AND (
                h.title LIKE @search 
                OR h.author LIKE @search 
                OR f.FileName LIKE @search
                OR f.LibraryId LIKE @search
            )";
        }

        sql += " ORDER BY COALESCE(h.title, f.FileName) LIMIT @limit OFFSET @offset";

        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", pageSize);
        cmd.Parameters.AddWithValue("@offset", offset);
        
        if (!string.IsNullOrWhiteSpace(search))
        {
            cmd.Parameters.AddWithValue("@search", $"%{search}%");
        }

        cmd.CommandTimeout = 120;

        var books = new List<HathiTrustBookListItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        
        while (await reader.ReadAsync(ct))
        {
            var fileIdVal = reader.GetValue(0);
            var fileId = fileIdVal is Guid g ? g : Guid.Parse(fileIdVal.ToString()!);
            var libraryId = reader.IsDBNull(11) ? null : reader.GetString(11);
            var libraryIdType = reader.IsDBNull(12) ? null : reader.GetString(12);
            
            // Only use LibraryId as HTID if it's been linked (LibraryIdType = 'htid')
            var isLinked = libraryIdType == "htid";
            var htid = isLinked ? libraryId : null;

            books.Add(new HathiTrustBookListItem
            {
                FileId = fileId,
                FileName = reader.GetString(1),
                FileSize = reader.GetInt64(2),
                Htid = htid,
                PageCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                PublicUrl = htid != null ? HathiTrustLinker.GetPublicUrl(htid) : null,
                Title = reader.IsDBNull(6) ? null : reader.GetString(6),
                Author = reader.IsDBNull(7) ? null : reader.GetString(7),
                Language = reader.IsDBNull(8) ? null : reader.GetString(8),
                Access = reader.IsDBNull(9) ? null : reader.GetString(9),
                HasMetadata = reader.GetInt32(10) == 1
            });
        }

        return books;
    }

    /// <summary>
    /// Gets the page content for a specific page.
    /// </summary>
    public async Task<PageContent?> GetPageContentAsync(Guid fileId, int pageNumber, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT FilePath, LibraryId, PageCount, LibraryIdType
            FROM bookcityfile
            WHERE FileId = @fileId";

        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fileId", fileId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var filePath = reader.GetString(0);
        var libraryId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var totalPages = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        var libraryIdType = reader.IsDBNull(3) ? null : reader.GetString(3);
        
        // Only use LibraryId as HTID if it's been linked
        var isLinked = libraryIdType == "htid";
        var htid = isLinked ? libraryId : null;

        // Read the page from the ZIP file
        var text = HathiTrustLinker.ReadPage(filePath, pageNumber);
        if (text == null && pageNumber == 1)
        {
            // If first page fails, maybe we need to refresh page count
            var pages = HathiTrustLinker.GetPageList(filePath);
            totalPages = pages.Count;
            if (pages.Count > 0)
            {
                text = HathiTrustLinker.ReadPage(filePath, 1);
            }
        }

        return new PageContent
        {
            PageNumber = pageNumber,
            TotalPages = totalPages > 0 ? totalPages : HathiTrustLinker.GetPageCount(filePath),
            Text = text ?? "[Page content not available]",
            Htid = htid,
            PageImageUrl = htid != null ? HathiTrustLinker.GetPageImageUrl(htid, pageNumber) : null,
            PublicUrl = htid != null ? HathiTrustLinker.GetPublicPageUrl(htid, pageNumber) : null
        };
    }

    /// <summary>
    /// Gets the list of pages in a book.
    /// </summary>
    public async Task<PageListResponse> GetPageListAsync(Guid fileId, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = "SELECT FilePath, LibraryId FROM bookcityfile WHERE FileId = @fileId";

        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@fileId", fileId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new PageListResponse { FileId = fileId, TotalPages = 0 };
        }

        var filePath = reader.GetString(0);
        var htid = reader.IsDBNull(1) ? null : reader.GetString(1);

        var pageNames = HathiTrustLinker.GetPageList(filePath);

        return new PageListResponse
        {
            FileId = fileId,
            Htid = htid,
            TotalPages = pageNames.Count,
            PageNames = pageNames
        };
    }

    /// <summary>
    /// Gets summary statistics.
    /// </summary>
    public async Task<HathiTrustSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        const string sql = @"
            SELECT 
                COUNT(*) AS TotalFiles,
                SUM(CASE WHEN f.LibraryId IS NOT NULL THEN 1 ELSE 0 END) AS LinkedFiles,
                SUM(CASE WHEN f.LibraryId IS NULL THEN 1 ELSE 0 END) AS UnlinkedFiles,
                SUM(CASE WHEN h.htid IS NOT NULL THEN 1 ELSE 0 END) AS WithMetadata,
                COALESCE(SUM(f.PageCount), 0) AS TotalPages,
                ROUND(SUM(f.FileSize) / 1024 / 1024 / 1024, 2) AS TotalSizeGB
            FROM bookcityfile f
            INNER JOIN bookcitycollection c ON f.CollectionId = c.CollectionId
            LEFT JOIN hathitrust_records h ON f.LibraryId = h.htid
            WHERE c.CollectionCode = 'hathitrust'";

        await using var cmd = new MySqlCommand(sql, connection);
        cmd.CommandTimeout = 120;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new HathiTrustSummary();
        }

        return new HathiTrustSummary
        {
            TotalFiles = reader.GetInt32(0),
            LinkedFiles = reader.GetInt32(1),
            UnlinkedFiles = reader.GetInt32(2),
            WithMetadata = reader.GetInt32(3),
            TotalPages = reader.GetInt64(4),
            TotalSizeGB = reader.GetDouble(5)
        };
    }
}

// DTOs shared between service and controller
public class HathiTrustBookListItem
{
    public Guid FileId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string? Htid { get; set; }
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Language { get; set; }
    public string? Access { get; set; }
    public string? PublicUrl { get; set; }
    public int PageCount { get; set; }
    public bool HasMetadata { get; set; }
}

public class PageContent
{
    public int PageNumber { get; set; }
    public int TotalPages { get; set; }
    public string Text { get; set; } = "";
    public string? Htid { get; set; }
    public string? PageImageUrl { get; set; }
    public string? PublicUrl { get; set; }
}

public class PageListResponse
{
    public Guid FileId { get; set; }
    public string? Htid { get; set; }
    public int TotalPages { get; set; }
    public List<string> PageNames { get; set; } = new();
}

public class HathiTrustSummary
{
    public int TotalFiles { get; set; }
    public int LinkedFiles { get; set; }
    public int UnlinkedFiles { get; set; }
    public int WithMetadata { get; set; }
    public long TotalPages { get; set; }
    public double TotalSizeGB { get; set; }
}
