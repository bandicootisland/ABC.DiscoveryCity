using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using System.Text.Json;
using ABC.DiscoveryCity.Embeddings;

namespace ABC.DiscoveryCity.PostgreSQL;

public class DbService
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService? _embeddingService;
    private static bool _pgvectorMapped = false;
    private static readonly object _mapLock = new();

    // Default connection string for convenience, but allows override
    private const string DefaultConnectionString = "Host=127.0.0.1;Port=5435;Username=discovery_user;Password=discovery_password;Database=DiscoveryCity";

    public DbService(IEmbeddingService? embeddingService = null, string? connectionString = null)
    {
        _embeddingService = embeddingService;
        _connectionString = connectionString ?? DefaultConnectionString;
        
        EnsurePgvectorMapping();
        var builder = new NpgsqlDataSourceBuilder(_connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    private static void EnsurePgvectorMapping()
    {
        if (_pgvectorMapped) return;
        lock (_mapLock)
        {
            if (_pgvectorMapped) return;
            _pgvectorMapped = true;
        }
    }

    public void InitDb()
    {
        try
        {
            using var conn = _dataSource.OpenConnection();

            Console.WriteLine($"Connected to DB: {_connectionString.Replace("discovery_password", "***")}");

            // 1. Enable Vector Extension
            using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn))
            {
                cmd.ExecuteNonQuery();
            }

            // Cleanup old table if exists
            using (var cmd = new NpgsqlCommand("DROP TABLE IF EXISTS Documents;", conn)) cmd.ExecuteNonQuery();

            // 2. Create Sources Table (top-level organization)
            Console.WriteLine("Creating Sources table...");
            string createSourcesTableSql = @"
                CREATE TABLE IF NOT EXISTS Sources (
                    Id SERIAL PRIMARY KEY,
                    Name TEXT NOT NULL UNIQUE,
                    Url TEXT,
                    BaseFilePath TEXT NOT NULL,
                    CreatedAt TIMESTAMPTZ DEFAULT NOW()
                );
            ";
            using (var cmd = new NpgsqlCommand(createSourcesTableSql, conn)) cmd.ExecuteNonQuery();

            // 3. Create DataSets Table (child of Sources)
            Console.WriteLine("Creating DataSets table...");
            string createDataSetsTableSql = @"
                CREATE TABLE IF NOT EXISTS DataSets (
                    Id SERIAL PRIMARY KEY,
                    SourceId INT REFERENCES Sources(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL,
                    CreatedAt TIMESTAMPTZ DEFAULT NOW(),
                    UNIQUE(SourceId, Name)
                );
            ";
            using (var cmd = new NpgsqlCommand(createDataSetsTableSql, conn)) cmd.ExecuteNonQuery();

            // 4. Create Parent Documents Table (child of DataSets)
            Console.WriteLine("Creating ParentDocuments table...");
            string createParentTableSql = @"
                CREATE TABLE IF NOT EXISTS ParentDocuments (
                    Id SERIAL PRIMARY KEY,
                    DataSetId INT REFERENCES DataSets(Id),
                    FilePath TEXT UNIQUE NOT NULL,
                    Metadata JSONB,
                    CreatedAt TIMESTAMPTZ DEFAULT NOW(),
                    ProcessedAt TIMESTAMPTZ DEFAULT NOW()
                );
            ";
            using (var cmd = new NpgsqlCommand(createParentTableSql, conn)) cmd.ExecuteNonQuery();

            // Add DataSetId column if table already exists without it
            using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ADD COLUMN IF NOT EXISTS DataSetId INT REFERENCES DataSets(Id);", conn)) cmd.ExecuteNonQuery();

            // 3. Create Chunks Table
            Console.WriteLine("Creating DocumentChunks table...");
            string createChunksTableSql = @"
                CREATE TABLE IF NOT EXISTS DocumentChunks (
                    Id SERIAL PRIMARY KEY,
                    ParentId INT REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
                    ChunkIndex INT,
                    TextContent TEXT,
                    Embedding vector(384), 
                    CreatedAt TIMESTAMPTZ DEFAULT NOW()
                );
            ";
            using (var cmd = new NpgsqlCommand(createChunksTableSql, conn)) cmd.ExecuteNonQuery();

            // 4. Create DocumentImages Table
            Console.WriteLine("Creating DocumentImages table...");
            string createImagesTableSql = @"
                CREATE TABLE IF NOT EXISTS DocumentImages (
                    Id SERIAL PRIMARY KEY,
                    ParentId INT REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
                    ImageType TEXT NOT NULL,
                    ImageSize TEXT NOT NULL,
                    FilePath TEXT NOT NULL,
                    Width INT,
                    Height INT,
                    CreatedAt TIMESTAMPTZ DEFAULT NOW()
                );
            ";
            using (var cmd = new NpgsqlCommand(createImagesTableSql, conn)) cmd.ExecuteNonQuery();

            // 5. Create Indexes
            using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_parent_metadata ON ParentDocuments USING GIN (Metadata);", conn)) cmd.ExecuteNonQuery();
            using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_chunks_parentid ON DocumentChunks(ParentId);", conn)) cmd.ExecuteNonQuery();
            using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_docimages_parentid ON DocumentImages(ParentId);", conn)) cmd.ExecuteNonQuery();

            Console.WriteLine("Database Schema Initialized Successfully.");
            
            // Verify
            using (var cmd = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_name = 'parentdocuments';", conn))
            {
                var count = (long)(cmd.ExecuteScalar() ?? 0L);
                Console.WriteLine($"Table Verification: {count} (Should be 1)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL Error initializing Database: {ex.Message}");
            throw; // Re-throw to stop program
        }
    }

    /// <summary>
    /// Get or create a Source by name. Returns the Source Id.
    /// </summary>
    public int GetOrCreateSource(string name, string baseFilePath, string? url = null)
    {
        using var conn = _dataSource.OpenConnection();
        string sql = @"
            INSERT INTO Sources (Name, BaseFilePath, Url)
            VALUES (@name, @basePath, @url)
            ON CONFLICT (Name) DO UPDATE SET Name = EXCLUDED.Name
            RETURNING Id;
        ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("basePath", baseFilePath);
        cmd.Parameters.AddWithValue("url", (object?)url ?? DBNull.Value);
        return (int)(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>
    /// Get or create a DataSet by name for a given Source. Returns the DataSet Id.
    /// </summary>
    public int GetOrCreateDataSet(int sourceId, string name)
    {
        using var conn = _dataSource.OpenConnection();
        string sql = @"
            INSERT INTO DataSets (SourceId, Name)
            VALUES (@sourceId, @name)
            ON CONFLICT (SourceId, Name) DO UPDATE SET Name = EXCLUDED.Name
            RETURNING Id;
        ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("name", name);
        return (int)(cmd.ExecuteScalar() ?? 0);
    }

    public void InsertDocument(string filePath, PdfMetadata metadata, int? dataSetId = null)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var trans = conn.BeginTransaction();

            try
            {
                // 1. Upsert Parent Document
                string json = JsonSerializer.Serialize(metadata);
                int parentId = 0;

                string parentSql = @"
                    INSERT INTO ParentDocuments (FilePath, Metadata, DataSetId, ProcessedAt)
                    VALUES (@fp, @meta::jsonb, @dataSetId, NOW())
                    ON CONFLICT (FilePath) 
                    DO UPDATE SET 
                        Metadata = EXCLUDED.Metadata, 
                        DataSetId = COALESCE(EXCLUDED.DataSetId, ParentDocuments.DataSetId),
                        ProcessedAt = NOW()
                    RETURNING Id;
                ";

                using (var cmd = new NpgsqlCommand(parentSql, conn, trans))
                {
                    cmd.Parameters.AddWithValue("fp", filePath);
                    cmd.Parameters.AddWithValue("meta", json);
                    cmd.Parameters.AddWithValue("dataSetId", (object?)dataSetId ?? DBNull.Value);
                    var scalar = cmd.ExecuteScalar();
                    parentId = scalar != null ? (int)scalar : 0;
                }

                // 2. Delete existing chunks for this document (re-processing case)
                using (var cmd = new NpgsqlCommand("DELETE FROM DocumentChunks WHERE ParentId = @pid", conn, trans))
                {
                    cmd.Parameters.AddWithValue("pid", parentId);
                    cmd.ExecuteNonQuery();
                }

                // 3. Chunk and Insert
                // Strategy: Smart Chunking (max 2048 chars or 25 sentences)
                var sentences = metadata.Text ?? new List<string>();
                
                var currentChunk = new List<string>();
                int currentLength = 0;
                int chunkIndex = 0; // Use a local counter

                // Local function to write chunk
                void WriteChunk()
                {
                    if (currentChunk.Count == 0) return;
                    
                    string chunkText = string.Join(" ", currentChunk);
                    
                    // Generate embedding if service available
                    float[]? embedding = null;
                    if (_embeddingService != null)
                    {
                        try
                        {
                            embedding = _embeddingService.GetEmbeddingAsync(chunkText).GetAwaiter().GetResult();
                        }
                        catch (Exception embEx)
                        {
                            Console.WriteLine($"  [WARN] Embedding failed: {embEx.Message}");
                        }
                    }
                    
                    string chunkSql = @"
                        INSERT INTO DocumentChunks (ParentId, ChunkIndex, TextContent, Embedding)
                        VALUES (@pid, @idx, @txt, @emb);
                    ";
                    
                    using (var cmd = new NpgsqlCommand(chunkSql, conn, trans))
                    {
                        cmd.Parameters.AddWithValue("pid", parentId);
                        cmd.Parameters.AddWithValue("idx", chunkIndex);
                        cmd.Parameters.AddWithValue("txt", chunkText);
                        if (embedding != null)
                            cmd.Parameters.AddWithValue("emb", new Vector(embedding));
                        else
                            cmd.Parameters.AddWithValue("emb", DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }

                    chunkIndex++; // Increment for next chunk
                    currentChunk.Clear();
                    currentLength = 0;
                }

                foreach (var s in sentences)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    
                    // If adding this sentence exceeds limits, write current chunk first
                    // Limit: 2048 chars or 25 sentences
                    if (currentChunk.Count > 0 && (currentLength + s.Length > 2048 || currentChunk.Count >= 25))
                    {
                        WriteChunk();
                    }

                    currentChunk.Add(s);
                    currentLength += s.Length;
                }
                
                // Write final chunk
                WriteChunk();

                trans.Commit();
                Console.WriteLine($"Saved to DB: {filePath} with {Math.Ceiling((double)sentences.Count/25.0)} chunks.");
            }
            catch
            {
                trans.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
             Console.WriteLine($"Error saving to DB for {filePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Insert an image record for a document.
    /// </summary>
    public void InsertImage(string parentFilePath, string imageType, string imageSize, string imagePath, int width, int height)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();

            // Get parent ID
            string parentIdSql = "SELECT Id FROM ParentDocuments WHERE FilePath = @fp LIMIT 1;";
            int parentId = 0;
            using (var cmd = new NpgsqlCommand(parentIdSql, conn))
            {
                cmd.Parameters.AddWithValue("fp", parentFilePath);
                var result = cmd.ExecuteScalar();
                if (result == null) return; // Parent not found
                parentId = (int)result;
            }

            // Delete existing image record for this size to avoid duplicates
            string deleteSql = "DELETE FROM DocumentImages WHERE ParentId = @pid AND ImageSize = @size;";
            using (var cmd = new NpgsqlCommand(deleteSql, conn))
            {
                cmd.Parameters.AddWithValue("pid", parentId);
                cmd.Parameters.AddWithValue("size", imageSize);
                cmd.ExecuteNonQuery();
            }

            // Insert image record
            string insertSql = @"
                INSERT INTO DocumentImages (ParentId, ImageType, ImageSize, FilePath, Width, Height)
                VALUES (@pid, @type, @size, @path, @w, @h);
            ";
            using (var cmd = new NpgsqlCommand(insertSql, conn))
            {
                cmd.Parameters.AddWithValue("pid", parentId);
                cmd.Parameters.AddWithValue("type", imageType);
                cmd.Parameters.AddWithValue("size", imageSize);
                cmd.Parameters.AddWithValue("path", imagePath);
                cmd.Parameters.AddWithValue("w", width);
                cmd.Parameters.AddWithValue("h", height);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] InsertImage failed: {ex.Message}");
        }
    }

    public async Task<List<(string FilePath, string Text, double Distance, DateTime? Date, int PageCount, string? Thumbnail, string? SourceName, string? DataSetName)>> SearchSimilarAsync(string query, int limit = 20)
    {
        var results = new List<(string, string, double, DateTime?, int, string?, string?, string?)>();

        try
        {
            using var conn = _dataSource.OpenConnection();
            
            // Text-based search using ILIKE on chunk text content
            // This is a fallback while pgvector compatibility is resolved
            string sql = @"
                SELECT DISTINCT ON (p.Id)
                       p.FilePath, 
                       c.TextContent, 
                       0.0 as Distance,
                       (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                       (p.Metadata->>'PageCount')::int as PageCount,
                       (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbPath,
                       s.Name as SourceName,
                       d.Name as DataSetName
                FROM DocumentChunks c
                JOIN ParentDocuments p ON c.ParentId = p.Id
                LEFT JOIN DataSets d ON p.DataSetId = d.Id
                LEFT JOIN Sources s ON d.SourceId = s.Id
                WHERE c.TextContent ILIKE @searchPattern
                ORDER BY p.Id, p.ProcessedAt DESC
                LIMIT @limit;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("searchPattern", $"%{query}%");
            cmd.Parameters.AddWithValue("limit", limit);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetDouble(2),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Search failed: {ex}");
        }
        return results;
    }

    public List<(string FilePath, string Text, double Distance, DateTime? Date, int PageCount, string? Thumbnail, string? SourceName, string? DataSetName)> GetRecentDocuments(int limit = 10)
    {
        var results = new List<(string, string, double, DateTime?, int, string?, string?, string?)>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                SELECT p.FilePath, 
                       (SELECT c.TextContent FROM DocumentChunks c WHERE c.ParentId = p.Id ORDER BY c.ChunkIndex LIMIT 1) as Text,
                       0.0 as Distance,
                       (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                       (p.Metadata->>'PageCount')::int as PageCount,
                       (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbPath,
                       s.Name as SourceName,
                       d.Name as DataSetName
                FROM ParentDocuments p
                LEFT JOIN DataSets d ON p.DataSetId = d.Id
                LEFT JOIN Sources s ON d.SourceId = s.Id
                ORDER BY p.ProcessedAt DESC
                LIMIT @limit;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "No text content" : reader.GetString(1),
                    reader.GetDouble(2),
                    reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting recent docs: {ex.Message}");
        }
        return results;
    }

    public (long Docs, long Images, long Chunks) GetCounts()
    {
        try 
        {
            using var conn = _dataSource.OpenConnection();
            using var cmdDocs = new NpgsqlCommand("SELECT count(*) FROM ParentDocuments", conn);
            long docs = (long)cmdDocs.ExecuteScalar();
            
            using var cmdImages = new NpgsqlCommand("SELECT count(*) FROM DocumentImages", conn);
            long images = (long)cmdImages.ExecuteScalar();
            
            using var cmdChunks = new NpgsqlCommand("SELECT count(*) FROM DocumentChunks", conn);
            long chunks = (long)cmdChunks.ExecuteScalar();
            
            return (docs, images, chunks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting counts: {ex.Message}");
            return (0, 0, 0);
        }
    }

    public List<(string ImageType, string ImageSize, string FilePath, int Width, int Height)> GetDocumentImages(string parentFilePath)
    {
        var results = new List<(string, string, string, int, int)>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                SELECT i.ImageType, i.ImageSize, i.FilePath, i.Width, i.Height
                FROM DocumentImages i
                JOIN ParentDocuments p ON i.ParentId = p.Id
                WHERE p.FilePath = @fp
                ORDER BY i.ImageSize;
            ";
            
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("fp", parentFilePath);
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4)
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting images: {ex.Message}");
        }
        return results;
    }
}
