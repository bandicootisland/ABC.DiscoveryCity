

namespace ABC.DiscoveryCity.PostgreSQL;

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
    /// Gets the page previews for a scimag record.
    /// </summary>
    public async Task<List<ScimagPreview>> GetScimagPreviewsAsync(string doi, CancellationToken ct = default)
    {
        await using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        var sql = "SELECT Doi, PageNumber, ImageData, ContentType FROM PaperPreviews WHERE Doi = @doi ORDER BY PageNumber";
        await using var cmd = new Npgsql.NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("doi", doi);

        var previews = new List<ScimagPreview>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            previews.Add(new ScimagPreview
            {
                Doi = reader["Doi"] as string ?? "",
                PageNumber = reader["PageNumber"] as int? ?? 0,
                ImageData = reader["ImageData"] as byte[] ?? Array.Empty<byte>(),
                ContentType = reader["ContentType"] as string ?? "image/png"
            });
        }

        return previews;
    }
}

// DTOs shared between service and controller
public class ScimagPreview
{
    public string Doi { get; set; } = "";
    public int PageNumber { get; set; }
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/png";
}


