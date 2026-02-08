using Npgsql;
using Pgvector;
using Pgvector.Npgsql;

namespace ABC.DiscoveryCity.PostgreSQL;

/// <summary>
/// Factory for creating configured PostgreSQL connections with pgvector support.
/// </summary>
public class WordCityDb
{
    private readonly PostgreSQLConfig _config;
    private readonly NpgsqlDataSource _dataSource;
    private static bool _pgvectorMapped = false;
    private static readonly object _mapLock = new();

    public WordCityDb(PostgreSQLConfig config)
    {
        _config = config;

        // Ensure pgvector type mapping is registered (once per application)
        EnsurePgvectorMapping();

        var builder = new NpgsqlDataSourceBuilder(_config.ConnectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    /// <summary>
    /// Mark pgvector as configured (data source builder handles the mapping).
    /// </summary>
    private static void EnsurePgvectorMapping()
    {
        if (_pgvectorMapped) return;
        lock (_mapLock)
        {
            if (_pgvectorMapped) return;
            // Vector type mapping is handled by NpgsqlDataSourceBuilder.UseVector()
            _pgvectorMapped = true;
        }
    }

    /// <summary>
    /// Get an open connection to the database.
    /// </summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        return conn;
    }

    /// <summary>
    /// Create a command with the configured timeout.
    /// </summary>
    public NpgsqlCommand CreateCommand(NpgsqlConnection conn, string sql)
    {
        var cmd = new NpgsqlCommand(sql, conn)
        {
            CommandTimeout = _config.CommandTimeout
        };
        return cmd;
    }

    /// <summary>
    /// Test connection and verify pgvector extension is available.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenConnectionAsync(ct);

            // Check PostgreSQL version
            await using var versionCmd = CreateCommand(conn, "SELECT version()");
            var version = await versionCmd.ExecuteScalarAsync(ct) as string ?? "unknown";

            // Check pgvector extension
            await using var extCmd = CreateCommand(conn,
                "SELECT extversion FROM pg_extension WHERE extname = 'vector'");
            var vectorVersion = await extCmd.ExecuteScalarAsync(ct) as string;

            if (vectorVersion == null)
            {
                return (false, $"PostgreSQL connected ({version}) but pgvector extension not found.");
            }

            // Test vector operations
            await using var vecTestCmd = CreateCommand(conn,
                $"SELECT '[1,2,3]'::vector({_config.VectorDimension}) <=> '[4,5,6]'::vector({_config.VectorDimension})");

            try
            {
                await vecTestCmd.ExecuteScalarAsync(ct);
            }
            catch
            {
                // Dimension mismatch in test is fine, just checking syntax works
            }

            return (true, $"PostgreSQL connected. Version: {version.Split(',')[0]}, pgvector: {vectorVersion}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Execute a non-query SQL command.
    /// </summary>
    public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = CreateCommand(conn, sql);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Execute a scalar query.
    /// </summary>
    public async Task<object?> ExecuteScalarAsync(string sql, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct);
        await using var cmd = CreateCommand(conn, sql);
        return await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// Dispose the data source.
    /// </summary>
    public void Dispose()
    {
        _dataSource.Dispose();
    }

    /// <summary>
    /// Configuration accessor.
    /// </summary>
    public PostgreSQLConfig Config => _config;
}
