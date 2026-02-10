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
    private const string DefaultConnectionString = "Host=192.168.1.114;Port=5435;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo;Database=discoverycity";

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

            // 5. Create DocumentChunks Table with HASH Partitioning (4 partitions for parallel vector search)
            Console.WriteLine("Creating DocumentChunks partitioned table...");

            // Check if table exists and is already partitioned
            bool chunksTableExists = false;
            using (var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name = 'documentchunks');", conn))
            {
                chunksTableExists = (bool)(cmd.ExecuteScalar() ?? false);
            }

            if (!chunksTableExists)
            {
                // Create partitioned table
                string createChunksTableSql = @"
                    CREATE TABLE DocumentChunks (
                        Id SERIAL,
                        ParentId INT NOT NULL,
                        ChunkIndex INT,
                        TextContent TEXT,
                        Embedding vector(384),
                        CreatedAt TIMESTAMPTZ DEFAULT NOW(),
                        PRIMARY KEY (Id, ParentId)
                    ) PARTITION BY HASH (ParentId);
                ";
                using (var cmd = new NpgsqlCommand(createChunksTableSql, conn)) cmd.ExecuteNonQuery();

                // Create 4 hash partitions
                for (int i = 0; i < 4; i++)
                {
                    string partitionSql = $@"
                        CREATE TABLE DocumentChunks_p{i} PARTITION OF DocumentChunks
                        FOR VALUES WITH (MODULUS 4, REMAINDER {i});
                    ";
                    using (var cmd = new NpgsqlCommand(partitionSql, conn)) cmd.ExecuteNonQuery();
                    Console.WriteLine($"  Created partition DocumentChunks_p{i}");
                }

                // Add FK constraint (works on partitioned tables pointing TO regular tables)
                using (var cmd = new NpgsqlCommand(@"
                    ALTER TABLE DocumentChunks ADD CONSTRAINT fk_chunks_parent
                    FOREIGN KEY (ParentId) REFERENCES ParentDocuments(Id) ON DELETE CASCADE;", conn))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            // 6. Create DocumentImages Table
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
                    CreatedAt TIMESTAMPTZ DEFAULT NOW(),
                    UNIQUE(ParentId, ImageSize)
                );
            ";
            using (var cmd = new NpgsqlCommand(createImagesTableSql, conn)) cmd.ExecuteNonQuery();

            // Add unique constraint if table already exists without it
            using (var cmd = new NpgsqlCommand(@"
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'documentimages_parentid_imagesize_key') THEN
                        ALTER TABLE DocumentImages ADD CONSTRAINT documentimages_parentid_imagesize_key UNIQUE (ParentId, ImageSize);
                    END IF;
                END $$;", conn)) cmd.ExecuteNonQuery();

            // 7. Create Indexes (skip if already exist)
            int indexCount = 0;
            using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE indexname LIKE 'idx_chunks_%' OR indexname LIKE 'idx_parent_%';", conn))
            {
                indexCount = (int)(long)(cmd.ExecuteScalar() ?? 0L);
            }

            if (indexCount >= 3)
            {
                Console.WriteLine($"Indexes already exist ({indexCount} found), skipping creation.");
            }
            else
            {
                Console.WriteLine($"Creating indexes ({indexCount} found, need more)...");
                using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_parent_metadata ON ParentDocuments USING GIN (Metadata);", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_chunks_parentid ON DocumentChunks(ParentId);", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_chunks_textcontent ON DocumentChunks USING GIN (to_tsvector('english', TextContent));", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_docimages_parentid ON DocumentImages(ParentId);", conn)) cmd.ExecuteNonQuery();
            }

            bool vectorIndexExists = false;
            using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'idx_chunks_embedding_hnsw';", conn))
            {
                vectorIndexExists = ((long)(cmd.ExecuteScalar() ?? 0L)) > 0;
            }

            // 8. Check pgvector version and create vector index if needed
            if (vectorIndexExists)
            {
                Console.WriteLine("Vector index already exists, skipping.");
            }
            else
            {
                Console.WriteLine("Checking pgvector version...");
                string? pgvectorVersion = null;
                try
                {
                    using (var cmd = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector';", conn))
                    {
                        pgvectorVersion = cmd.ExecuteScalar() as string;
                    }
                    Console.WriteLine($"  pgvector version: {pgvectorVersion ?? "not found"}");
                }
                catch { }

                if (pgvectorVersion != null)
                {
                    bool supportsHnsw = false;
                    if (Version.TryParse(pgvectorVersion, out var ver))
                    {
                        supportsHnsw = ver >= new Version(0, 5, 0);
                    }

                    Console.WriteLine("Creating vector index...");
                    try
                    {
                        if (supportsHnsw)
                        {
                            using (var cmd = new NpgsqlCommand(@"
                                CREATE INDEX IF NOT EXISTS idx_chunks_embedding_hnsw ON DocumentChunks
                                USING hnsw (Embedding vector_cosine_ops)
                                WITH (m = 16, ef_construction = 64);", conn))
                            {
                                cmd.CommandTimeout = 300;
                                cmd.ExecuteNonQuery();
                            }
                            Console.WriteLine("  HNSW vector index created.");
                        }
                        else
                        {
                            Console.WriteLine($"  HNSW requires pgvector 0.5+ (you have {pgvectorVersion}). Skipping.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [WARN] Vector index creation failed: {ex.Message}");
                    }
                }
            }

            Console.WriteLine("Database Schema Initialized Successfully.");

            // Verify
            using (var cmd = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_name = 'parentdocuments';", conn))
            {
                var count = (long)(cmd.ExecuteScalar() ?? 0L);
                Console.WriteLine($"Table Verification: {count} (Should be 1)");
            }

            // Show partition info
            using (var cmd = new NpgsqlCommand(@"
                SELECT count(*) FROM pg_inherits
                WHERE inhparent = 'documentchunks'::regclass;", conn))
            {
                var partitions = (long)(cmd.ExecuteScalar() ?? 0L);
                Console.WriteLine($"DocumentChunks partitions: {partitions}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL Error initializing Database: {ex.Message}");
            throw; // Re-throw to stop program
        }
    }

    /// <summary>
    /// Drops all tables to provide a clean slate. 
    /// </summary>
    public void ResetDb()
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            Console.WriteLine("Resetting database - truncating all tables...");

            // Truncate all tables, reset identity sequences
            using (var cmd = new NpgsqlCommand(
                "TRUNCATE DocumentImages, DocumentChunks, ParentDocuments, DataSets, Sources RESTART IDENTITY CASCADE;",
                conn))
            {
                cmd.ExecuteNonQuery();
            }

            Console.WriteLine("All tables truncated. Schema and indexes preserved.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error resetting DB: {ex.Message}");
            throw;
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
                // 1. Upsert Parent Document (store metadata WITHOUT Text - text goes to DocumentChunks)
                string json = JsonSerializer.Serialize(metadata.ToStorageDto());
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

                if (sentences.Count == 0)
                {
                    Console.WriteLine($"  [WARN] No sentences to chunk for {filePath}");
                }

                var currentChunk = new List<string>();
                int currentLength = 0;
                int chunkIndex = 0; // Use a local counter
                int totalChunksCreated = 0;

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
                    totalChunksCreated++;
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
                Console.WriteLine($"Saved to DB: {filePath} with {totalChunksCreated} chunks (from {sentences.Count} sentences).");
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
    /// Upsert full and thumb image records for a document in a single transaction.
    /// Only updates an image if width > 0 and height > 0 (preserves existing if not provided).
    /// </summary>
    public void UpsertDocumentImages(string parentFilePath,
        string fullPath, int fullWidth, int fullHeight,
        string thumbPath, int thumbWidth, int thumbHeight)
    {
        // Skip if nothing to update
        if (fullWidth <= 0 && fullHeight <= 0 && thumbWidth <= 0 && thumbHeight <= 0) return;

        try
        {
            using var conn = _dataSource.OpenConnection();

            // Get parent ID
            int parentId = 0;
            using (var cmd = new NpgsqlCommand("SELECT Id FROM ParentDocuments WHERE FilePath = @fp LIMIT 1;", conn))
            {
                cmd.Parameters.AddWithValue("fp", parentFilePath);
                var result = cmd.ExecuteScalar();
                if (result == null) return;
                parentId = (int)result;
            }

            string upsertSql = @"
                INSERT INTO DocumentImages (ParentId, ImageType, ImageSize, FilePath, Width, Height)
                VALUES (@pid, 'jpg', @size, @path, @w, @h)
                ON CONFLICT (ParentId, ImageSize)
                DO UPDATE SET
                    FilePath = EXCLUDED.FilePath,
                    Width = EXCLUDED.Width,
                    Height = EXCLUDED.Height,
                    CreatedAt = NOW();
            ";

            using var trans = conn.BeginTransaction();
            try
            {
                // Upsert full only if dimensions provided
                if (fullWidth > 0 && fullHeight > 0)
                {
                    using var cmd = new NpgsqlCommand(upsertSql, conn, trans);
                    cmd.Parameters.AddWithValue("pid", parentId);
                    cmd.Parameters.AddWithValue("size", "full");
                    cmd.Parameters.AddWithValue("path", fullPath);
                    cmd.Parameters.AddWithValue("w", fullWidth);
                    cmd.Parameters.AddWithValue("h", fullHeight);
                    cmd.ExecuteNonQuery();
                }

                // Upsert thumb only if dimensions provided
                if (thumbWidth > 0 && thumbHeight > 0)
                {
                    using var cmd = new NpgsqlCommand(upsertSql, conn, trans);
                    cmd.Parameters.AddWithValue("pid", parentId);
                    cmd.Parameters.AddWithValue("size", "thumb");
                    cmd.Parameters.AddWithValue("path", thumbPath);
                    cmd.Parameters.AddWithValue("w", thumbWidth);
                    cmd.Parameters.AddWithValue("h", thumbHeight);
                    cmd.ExecuteNonQuery();
                }

                trans.Commit();
            }
            catch
            {
                trans.Rollback();
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] UpsertDocumentImages failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if a document already exists in the database by file path.
    /// Used for duplicate detection in distributed processing.
    /// </summary>
    public bool DocumentExists(string filePath)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand("SELECT 1 FROM parentdocuments WHERE filepath = @path LIMIT 1", conn);
            cmd.Parameters.AddWithValue("path", filePath);
            var result = cmd.ExecuteScalar();
            return result != null;
        }
        catch
        {
            return false; // On error, allow processing (will fail on insert if duplicate)
        }
    }

    public async Task<List<(string FilePath, string Text, double Distance, DateTime? Date, int PageCount, string? Thumbnail, string? FullImage, string? SourceName, string? DataSetName)>> SearchSimilarAsync(string query, int limit = 20)
    {
        var results = new List<(string, string, double, DateTime?, int, string?, string?, string?, string?)>();

        try
        {
            using var conn = _dataSource.OpenConnection();

            // Try vector similarity search if embedding service available
            if (_embeddingService != null)
            {
                try
                {
                    var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);
                    results = await SearchByVectorAsync(conn, queryEmbedding, limit);
                    if (results.Count > 0) return results;
                }
                catch (Exception embEx)
                {
                    Console.WriteLine($"Vector search failed, falling back to text: {embEx.Message}");
                }
            }

            // Fallback to full-text search
            results = await SearchByTextAsync(conn, query, limit);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Search failed: {ex}");
        }
        return results;
    }

    private async Task<List<(string, string, double, DateTime?, int, string?, string?, string?, string?)>> SearchByVectorAsync(NpgsqlConnection conn, float[] queryEmbedding, int limit)
    {
        var results = new List<(string, string, double, DateTime?, int, string?, string?, string?, string?)>();

        // Vector similarity search using cosine distance
        // Find best matching chunk for ranking, but return ALL chunks combined for display
        string sql = @"
            WITH ranked_chunks AS (
                SELECT
                    c.ParentId,
                    c.Embedding <=> @queryVector AS distance,
                    ROW_NUMBER() OVER (PARTITION BY c.ParentId ORDER BY c.Embedding <=> @queryVector) as rn
                FROM DocumentChunks c
                WHERE c.Embedding IS NOT NULL
                ORDER BY c.Embedding <=> @queryVector
                LIMIT @limit * 3
            ),
            best_matches AS (
                SELECT ParentId, distance
                FROM ranked_chunks
                WHERE rn = 1
            )
            SELECT p.FilePath,
                   (SELECT STRING_AGG(c.TextContent, ' ' ORDER BY c.ChunkIndex) FROM DocumentChunks c WHERE c.ParentId = p.Id) as FullText,
                   bm.distance,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbPath,
                   (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullPath,
                   s.Name as SourceName,
                   d.Name as DataSetName
            FROM best_matches bm
            JOIN ParentDocuments p ON bm.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            ORDER BY bm.distance
            LIMIT @limit;
        ";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("queryVector", new Vector(queryEmbedding));
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
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }

        return results;
    }

    private async Task<List<(string, string, double, DateTime?, int, string?, string?, string?, string?)>> SearchByTextAsync(NpgsqlConnection conn, string query, int limit)
    {
        var results = new List<(string, string, double, DateTime?, int, string?, string?, string?, string?)>();

        // Full-text search using PostgreSQL tsvector
        // Find best matching chunk for ranking, but return ALL chunks combined for display
        string sql = @"
            WITH matching_docs AS (
                SELECT DISTINCT c.ParentId,
                       MAX(ts_rank(to_tsvector('english', c.TextContent), plainto_tsquery('english', @query))) as score
                FROM DocumentChunks c
                WHERE to_tsvector('english', c.TextContent) @@ plainto_tsquery('english', @query)
                   OR c.TextContent ILIKE @pattern
                GROUP BY c.ParentId
                ORDER BY score DESC
                LIMIT @limit
            )
            SELECT p.FilePath,
                   (SELECT STRING_AGG(c.TextContent, ' ' ORDER BY c.ChunkIndex) FROM DocumentChunks c WHERE c.ParentId = p.Id) as FullText,
                   md.score,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbPath,
                   (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullPath,
                   s.Name as SourceName,
                   d.Name as DataSetName
            FROM matching_docs md
            JOIN ParentDocuments p ON md.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            ORDER BY md.score DESC;
        ";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("pattern", $"%{query}%");
        cmd.Parameters.AddWithValue("limit", limit);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? 0.0 : reader.GetDouble(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }

        return results;
    }

    /// <summary>
    /// Exact text match search - only returns documents containing the exact query string
    /// </summary>
    public List<(string FilePath, string Text, double Distance, DateTime? Date, int PageCount, string? Thumbnail, string? FullImage, string? SourceName, string? DataSetName)> SearchExactMatch(string query, int limit = 20)
    {
        var results = new List<(string, string, double, DateTime?, int, string?, string?, string?, string?)>();

        try
        {
            using var conn = _dataSource.OpenConnection();

            // Exact text match - only returns docs where the chunk contains the exact query string
            string sql = @"
                WITH matching_docs AS (
                    SELECT DISTINCT c.ParentId
                    FROM DocumentChunks c
                    WHERE c.TextContent ILIKE @pattern
                    LIMIT @limit
                )
                SELECT p.FilePath,
                       (SELECT STRING_AGG(c.TextContent, ' ' ORDER BY c.ChunkIndex) FROM DocumentChunks c WHERE c.ParentId = p.Id) as FullText,
                       1.0 as score,
                       (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                       (p.Metadata->>'PageCount')::int as PageCount,
                       (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbPath,
                       (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullPath,
                       s.Name as SourceName,
                       d.Name as DataSetName
                FROM matching_docs md
                JOIN ParentDocuments p ON md.ParentId = p.Id
                LEFT JOIN DataSets d ON p.DataSetId = d.Id
                LEFT JOIN Sources s ON d.SourceId = s.Id;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("pattern", $"%{query}%");
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
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exact match search failed: {ex.Message}");
        }

        return results;
    }

    public List<(string FilePath, string Text, double Distance, DateTime? Date, int PageCount, string? Thumbnail, string? FullImage, string? SourceName, string? DataSetName)> GetRecentDocuments(int limit = 10)
    {
        var results = new List<(string, string, double, DateTime?, int, string?, string?, string?, string?)>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                SELECT p.FilePath,
                       (SELECT STRING_AGG(c.TextContent, ' ' ORDER BY c.ChunkIndex) FROM DocumentChunks c WHERE c.ParentId = p.Id) as Text,
                       0.0 as Distance,
                       (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                       (p.Metadata->>'PageCount')::int as PageCount,
                       (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbPath,
                       (SELECT FilePath FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullPath,
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
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)
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
            long docs = (long)(cmdDocs.ExecuteScalar() ?? 0L);

            using var cmdImages = new NpgsqlCommand("SELECT count(*) FROM DocumentImages", conn);
            long images = (long)(cmdImages.ExecuteScalar() ?? 0L);

            using var cmdChunks = new NpgsqlCommand("SELECT count(*) FROM DocumentChunks", conn);
            long chunks = (long)(cmdChunks.ExecuteScalar() ?? 0L);

            return (docs, images, chunks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting counts: {ex.Message}");
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// Get detailed stats grouped by DataSet for admin dashboard.
    /// </summary>
    public List<DataSetStats> GetDataSetStats()
    {
        var results = new List<DataSetStats>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                SELECT
                    COALESCE(s.Name, 'Unknown') as SourceName,
                    COALESCE(d.Name, 'Unassigned') as DataSetName,
                    COUNT(DISTINCT p.Id) as DocumentCount,
                    COALESCE(SUM((p.Metadata->>'PageCount')::int), 0) as TotalPages,
                    COUNT(DISTINCT i.Id) as ImageCount,
                    COUNT(DISTINCT c.Id) as ChunkCount,
                    MIN(p.ProcessedAt) as FirstProcessed,
                    MAX(p.ProcessedAt) as LastProcessed
                FROM ParentDocuments p
                LEFT JOIN DataSets d ON p.DataSetId = d.Id
                LEFT JOIN Sources s ON d.SourceId = s.Id
                LEFT JOIN DocumentImages i ON i.ParentId = p.Id
                LEFT JOIN DocumentChunks c ON c.ParentId = p.Id
                GROUP BY s.Name, d.Name
                ORDER BY s.Name, d.Name;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 120; // Complex aggregation query
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new DataSetStats
                {
                    SourceName = reader.GetString(0),
                    DataSetName = reader.GetString(1),
                    DocumentCount = Convert.ToInt64(reader.GetValue(2)),
                    TotalPages = Convert.ToInt64(reader.GetValue(3)),
                    ImageCount = Convert.ToInt64(reader.GetValue(4)),
                    ChunkCount = Convert.ToInt64(reader.GetValue(5)),
                    FirstProcessed = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                    LastProcessed = reader.IsDBNull(7) ? null : reader.GetDateTime(7)
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting dataset stats: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Get overall system stats for admin dashboard.
    /// </summary>
    public SystemStats GetSystemStats()
    {
        var stats = new SystemStats();
        try
        {
            using var conn = _dataSource.OpenConnection();

            // Basic counts
            var (docs, images, chunks) = GetCounts();
            stats.TotalDocuments = docs;
            stats.TotalImages = images;
            stats.TotalChunks = chunks;

            // Total pages
            using (var cmd = new NpgsqlCommand("SELECT COALESCE(SUM((Metadata->>'PageCount')::int), 0) FROM ParentDocuments;", conn))
            {
                stats.TotalPages = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            // Source count
            using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM Sources;", conn))
            {
                stats.SourceCount = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            // DataSet count
            using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM DataSets;", conn))
            {
                stats.DataSetCount = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            // Documents with embeddings
            using (var cmd = new NpgsqlCommand("SELECT COUNT(DISTINCT ParentId) FROM DocumentChunks WHERE Embedding IS NOT NULL;", conn))
            {
                stats.DocumentsWithEmbeddings = (long)(cmd.ExecuteScalar() ?? 0L);
            }

            // Average pages per document
            if (stats.TotalDocuments > 0)
            {
                stats.AvgPagesPerDocument = (double)stats.TotalPages / stats.TotalDocuments;
            }

            // Last processed
            using (var cmd = new NpgsqlCommand("SELECT MAX(ProcessedAt) FROM ParentDocuments;", conn))
            {
                var result = cmd.ExecuteScalar();
                stats.LastProcessedAt = result == DBNull.Value ? null : (DateTime?)result;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting system stats: {ex.Message}");
        }
        return stats;
    }

public class DataSetStats
{
    public string SourceName { get; set; } = "";
    public string DataSetName { get; set; } = "";
    public long DocumentCount { get; set; }
    public long TotalPages { get; set; }
    public long ImageCount { get; set; }
    public long ChunkCount { get; set; }
    public DateTime? FirstProcessed { get; set; }
    public DateTime? LastProcessed { get; set; }
}

public class SystemStats
{
    public long TotalDocuments { get; set; }
    public long TotalPages { get; set; }
    public long TotalImages { get; set; }
    public long TotalChunks { get; set; }
    public long SourceCount { get; set; }
    public long DataSetCount { get; set; }
    public long DocumentsWithEmbeddings { get; set; }
    public double AvgPagesPerDocument { get; set; }
    public DateTime? LastProcessedAt { get; set; }
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
