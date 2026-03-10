using Npgsql;

namespace ABC.DiscoveryCity.PostgreSQL;

/// <summary>
/// Storage and retrieval for dictionary packages (XLSX blobs).
/// Ported from BookCity — stores OED binary data for word splitting.
/// </summary>
public class DictionaryStorageService
{
    private readonly NpgsqlDataSource _dataSource;

    public DictionaryStorageService(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Stores a dictionary XLSX package blob. Upserts by dictionary_name.
    /// </summary>
    public void StoreDictionary(string dictionaryName, string? version, int entryCount, byte[] xlsxData)
    {
        if (xlsxData == null || xlsxData.Length == 0) return;

        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            INSERT INTO dictionary_packages (id, dictionary_name, version, entry_count, package_data)
            VALUES (@id, @name, @ver, @cnt, @data)
            ON CONFLICT (dictionary_name) DO UPDATE SET
                version = EXCLUDED.version,
                entry_count = EXCLUDED.entry_count,
                package_data = EXCLUDED.package_data,
                updated_at = NOW();", conn);

        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("name", dictionaryName);
        cmd.Parameters.AddWithValue("ver", (object?)version ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cnt", entryCount);
        cmd.Parameters.AddWithValue("data", xlsxData);
        cmd.ExecuteNonQuery();

        Console.WriteLine($"  [DictStorage] Stored '{dictionaryName}' v{version} ({entryCount:N0} entries, {xlsxData.Length / 1024.0 / 1024.0:F2} MB)");
    }

    /// <summary>
    /// Loads the XLSX package blob for a dictionary. Returns null if not found.
    /// </summary>
    public byte[]? LoadDictionaryPackage(string dictionaryName)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT package_data FROM dictionary_packages WHERE dictionary_name = @name;", conn);
        cmd.Parameters.AddWithValue("name", dictionaryName);

        var result = cmd.ExecuteScalar();
        if (result is byte[] data)
        {
            Console.WriteLine($"  [DictStorage] Loaded '{dictionaryName}' ({data.Length / 1024.0 / 1024.0:F2} MB)");
            return data;
        }
        return null;
    }

    /// <summary>
    /// Checks if a dictionary exists in the database.
    /// </summary>
    public bool Exists(string dictionaryName)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM dictionary_packages WHERE dictionary_name = @name;", conn);
        cmd.Parameters.AddWithValue("name", dictionaryName);
        return cmd.ExecuteScalar() != null;
    }
}
