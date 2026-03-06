using Npgsql;

namespace ABC.DiscoveryCity.PostgreSQL;

/// <summary>
/// User-supplied files: uploaded PDFs/documents that supplement the discovery collection.
/// Each record links a user-uploaded file to the original ParentDocument it relates to.
/// </summary>
public partial class DbService
{
    public void InitUserEditsTables()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS UserEdits (
                Id              SERIAL PRIMARY KEY,
                DocumentPath    TEXT NOT NULL,
                FileName        TEXT NOT NULL,
                FileSize        BIGINT NOT NULL DEFAULT 0,
                ContentType     TEXT NOT NULL DEFAULT 'application/pdf',
                StoredPath      TEXT NOT NULL,
                UploadedAt      TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_useredits_docpath ON UserEdits(DocumentPath);
        ", conn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Record a user-uploaded file. Returns the new row Id.
    /// </summary>
    public int InsertUserEdit(string documentPath, string fileName, long fileSize, string contentType, string storedPath)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            INSERT INTO UserEdits (DocumentPath, FileName, FileSize, ContentType, StoredPath)
            VALUES (@documentPath, @fileName, @fileSize, @contentType, @storedPath)
            RETURNING Id", conn);
        cmd.Parameters.AddWithValue("documentPath", documentPath);
        cmd.Parameters.AddWithValue("fileName", fileName);
        cmd.Parameters.AddWithValue("fileSize", fileSize);
        cmd.Parameters.AddWithValue("contentType", contentType);
        cmd.Parameters.AddWithValue("storedPath", storedPath);
        return (int)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Get all user-uploaded files for a given document path.
    /// </summary>
    public List<UserEditDto> GetUserEdits(string documentPath)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT Id, DocumentPath, FileName, FileSize, ContentType, StoredPath, UploadedAt FROM UserEdits WHERE DocumentPath = @documentPath ORDER BY UploadedAt DESC",
            conn);
        cmd.Parameters.AddWithValue("documentPath", documentPath);

        var results = new List<UserEditDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new UserEditDto
            {
                Id = reader.GetInt32(0),
                DocumentPath = reader.GetString(1),
                FileName = reader.GetString(2),
                FileSize = reader.GetInt64(3),
                ContentType = reader.GetString(4),
                StoredPath = reader.GetString(5),
                UploadedAt = reader.GetDateTime(6)
            });
        }
        return results;
    }

    /// <summary>
    /// Get a single user edit by Id.
    /// </summary>
    public UserEditDto? GetUserEdit(int id)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT Id, DocumentPath, FileName, FileSize, ContentType, StoredPath, UploadedAt FROM UserEdits WHERE Id = @id",
            conn);
        cmd.Parameters.AddWithValue("id", id);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new UserEditDto
            {
                Id = reader.GetInt32(0),
                DocumentPath = reader.GetString(1),
                FileName = reader.GetString(2),
                FileSize = reader.GetInt64(3),
                ContentType = reader.GetString(4),
                StoredPath = reader.GetString(5),
                UploadedAt = reader.GetDateTime(6)
            };
        }
        return null;
    }
}

public class UserEditDto
{
    public int Id { get; set; }
    public string DocumentPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string ContentType { get; set; } = "application/pdf";
    public string StoredPath { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}
