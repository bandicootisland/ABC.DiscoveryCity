using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using System.Text.Json;
using System.IO.Compression;
using ABC.DiscoveryCity.Embeddings;
using System.Text;

namespace ABC.DiscoveryCity.PostgreSQL;

public partial class DbService
{
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingService? _embeddingService;
    private static bool _pgvectorMapped = false;
    private static readonly object _mapLock = new();

    /// <summary>
    /// Returns true if running on Windows, false for Linux/Mac.
    /// Used to decide which file path column to read/write.
    /// </summary>
    public static bool IsWindows => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
        System.Runtime.InteropServices.OSPlatform.Windows);

    /// <summary>
    /// Given a full file path, returns just the file name portion for use as a unique key.
    /// </summary>
    public static string ExtractFileName(string filePath) => System.IO.Path.GetFileName(filePath);

    /// <summary>
    /// Creates a new NpgsqlConnection for use by pipeline steps that need direct DB access.
    /// Caller is responsible for opening and disposing the connection.
    /// </summary>
    public NpgsqlConnection CreateConnection() => _dataSource.CreateConnection();

    // Cached basepaths from filesources table (loaded once at startup)
    private static string? _windowsBasePath;
    private static string? _linuxBasePath;

    /// <summary>
    /// Build a full file path for the current OS from folder + filename.
    /// Uses cached basepaths from the filesources table.
    /// </summary>
    public static string? BuildPath(string? folder, string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        var basePath = IsWindows ? _windowsBasePath : _linuxBasePath;
        if (basePath == null) return null;
        var fullPath = basePath + (folder ?? "") + filename;
        return IsWindows ? fullPath.Replace('/', '\\') : fullPath.Replace('\\', '/');
    }

    /// <summary>
    /// Resolve a file path for the current OS. If the path contains a known base path
    /// from the other OS (e.g. Windows path on Linux), translate it.
    /// Falls back to the original path if no translation is possible.
    /// </summary>
    public static string ResolveFilePathForCurrentOs(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // Already valid on current OS?
        if (System.IO.File.Exists(path)) return path;

        // Try to translate: extract the relative portion after the foreign base path
        string? foreignBase = IsWindows ? _linuxBasePath : _windowsBasePath;
        string? localBase = IsWindows ? _windowsBasePath : _linuxBasePath;

        if (foreignBase != null && localBase != null)
        {
            // Normalise separators for comparison
            string normPath = path.Replace('\\', '/');
            string normForeignBase = foreignBase.Replace('\\', '/');

            if (normPath.StartsWith(normForeignBase, StringComparison.OrdinalIgnoreCase))
            {
                string relativePart = normPath.Substring(normForeignBase.Length);
                string resolved = localBase + relativePart;
                resolved = IsWindows ? resolved.Replace('/', '\\') : resolved.Replace('\\', '/');
                return resolved;
            }
        }

        // Last resort: just fix separators for current OS
        return IsWindows ? path.Replace('/', '\\') : path.Replace('\\', '/');
    }

    // Default connection string for convenience, but allows override
    private const string DefaultConnectionString = "Host=192.168.1.114;Port=5435;Database=discoverycity;Username=discovery_user;Password=WL71dM5oM2s36FP6ZrBo";

    // Static shared pool — built once, reused by all DbService instances (prevents connection leak)
    private static NpgsqlDataSource? _sharedDataSource;
    private static string? _sharedConnectionString;
    private static readonly object _dataSourceLock = new();

    public DbService(IEmbeddingService? embeddingService = null, string? connectionString = null)
    {
        _embeddingService = embeddingService;
        _connectionString = connectionString ?? DefaultConnectionString;
        
        EnsurePgvectorMapping();
        _dataSource = GetOrCreateSharedDataSource(_connectionString);
        LoadBasePaths();
    }

    /// <summary>
    /// Returns a shared NpgsqlDataSource for the given connection string.
    /// The pool is built once and reused across all DbService instances,
    /// preventing connection pool leaks when DbService is registered as Scoped in DI.
    /// </summary>
    private static NpgsqlDataSource GetOrCreateSharedDataSource(string connectionString)
    {
        if (_sharedDataSource != null && _sharedConnectionString == connectionString)
            return _sharedDataSource;

        lock (_dataSourceLock)
        {
            if (_sharedDataSource != null && _sharedConnectionString == connectionString)
                return _sharedDataSource;

            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.UseVector();
            _sharedDataSource = builder.Build();
            _sharedConnectionString = connectionString;
            return _sharedDataSource;
        }
    }

    /// <summary>
    /// Load the Windows and Linux basepaths from the filesources table (2 rows).
    /// </summary>
    private void LoadBasePaths()
    {
        if (_windowsBasePath != null && _linuxBasePath != null) return;
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand("SELECT basepath FROM filesources ORDER BY id;", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (path.Contains('\\') || path.StartsWith("S:"))
                    _windowsBasePath = path;
                else
                    _linuxBasePath = path;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not load basepaths from filesources: {ex.Message}");
        }
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

    private static SentenceQuantizer? _sentenceQuantizer;
    private static readonly object _sqLock = new();

    /// <summary>
    /// Lazy-loads the Sentence Quantization (SQ) codebook from the DB package.
    /// Shared static instance to prevent reloading on every Scoped DbService instantiation.
    /// </summary>
    public SentenceQuantizer GetSentenceQuantizer()
    {
        if (_sentenceQuantizer != null) return _sentenceQuantizer;
        lock (_sqLock)
        {
            if (_sentenceQuantizer != null) return _sentenceQuantizer;
            _sentenceQuantizer = new SentenceQuantizer(16, 256, 64);
            try
            {
                var dictStorage = new DictionaryStorageService(_dataSource);
                byte[]? package = dictStorage.LoadDictionaryPackage("sq_codebook");
                if (package != null)
                {
                    using var ms = new MemoryStream(package);
                    using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                    var entry = zip.GetEntry("data/sq_codebook.bin");
                    if (entry != null)
                    {
                        using var s = entry.Open();
                        _sentenceQuantizer.Load(s);
                        Console.WriteLine("[INFO] SQ Codebook loaded into TieredSearch engine.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to load SQ Codebook for Tier 4: {ex.Message}");
            }
            return _sentenceQuantizer;
        }
    }

    /// <summary>
    /// Drops all application tables. Used for schema migration (e.g. int→UUID PKs).
    /// Requires full reprocessing of all documents afterward.
    /// </summary>
    public void DropAllTables()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            DROP TABLE IF EXISTS DocumentImages CASCADE;
            DROP TABLE IF EXISTS DocumentChunks CASCADE;
            DROP TABLE IF EXISTS DocumentChunks_p0 CASCADE;
            DROP TABLE IF EXISTS DocumentChunks_p1 CASCADE;
            DROP TABLE IF EXISTS DocumentChunks_p2 CASCADE;
            DROP TABLE IF EXISTS DocumentChunks_p3 CASCADE;
            DROP TABLE IF EXISTS ParentDocuments CASCADE;
            DROP TABLE IF EXISTS DataSets CASCADE;
            DROP TABLE IF EXISTS Sources CASCADE;
            DROP TABLE IF EXISTS filesources CASCADE;
            DROP TABLE IF EXISTS SentenceSignatures CASCADE;
        ", conn);
        cmd.ExecuteNonQuery();
        Console.WriteLine("All tables dropped successfully.");
    }

    public void InitDb()
    {
        try
        {
            using var conn = _dataSource.OpenConnection();

            Console.WriteLine($"Connected to DB: {_connectionString.Replace("discovery_password", "***")}");

            // 1. Enable Vector and Trigram Extensions
            using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn))
            {
                cmd.ExecuteNonQuery();
            }
            using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pg_trgm;", conn))
            {
                cmd.ExecuteNonQuery();
            }

            // Check if schema already exists — skip all CREATE TABLE if so
            bool schemaExists = false;
            using (var cmd = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM information_schema.tables WHERE table_name = 'sources');", conn))
            {
                schemaExists = (bool)(cmd.ExecuteScalar() ?? false);
            }

            if (schemaExists)
            {
                Console.WriteLine("Database schema already exists. Skipping table creation.");

                // Just verify and report
                using (var cmd = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_name = 'parentdocuments';", conn))
                {
                    var count = (long)(cmd.ExecuteScalar() ?? 0L);
                    Console.WriteLine($"Table Verification: {count} (Should be 1)");
                }
                using (var cmd = new NpgsqlCommand(@"SELECT count(*) FROM pg_inherits WHERE inhparent = 'documentchunks'::regclass;", conn))
                {
                    var partitions = (long)(cmd.ExecuteScalar() ?? 0L);
                    Console.WriteLine($"DocumentChunks partitions: {partitions}");
                }
                Console.WriteLine("Database Schema Verified Successfully.");

                // --- MIGRATION: Ensure filesources + dataset folder schema ---
                Console.WriteLine("Applying schema migrations (if needed)...");

                // Create filesources table
                using (var cmd = new NpgsqlCommand(@"
                    CREATE TABLE IF NOT EXISTS filesources (
                        id SERIAL PRIMARY KEY,
                        basepath TEXT NOT NULL UNIQUE,
                        createdat TIMESTAMPTZ DEFAULT NOW()
                    );", conn)) cmd.ExecuteNonQuery();

                // Add folder columns to datasets
                using (var cmd = new NpgsqlCommand("ALTER TABLE DataSets ADD COLUMN IF NOT EXISTS pdffolder TEXT;", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new NpgsqlCommand("ALTER TABLE DataSets ADD COLUMN IF NOT EXISTS imagefolder TEXT;", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new NpgsqlCommand("ALTER TABLE DataSets ADD COLUMN IF NOT EXISTS m4folder TEXT;", conn)) cmd.ExecuteNonQuery();

                // Ensure FileName column on ParentDocuments
                using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ADD COLUMN IF NOT EXISTS FileName TEXT;", conn)) cmd.ExecuteNonQuery();
                using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ALTER COLUMN FileName SET NOT NULL;", conn))
                    try { cmd.ExecuteNonQuery(); } catch { /* already set */ }

                // Ensure FileName column on DocumentImages
                using (var cmd = new NpgsqlCommand("ALTER TABLE DocumentImages ADD COLUMN IF NOT EXISTS filename TEXT;", conn)) cmd.ExecuteNonQuery();

                // Ensure unique constraint
                using (var cmd = new NpgsqlCommand(@"
                    DO $$ BEGIN
                        IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'parentdocuments_datasetid_filename_key') THEN
                            ALTER TABLE ParentDocuments ADD CONSTRAINT parentdocuments_datasetid_filename_key UNIQUE (DataSetId, FileName);
                        END IF;
                    END $$;", conn)) cmd.ExecuteNonQuery();

                // Make BaseFilePath nullable on Sources (legacy)
                using (var cmd = new NpgsqlCommand("ALTER TABLE Sources ALTER COLUMN BaseFilePath DROP NOT NULL;", conn))
                    try { cmd.ExecuteNonQuery(); } catch { /* already nullable */ }

                // Ensure SentenceIds column on ParentDocuments (UUIDv8)
                using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ADD COLUMN IF NOT EXISTS SentenceIds JSONB;", conn)) cmd.ExecuteNonQuery();

                // Ensure dictionary_packages table exists (for word splitting)
                using (var cmd = new NpgsqlCommand(@"
                    CREATE TABLE IF NOT EXISTS dictionary_packages (
                        id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                        dictionary_name TEXT NOT NULL UNIQUE,
                        version TEXT,
                        entry_count INTEGER,
                        package_data BYTEA NOT NULL,
                        source_format TEXT DEFAULT 'xlsx',
                        created_at TIMESTAMPTZ DEFAULT NOW(),
                        updated_at TIMESTAMPTZ DEFAULT NOW()
                    );", conn)) cmd.ExecuteNonQuery();

                // Ensure SentenceSignatures table exists
                using (var cmd = new NpgsqlCommand(@"
                    CREATE TABLE IF NOT EXISTS SentenceSignatures (
                        SentenceId UUID PRIMARY KEY,
                        ParentId UUID REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
                        SemanticId UUID,
                        ""Who"" int2,
                        ""What"" int2,
                        ""Where"" int2,
                        ""When"" int2,
                        ""Which"" int2,
                        ""Why"" int2,
                        ""How"" int2,
                        ""Ordinal"" INT
                    );
                    CREATE INDEX IF NOT EXISTS idx_signatures_parent ON SentenceSignatures(ParentId);
                ", conn)) cmd.ExecuteNonQuery();

                Console.WriteLine("Schema migrations complete.");
                return;
            }

            // 2. Create Sources Table (top-level organization)
            Console.WriteLine("Creating Sources table...");
            string createSourcesTableSql = @"
                CREATE TABLE IF NOT EXISTS Sources (
                    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    Name TEXT NOT NULL UNIQUE,
                    Url TEXT,
                    BaseFilePath TEXT,
                    CreatedAt TIMESTAMPTZ DEFAULT NOW()
                );
            ";
            using (var cmd = new NpgsqlCommand(createSourcesTableSql, conn)) cmd.ExecuteNonQuery();

            // 1b. Create FileSources Table (root basepaths — 1 per OS)
            Console.WriteLine("Creating FileSources table...");
            using (var cmd = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS filesources (
                    id SERIAL PRIMARY KEY,
                    basepath TEXT NOT NULL UNIQUE,
                    createdat TIMESTAMPTZ DEFAULT NOW()
                );", conn)) cmd.ExecuteNonQuery();

            // 3. Create DataSets Table (child of Sources)
            Console.WriteLine("Creating DataSets table...");
            string createDataSetsTableSql = @"
                CREATE TABLE IF NOT EXISTS DataSets (
                    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    SourceId UUID REFERENCES Sources(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL,
                    PdfFolder TEXT,
                    ImageFolder TEXT,
                    M4Folder TEXT,
                    CreatedAt TIMESTAMPTZ DEFAULT NOW(),
                    UNIQUE(SourceId, Name)
                );
            ";
            using (var cmd = new NpgsqlCommand(createDataSetsTableSql, conn)) cmd.ExecuteNonQuery();

            // 4. Create Parent Documents Table (child of DataSets)
            //    Unique key is (DataSetId, FileName) — OS-independent.
            //    Separate columns for Windows and Linux file paths.
            Console.WriteLine("Creating ParentDocuments table...");
            string createParentTableSql = @"
                CREATE TABLE IF NOT EXISTS ParentDocuments (
                    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    DataSetId UUID REFERENCES DataSets(Id),
                    FileName TEXT NOT NULL,
                    FilePath TEXT,
                    Metadata JSONB,
                    CreatedAt TIMESTAMPTZ DEFAULT NOW(),
                    ProcessedAt TIMESTAMPTZ DEFAULT NOW(),
                    UNIQUE(DataSetId, FileName)
                );
            ";
            using (var cmd = new NpgsqlCommand(createParentTableSql, conn)) cmd.ExecuteNonQuery();

            // Add DataSetId column if table already exists without it
            using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ADD COLUMN IF NOT EXISTS DataSetId UUID REFERENCES DataSets(Id);", conn)) cmd.ExecuteNonQuery();

            // Option B: Add Sentences JSONB + Embedding columns to ParentDocuments
            using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ADD COLUMN IF NOT EXISTS Sentences JSONB;", conn)) cmd.ExecuteNonQuery();
            using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ADD COLUMN IF NOT EXISTS SentenceIds JSONB;", conn)) cmd.ExecuteNonQuery();
            using (var cmd = new NpgsqlCommand("ALTER TABLE ParentDocuments ADD COLUMN IF NOT EXISTS Embedding vector(1024);", conn)) cmd.ExecuteNonQuery();

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
                        Id UUID DEFAULT gen_random_uuid(),
                        ParentId UUID NOT NULL,
                        ChunkIndex INT,
                        TextContent TEXT,
                        Embedding vector(1024),
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
            //    Separate columns for Windows and Linux image file paths.
            Console.WriteLine("Creating DocumentImages table...");
            string createImagesTableSql = @"
                CREATE TABLE IF NOT EXISTS DocumentImages (
                    Id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    ParentId UUID REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
                    ImageType TEXT NOT NULL,
                    ImageSize TEXT NOT NULL,
                    FilePath TEXT,
                    FileName TEXT,
                    Width INT,
                    Height INT,
                    ImageData BYTEA,
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

            // 7. Create Dictionary Packages Table (for word splitting)
            Console.WriteLine("Creating dictionary_packages table...");
            using (var cmd = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS dictionary_packages (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    dictionary_name TEXT NOT NULL UNIQUE,
                    version TEXT,
                    entry_count INTEGER,
                    package_data BYTEA NOT NULL,
                    source_format TEXT DEFAULT 'xlsx',
                    created_at TIMESTAMPTZ DEFAULT NOW(),
                    updated_at TIMESTAMPTZ DEFAULT NOW()
                );", conn)) cmd.ExecuteNonQuery();

            // 7b. Create SentenceSignatures Table
            Console.WriteLine("Creating SentenceSignatures table...");
            using (var cmd = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS SentenceSignatures (
                    SentenceId UUID PRIMARY KEY,
                    ParentId UUID REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
                    SemanticId UUID,
                    ""Who"" int2,
                    ""What"" int2,
                    ""Where"" int2,
                    ""When"" int2,
                    ""Which"" int2,
                    ""Why"" int2,
                    ""How"" int2,
                    ""Ordinal"" INT
                );
                CREATE INDEX IF NOT EXISTS idx_signatures_parent ON SentenceSignatures(ParentId);
            ", conn)) cmd.ExecuteNonQuery();

            // 8. Create Indexes (skip if already exist)
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
                using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_parentdocs_processedat ON ParentDocuments (ProcessedAt DESC);", conn)) cmd.ExecuteNonQuery();
            }

            // Option B: Sentences full-text search index (on ParentDocuments.Sentences JSONB)
            try
            {
                using (var cmd = new NpgsqlCommand(@"
                    CREATE INDEX IF NOT EXISTS idx_parent_sentences_fts ON ParentDocuments 
                    USING GIN (jsonb_to_tsvector('english', COALESCE(Sentences, '[]'::jsonb), '[""string""]'));", conn))
                {
                    cmd.CommandTimeout = 300;
                    cmd.ExecuteNonQuery();
                }
                Console.WriteLine("  Sentences full-text search index created.");
                using (var cmd = new NpgsqlCommand(@"
                    CREATE INDEX IF NOT EXISTS idx_chunks_text_trgm ON DocumentChunks USING GIN (TextContent gin_trgm_ops);", conn))
                {
                    cmd.CommandTimeout = 300;
                    cmd.ExecuteNonQuery();
                }
                Console.WriteLine("  Chunk text trigram index created.");

                using (var cmd = new NpgsqlCommand(@"
                    CREATE INDEX IF NOT EXISTS idx_parent_title_trgm ON ParentDocuments USING GIN ((Metadata->>'Title') gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS idx_parent_names_trgm ON ParentDocuments USING GIN ((Metadata->>'Names') gin_trgm_ops);
                    CREATE INDEX IF NOT EXISTS idx_parent_filename_trgm ON ParentDocuments USING GIN (FileName gin_trgm_ops);", conn))
                {
                    cmd.CommandTimeout = 300;
                    cmd.ExecuteNonQuery();
                }
                Console.WriteLine("  Metadata trigram indexes created.");
            }
            catch (Exception ex) { Console.WriteLine($"  [WARN] Sentences FTS index: {ex.Message}"); }

            bool vectorIndexExists = false;
            using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'idx_chunks_embedding_hnsw';", conn))
            {
                vectorIndexExists = ((long)(cmd.ExecuteScalar() ?? 0L)) > 0;
            }

            // Option B: Check for parent document embedding index too
            bool parentVectorIndexExists = false;
            using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM pg_indexes WHERE indexname = 'idx_parent_embedding_hnsw';", conn))
            {
                parentVectorIndexExists = ((long)(cmd.ExecuteScalar() ?? 0L)) > 0;
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
                                WITH (m = 24, ef_construction = 128);", conn))
                            {
                                cmd.CommandTimeout = 300;
                                cmd.ExecuteNonQuery();
                            }
                            Console.WriteLine("  HNSW vector index created (optimized for 1M+ chunks).");
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

            // Option B: HNSW vector index on ParentDocuments.Embedding
            if (parentVectorIndexExists)
            {
                Console.WriteLine("Parent document vector index already exists, skipping.");
            }
            else
            {
                try
                {
                    using (var cmd = new NpgsqlCommand(@"
                        CREATE INDEX IF NOT EXISTS idx_parent_embedding_hnsw ON ParentDocuments
                        USING hnsw (Embedding vector_cosine_ops)
                        WITH (m = 16, ef_construction = 64);", conn))
                    {
                        cmd.CommandTimeout = 300;
                        cmd.ExecuteNonQuery();
                    }
                    Console.WriteLine("  Parent document HNSW vector index created.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [WARN] Parent vector index creation failed: {ex.Message}");
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
                "TRUNCATE DocumentImages, ParentDocuments, DataSets, Sources RESTART IDENTITY CASCADE;",
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
    /// Stores OS-specific base path in the appropriate column.
    /// </summary>
    public Guid GetOrCreateSource(string name, string baseFilePath, string? url = null)
    {
        using var conn = _dataSource.OpenConnection();
        string sql = @"
            INSERT INTO Sources (Id, Name, BaseFilePath, Url)
            VALUES (@id, @name, @basePath, @url)
            ON CONFLICT (Name) DO UPDATE SET
                BaseFilePath = COALESCE(EXCLUDED.BaseFilePath, Sources.BaseFilePath)
            RETURNING Id;
        ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("basePath", (object?)baseFilePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("url", (object?)url ?? DBNull.Value);
        return (Guid)(cmd.ExecuteScalar() ?? Guid.Empty);
    }

    /// <summary>
    /// Get or create a DataSet by name for a given Source. Returns the DataSet Id.
    /// </summary>
    public Guid GetOrCreateDataSet(Guid sourceId, string name, string? pdfFolder = null)
    {
        using var conn = _dataSource.OpenConnection();
        string sql = @"
            INSERT INTO DataSets (Id, SourceId, Name, PdfFolder, ImageFolder)
            VALUES (@id, @sourceId, @name, @pdfFolder, @pdfFolder)
            ON CONFLICT (SourceId, Name) DO UPDATE SET
                PdfFolder = COALESCE(EXCLUDED.PdfFolder, DataSets.PdfFolder),
                ImageFolder = COALESCE(EXCLUDED.ImageFolder, DataSets.ImageFolder)
            RETURNING Id;
        ";
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("sourceId", sourceId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("pdfFolder", (object?)pdfFolder ?? DBNull.Value);
        return (Guid)(cmd.ExecuteScalar() ?? Guid.Empty);
    }

    /// <summary>
    /// Check if a document already exists in the DB by FileName (and optionally DataSetId).
    /// Falls back to legacy FilePath check for compatibility.
    /// Returns the ParentDocument Id if found, or null if not.
    /// </summary>
    public Guid? DocumentExists(string filePath, Guid? dataSetId = null)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            string fileName = ExtractFileName(filePath);

            // Primary lookup: by DataSetId + FileName (the new unique key)
            if (dataSetId.HasValue)
            {
                using var cmd = new NpgsqlCommand(
                    "SELECT Id FROM ParentDocuments WHERE DataSetId = @dsId AND FileName = @fn;", conn);
                cmd.Parameters.AddWithValue("dsId", dataSetId.Value);
                cmd.Parameters.AddWithValue("fn", fileName);
                var result = cmd.ExecuteScalar();
                if (result != null) return (Guid)result;
            }

            // Fallback: by FileName alone (may return first match)
            using (var cmd = new NpgsqlCommand(
                "SELECT Id FROM ParentDocuments WHERE FileName = @fn LIMIT 1;", conn))
            {
                cmd.Parameters.AddWithValue("fn", fileName);
                var result = cmd.ExecuteScalar();
                if (result != null) return (Guid)result;
            }

            // Legacy fallback: exact FilePath match
            using (var cmd = new NpgsqlCommand(
                "SELECT Id FROM ParentDocuments WHERE FilePath = @fp;", conn))
            {
                cmd.Parameters.AddWithValue("fp", filePath);
                var result = cmd.ExecuteScalar();
                return result != null ? (Guid)result : null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking document exists: {ex.Message}");
            return null;
        }
    }

    public Guid InsertDocument(string filePath, PdfMetadata metadata, Guid? dataSetId = null, List<Guid>? sentenceIds = null)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var trans = conn.BeginTransaction();

            try
            {
                string json = JsonSerializer.Serialize(metadata.ToStorageDto());
                string fileName = ExtractFileName(filePath);
                Guid parentId = Guid.Empty;
                bool isUpdate = false;

                // 1. Check for existing document
                if (dataSetId.HasValue)
                {
                    using (var checkCmd = new NpgsqlCommand(
                        "SELECT Id FROM ParentDocuments WHERE DataSetId = @dsId AND FileName = @fn;", conn, trans))
                    {
                        checkCmd.Parameters.AddWithValue("dsId", dataSetId.Value);
                        checkCmd.Parameters.AddWithValue("fn", fileName);
                        var existing = checkCmd.ExecuteScalar();
                        if (existing != null) { parentId = (Guid)existing; isUpdate = true; }
                    }
                }

                if (!isUpdate)
                {
                    using (var checkCmd = new NpgsqlCommand("SELECT Id FROM ParentDocuments WHERE FileName = @fn LIMIT 1;", conn, trans))
                    {
                        checkCmd.Parameters.AddWithValue("fn", fileName);
                        var existing = checkCmd.ExecuteScalar();
                        if (existing != null) { parentId = (Guid)existing; isUpdate = true; }
                    }
                }

                var sentences = metadata.Text ?? new List<string>();
                var cleanSentences = sentences.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                string sentencesJson = JsonSerializer.Serialize(cleanSentences);
                string? sentenceIdsJson = sentenceIds != null && sentenceIds.Count > 0
                    ? JsonSerializer.Serialize(sentenceIds) : null;

                if (isUpdate)
                {
                    // 2a. UPDATE Parent
                    using (var cmd = new NpgsqlCommand(@"
                        UPDATE ParentDocuments SET Metadata = @meta::jsonb, DataSetId = COALESCE(@dataSetId, DataSetId),
                        FilePath = @fp, Sentences = @sentences::jsonb, SentenceIds = @sids::jsonb, ProcessedAt = NOW()
                        WHERE Id = @id;", conn, trans))
                    {
                        cmd.Parameters.AddWithValue("id", parentId);
                        cmd.Parameters.AddWithValue("meta", json);
                        cmd.Parameters.AddWithValue("fp", filePath);
                        cmd.Parameters.AddWithValue("sentences", sentencesJson);
                        cmd.Parameters.AddWithValue("sids", (object?)sentenceIdsJson ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("dataSetId", (object?)dataSetId ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    // Delete old chunks
                    using (var delCmd = new NpgsqlCommand("DELETE FROM DocumentChunks WHERE ParentId = @id;", conn, trans))
                    {
                         delCmd.Parameters.AddWithValue("id", parentId);
                         delCmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    // 2b. INSERT Parent
                    parentId = Guid.CreateVersion7();
                    using (var cmd = new NpgsqlCommand(@"
                        INSERT INTO ParentDocuments (Id, FileName, FilePath, Metadata, DataSetId, Sentences, SentenceIds, ProcessedAt)
                        VALUES (@id, @fn, @fp, @meta::jsonb, @dataSetId, @sentences::jsonb, @sids::jsonb, NOW());", conn, trans))
                    {
                        cmd.Parameters.AddWithValue("id", parentId);
                        cmd.Parameters.AddWithValue("fn", fileName);
                        cmd.Parameters.AddWithValue("fp", filePath);
                        cmd.Parameters.AddWithValue("meta", json);
                        cmd.Parameters.AddWithValue("sentences", sentencesJson);
                        cmd.Parameters.AddWithValue("sids", (object?)sentenceIdsJson ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("dataSetId", (object?)dataSetId ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 3. Populate Chunks (Option A)
                if (cleanSentences.Count > 0)
                {
                    var chunks = GroupSentencesIntoChunks(cleanSentences, targetLength: 2000);
                    int chunkIdx = 0;
                    foreach (var chunkText in chunks)
                    {
                        float[]? chunkEmbedding = null;
                        if (_embeddingService != null)
                        {
                            chunkEmbedding = GetEmbeddingWithRetry(chunkText, maxRetries: 3);
                        }

                        using (var cmd = new NpgsqlCommand(@"
                            INSERT INTO DocumentChunks (ParentId, ChunkIndex, TextContent, Embedding)
                            VALUES (@pid, @idx, @txt, @emb);", conn, trans))
                        {
                            cmd.Parameters.AddWithValue("pid", parentId);
                            cmd.Parameters.AddWithValue("idx", chunkIdx++);
                            cmd.Parameters.AddWithValue("txt", chunkText);
                            cmd.Parameters.AddWithValue("emb", chunkEmbedding != null ? new Vector(chunkEmbedding) : DBNull.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                trans.Commit();
                Console.WriteLine($"{(isUpdate ? "Updated" : "Saved")} in DB: {fileName} with {cleanSentences.Count} sentences across {Math.Max(1, cleanSentences.Count/5)} chunks.");
                return parentId;
            }
            catch (Exception ex)
            {
                trans.Rollback();
                Console.WriteLine($"Error saving to DB for {filePath}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Connection Error saving to DB for {filePath}: {ex.Message}");
        }
        return Guid.Empty;
    }

    public string GetSearchHash(string? query, bool exactMatch, List<string>? datasets, List<string>? names, bool filenameOnly = false, bool useSemantic = true)
    {
        var hashBuilder = new global::System.Text.StringBuilder();
        hashBuilder.Append($"Q:{query?.ToLowerInvariant()}");
        hashBuilder.Append($"|E:{exactMatch}");
        hashBuilder.Append($"|FN:{filenameOnly}");
        hashBuilder.Append($"|S:{useSemantic}");
        return hashBuilder.ToString();
    }

    private List<string> GroupSentencesIntoChunks(List<string> sentences, int targetLength)
    {
        var chunks = new List<string>();
        var currentChunk = new System.Text.StringBuilder();
        foreach (var s in sentences)
        {
            if (currentChunk.Length + s.Length > targetLength && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
            }
            currentChunk.Append(s).Append(" ");
        }
        if (currentChunk.Length > 0) chunks.Add(currentChunk.ToString().Trim());
        return chunks;
    }

    /// <summary>
    /// Gets an embedding with exponential backoff retry.
    /// Falls back to truncated text if the full chunk exceeds the model's token limit.
    /// </summary>
    private float[]? GetEmbeddingWithRetry(string text, int maxRetries = 3)
    {
        if (_embeddingService == null) return null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                return _embeddingService.GetEmbeddingAsync(text).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                int delayMs = 500 * (1 << attempt); // 500ms, 1s, 2s
                Console.WriteLine($"  [Embedding] Retry {attempt + 1}/{maxRetries} in {delayMs}ms: {ex.Message.Split('\n')[0]}");
                Thread.Sleep(delayMs);

                // On second retry, try truncating — model may have a token limit
                if (attempt == 1 && text.Length > 1500)
                {
                    text = text[..1500];
                    Console.WriteLine($"  [Embedding] Truncated to {text.Length} chars for retry");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [Embedding] Failed after {maxRetries} retries: {ex.Message.Split('\n')[0]}");
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Upsert full and thumb image records for a document in a single transaction.
    /// Looks up by FileName (OS-independent key). Stores paths in OS-specific columns.
    /// Only updates an image if width > 0 and height > 0 (preserves existing if not provided).
    /// </summary>
    public void UpsertDocumentImages(string parentFilePath,
        string fullPath, int fullWidth, int fullHeight,
        string thumbPath, int thumbWidth, int thumbHeight,
        byte[]? previewData = null, byte[]? thumbData = null)
    {
        // Skip if nothing to update
        if (fullWidth <= 0 && fullHeight <= 0 && thumbWidth <= 0 && thumbHeight <= 0) return;

        try
        {
            using var conn = _dataSource.OpenConnection();

            // Look up parent by FileName (OS-independent)
            string fileName = ExtractFileName(parentFilePath);
            var parentIds = new List<Guid>();
            using (var cmd = new NpgsqlCommand(
                "SELECT Id FROM ParentDocuments WHERE FileName = @fn;", conn))
            {
                cmd.Parameters.AddWithValue("fn", fileName);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    parentIds.Add(reader.GetGuid(0));
            }
            if (parentIds.Count == 0) return;

            string upsertSql = @"
                INSERT INTO DocumentImages (ParentId, ImageType, ImageSize, FilePath, FileName, Width, Height, ImageData)
                VALUES (@pid, 'jpg', @size, @path, @fname, @w, @h, @data)
                ON CONFLICT (ParentId, ImageSize)
                DO UPDATE SET
                    FilePath = COALESCE(EXCLUDED.FilePath, DocumentImages.FilePath),
                    FileName = COALESCE(EXCLUDED.FileName, DocumentImages.FileName),
                    Width = EXCLUDED.Width,
                    Height = EXCLUDED.Height,
                    ImageData = COALESCE(EXCLUDED.ImageData, DocumentImages.ImageData),
                    CreatedAt = NOW();
            ";

            using var trans = conn.BeginTransaction();
            try
            {
                foreach (var pid in parentIds)
                {
                    // Upsert full/preview only if dimensions provided
                    if (fullWidth > 0 && fullHeight > 0)
                    {
                        using var cmd = new NpgsqlCommand(upsertSql, conn, trans);
                        cmd.Parameters.AddWithValue("pid", pid);
                        cmd.Parameters.AddWithValue("size", "full");
                        cmd.Parameters.AddWithValue("path", fullPath);
                        cmd.Parameters.AddWithValue("fname", ExtractFileName(fullPath));
                        cmd.Parameters.AddWithValue("w", fullWidth);
                        cmd.Parameters.AddWithValue("h", fullHeight);
                        cmd.Parameters.AddWithValue("data", previewData is { Length: > 0 } ? (object)previewData : DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }

                    // Upsert thumb only if dimensions provided
                    if (thumbWidth > 0 && thumbHeight > 0)
                    {
                        using var cmd = new NpgsqlCommand(upsertSql, conn, trans);
                        cmd.Parameters.AddWithValue("pid", pid);
                        cmd.Parameters.AddWithValue("size", "thumb");
                        cmd.Parameters.AddWithValue("path", thumbPath);
                        cmd.Parameters.AddWithValue("fname", ExtractFileName(thumbPath));
                        cmd.Parameters.AddWithValue("w", thumbWidth);
                        cmd.Parameters.AddWithValue("h", thumbHeight);
                        cmd.Parameters.AddWithValue("data", thumbData is { Length: > 0 } ? (object)thumbData : DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
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

    // ── New relational tables (BookCity pattern) ──────────────────────────

    /// <summary>
    /// Store XLSX bundle in document_packages (1:1 with parentdocuments).
    /// </summary>
    public void UpsertDocumentPackage(Guid documentId, byte[] packageData)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO document_packages (document_id, package_data, created_at)
                VALUES (@id, @data, NOW())
                ON CONFLICT (document_id) DO UPDATE SET package_data = EXCLUDED.package_data, created_at = NOW();", conn);
            cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("data", packageData);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Console.WriteLine($"  [WARN] UpsertDocumentPackage failed: {ex.Message}"); }
    }

    /// <summary>
    /// Retrieve XLSX bundle bytes from document_packages.
    /// </summary>
    public byte[]? GetDocumentPackage(Guid documentId)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(
                "SELECT package_data FROM document_packages WHERE document_id = @id;", conn);
            cmd.Parameters.AddWithValue("id", documentId);
            var result = cmd.ExecuteScalar();
            return result as byte[];
        }
        catch { return null; }
    }

    /// <summary>
    /// Retrieve XLSX bundle bytes by filename lookup.
    /// </summary>
    public byte[]? GetDocumentPackageByFileName(string fileName)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT dp.package_data FROM document_packages dp
                JOIN parentdocuments pd ON pd.id = dp.document_id
                WHERE pd.filename = @fn LIMIT 1;", conn);
            cmd.Parameters.AddWithValue("fn", fileName);
            var result = cmd.ExecuteScalar();
            return result as byte[];
        }
        catch { return null; }
    }

    /// <summary>
    /// Upsert preview/thumb images into document_previews (like book_previews).
    /// </summary>
    public void UpsertDocumentPreview(Guid documentId, string imageSize,
        int width, int height, byte[]? imageData, string contentType = "image/jpeg", string? renderMethod = null)
    {
        if (width <= 0 || height <= 0) return;
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO document_previews (document_id, image_size, width, height, content_type, image_data, render_method, created_at)
                VALUES (@id, @size, @w, @h, @ct, @data, @rm, NOW())
                ON CONFLICT (document_id, image_size) DO UPDATE SET
                    width = EXCLUDED.width, height = EXCLUDED.height,
                    content_type = EXCLUDED.content_type, image_data = EXCLUDED.image_data,
                    render_method = EXCLUDED.render_method, created_at = NOW();", conn);
            cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("size", imageSize);
            cmd.Parameters.AddWithValue("w", width);
            cmd.Parameters.AddWithValue("h", height);
            cmd.Parameters.AddWithValue("ct", contentType);
            cmd.Parameters.AddWithValue("data", imageData is { Length: > 0 } ? (object)imageData : DBNull.Value);
            cmd.Parameters.AddWithValue("rm", (object?)renderMethod ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Console.WriteLine($"  [WARN] UpsertDocumentPreview failed: {ex.Message}"); }
    }

    /// <summary>
    /// Store sentences into document_sentences (proper relational, like book_sentences).
    /// Deletes existing sentences first, then bulk inserts.
    /// </summary>
    public void StoreDocumentSentences(Guid documentId, List<string> sentences, List<Guid> sentenceIds, int pageCount = 1)
    {
        if (sentences.Count == 0) return;
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var trans = conn.BeginTransaction();

            // Delete existing sentences (cascade deletes vectors too)
            using (var del = new NpgsqlCommand("DELETE FROM document_sentences WHERE document_id = @id;", conn, trans))
            {
                del.Parameters.AddWithValue("id", documentId);
                del.ExecuteNonQuery();
            }

            // Bulk insert sentences
            bool hasIds = sentenceIds.Count == sentences.Count;
            int sentencesPerPage = pageCount > 0 ? Math.Max(1, sentences.Count / pageCount) : sentences.Count;

            for (int i = 0; i < sentences.Count; i++)
            {
                var text = sentences[i];
                if (string.IsNullOrWhiteSpace(text)) continue;

                int pageNum = pageCount > 0 ? Math.Min(i / sentencesPerPage + 1, pageCount) : 1;
                int wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                var sentenceId = hasIds ? sentenceIds[i] : Guid.CreateVersion7();

                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO document_sentences (document_id, ordinal, sentence_id, page_number, text, word_count)
                    VALUES (@did, @ord, @sid, @pg, @txt, @wc);", conn, trans);
                cmd.Parameters.AddWithValue("did", documentId);
                cmd.Parameters.AddWithValue("ord", i + 1);
                cmd.Parameters.AddWithValue("sid", sentenceId);
                cmd.Parameters.AddWithValue("pg", pageNum);
                cmd.Parameters.AddWithValue("txt", text);
                cmd.Parameters.AddWithValue("wc", (short)wordCount);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch (Exception ex) { Console.WriteLine($"  [WARN] StoreDocumentSentences failed: {ex.Message}"); }
    }

    /// <summary>
    /// Store page-level data into document_pages.
    /// </summary>
    public void StoreDocumentPages(Guid documentId, int pageCount, string? html = null,
        float pageWidth = 0, float pageHeight = 0, List<string>? sentences = null)
    {
        if (pageCount <= 0) return;
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var trans = conn.BeginTransaction();

            using (var del = new NpgsqlCommand("DELETE FROM document_pages WHERE document_id = @id;", conn, trans))
            {
                del.Parameters.AddWithValue("id", documentId);
                del.ExecuteNonQuery();
            }

            int totalSentences = sentences?.Count ?? 0;
            int sentencesPerPage = totalSentences > 0 ? Math.Max(1, totalSentences / pageCount) : 0;

            for (int p = 1; p <= pageCount; p++)
            {
                int sentStart = totalSentences > 0 ? (p - 1) * sentencesPerPage + 1 : 0;
                int sentEnd = totalSentences > 0 ? Math.Min(p * sentencesPerPage, totalSentences) : 0;
                string? pageHtml = p == 1 ? html : null;

                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO document_pages (document_id, page_number, html, page_width, page_height, sentence_start, sentence_end)
                    VALUES (@id, @pg, @html, @pw, @ph, @ss, @se);", conn, trans);
                cmd.Parameters.AddWithValue("id", documentId);
                cmd.Parameters.AddWithValue("pg", p);
                cmd.Parameters.AddWithValue("html", (object?)pageHtml ?? DBNull.Value);
                cmd.Parameters.AddWithValue("pw", pageWidth);
                cmd.Parameters.AddWithValue("ph", pageHeight);
                cmd.Parameters.AddWithValue("ss", sentStart);
                cmd.Parameters.AddWithValue("se", sentEnd);
                cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch (Exception ex) { Console.WriteLine($"  [WARN] StoreDocumentPages failed: {ex.Message}"); }
    }

    // ── End new relational tables ───────────────────────────────────────

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

    public async Task<List<DocumentSearchResult>> SearchSimilarAsync(string query, int limit = 20, List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();

            // 1. Vector Search
            var vectorResults = new List<DocumentSearchResult>();
            if (_embeddingService != null)
            {
                try
                {
                    var queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);
                    vectorResults = await SearchByVectorAsync(conn, queryEmbedding, Math.Max(limit, 100), datasetNames, nameValues);
                }
                catch (Exception embEx) { Console.WriteLine($"Vector search failed (hybrid): {embEx.Message}"); }
            }

            // 2. Text Search
            var textResults = await SearchByTextAsync(conn, query, Math.Max(limit, 100), datasetNames, nameValues);

            // 3. Combine with RRF
            var combined = CombineResultsRRF(vectorResults, textResults, limit);
            return combined;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Hybrid search failed: {ex}");
            return new List<DocumentSearchResult>();
        }
    }

    private List<DocumentSearchResult> CombineResultsRRF(List<DocumentSearchResult> vectorResults, List<DocumentSearchResult> textResults, int limit)
    {
        var rrfScores = new Dictionary<string, (DocumentSearchResult Item, double Score)>();
        const double k = 60.0;

        // Rank-based scoring from vector search
        for (int i = 0; i < vectorResults.Count; i++)
        {
            var item = vectorResults[i];
            string key = item.FilePath ?? item.FileName;
            rrfScores[key] = (item, 1.0 / (k + i + 1));
        }

        // Add scores from text search
        for (int i = 0; i < textResults.Count; i++)
        {
            var item = textResults[i];
            string key = item.FilePath ?? item.FileName;
            if (rrfScores.TryGetValue(key, out var existing))
            {
                var score = existing.Score + (1.0 / (k + i + 1));
                rrfScores[key] = (item, score); 
            }
            else
            {
                rrfScores[key] = (item, 1.0 / (k + i + 1));
            }
        }

        return rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(limit > 0 ? limit : int.MaxValue)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Generates a SQL clause requiring ALL name values to match (AND logic) with ILIKE partial matching.
    /// Users can type partial names (e.g. "Maxwell" matches "Ghislaine Maxwell", "Miss Maxwell", etc.)
    /// </summary>
    private static string NamesAndClause(string tableAlias)
    {
        return $"(SELECT COUNT(*) FROM unnest(@nameValues::text[]) pn WHERE EXISTS (SELECT 1 FROM jsonb_array_elements_text({tableAlias}.Metadata->'Names') elem WHERE elem ILIKE '%' || pn || '%')) = array_length(@nameValues::text[], 1)";
    }

    private async Task<List<DocumentSearchResult>> SearchByVectorAsync(NpgsqlConnection conn, float[] queryEmbedding, int limit, List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        var results = new List<DocumentSearchResult>();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };

        // Build WHERE clauses for the final SELECT
        var whereClauses = new List<string>();
        if (datasetFilter) whereClauses.Add("d.Name = ANY(@datasetNames)");
        if (namesFilter) whereClauses.Add(NamesAndClause("p"));
        var whereClause = whereClauses.Count > 0 ? "AND " + string.Join(" AND ", whereClauses) : "";

        // Option A: Vector similarity search on DocumentChunks
        string sql = $@"
            SELECT p.FileName,
                   p.FilePath,
                   d.pdffolder as PdfFolder,
                   COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                   c.TextContent as FullText,
                   c.Embedding <=> @queryVector as distance,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                   s.Name as SourceName,
                   d.Name as DataSetName,
                   p.Metadata->>'Names' as Names,
                   p.Metadata->>'Terms' as Terms,
                   p.Metadata::text as MetadataJson,
                   s.Url as SourceUrl
            FROM DocumentChunks c
            JOIN ParentDocuments p ON c.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            WHERE c.Embedding IS NOT NULL
            {whereClause}
            ORDER BY c.Embedding <=> @queryVector
            {(limit > 0 ? "LIMIT @limit" : "")};
        ";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("queryVector", new Vector(queryEmbedding));
        if (limit > 0) cmd.Parameters.AddWithValue("limit", limit);
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadSearchResult(reader));
        }

        return results;
    }

    private async Task<List<DocumentSearchResult>> SearchByTextAsync(NpgsqlConnection conn, string query, int limit, List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        var results = new List<DocumentSearchResult>();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };

        // Option B: Search ParentDocuments.Sentences JSONB directly
        var extraJoin = datasetFilter ? "JOIN DataSets dd ON p.DataSetId = dd.Id" : "";
        var extraWhere = new List<string>();
        if (datasetFilter) extraWhere.Add("dd.Name = ANY(@datasetNames)");
        if (namesFilter) extraWhere.Add(NamesAndClause("p"));
        var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

        // Full-text search across Sentences JSONB AND metadata
        // Option A: Search DocumentChunks for text matches
        string sql = $@"
            WITH text_matches AS (
                SELECT c.ParentId,
                       MAX(ts_rank(to_tsvector('english', c.TextContent), plainto_tsquery('english', @query))) as score,
                       (ARRAY_AGG(c.TextContent ORDER BY ts_rank(to_tsvector('english', c.TextContent), plainto_tsquery('english', @query)) DESC))[1] as best_chunk
                FROM DocumentChunks c
                JOIN ParentDocuments p2 ON c.ParentId = p2.Id
                {extraJoin}
                WHERE (to_tsvector('english', c.TextContent) @@ plainto_tsquery('english', @query)
                   OR c.TextContent ILIKE @pattern)
                {extraWhereStr}
                GROUP BY c.ParentId
            ),
            metadata_matches AS (
                SELECT p.Id as ParentId, 0.5 as score, '' as best_chunk
                FROM ParentDocuments p
                {(datasetFilter ? "JOIN DataSets dd2 ON p.DataSetId = dd2.Id" : "")}
                WHERE (p.Metadata->>'Title' ILIKE @pattern
                   OR p.Metadata->>'Names' ILIKE @pattern
                   OR p.Metadata->>'FileName' ILIKE @pattern
                   OR p.Metadata->>'DataSetName' ILIKE @pattern
                   OR p.FileName ILIKE @pattern)
                {(datasetFilter ? "AND dd2.Name = ANY(@datasetNames)" : "")}
                {(namesFilter ? "AND " + NamesAndClause("p") : "")}
            ),
            matching_docs AS (
                SELECT ParentId, MAX(score) as score, MAX(best_chunk) as best_chunk
                FROM (
                    SELECT ParentId, score, best_chunk FROM text_matches
                    UNION ALL
                    SELECT ParentId, score, best_chunk FROM metadata_matches
                ) combined
                GROUP BY ParentId
                ORDER BY score DESC
                {(limit > 0 ? "LIMIT @limit" : "")}
            )
            SELECT p.FileName,
                   p.FilePath,
                   d.pdffolder as PdfFolder,
                   COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                   COALESCE(NULLIF(md.best_chunk, ''), (SELECT string_agg(elem, ' ') FROM jsonb_array_elements_text(p.Sentences) elem)) as FullText,
                   md.score,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                   s.Name as SourceName,
                   d.Name as DataSetName,
                   p.Metadata->>'Names' as Names,
                   p.Metadata->>'Terms' as Terms,
                   p.Metadata::text as MetadataJson,
                   s.Url as SourceUrl
            FROM matching_docs md
            JOIN ParentDocuments p ON md.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            ORDER BY md.score DESC;
        ";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("pattern", $"%{query}%");
        if (limit > 0) cmd.Parameters.AddWithValue("limit", limit);
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadSearchResult(reader));
        }

        return results;
    }

    /// <summary>
    /// Exact text match search - only returns documents containing the exact query string
    /// </summary>
    public List<DocumentSearchResult> SearchExactMatch(string query, int limit = 20, List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        var results = new List<DocumentSearchResult>();

        try
        {
            using var conn = _dataSource.OpenConnection();
            var datasetFilter = datasetNames is { Count: > 0 };
            var namesFilter = nameValues is { Count: > 0 };

        // Option A: Search DocumentChunks for exact match
        var extraJoin = datasetFilter ? "JOIN DataSets dd ON p.DataSetId = dd.Id" : "";
        var extraWhere = new List<string>();
        if (datasetFilter) extraWhere.Add("dd.Name = ANY(@datasetNames)");
        if (namesFilter) extraWhere.Add(NamesAndClause("p"));
        var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

        string sql = $@"
            WITH text_matches AS (
                SELECT DISTINCT c.ParentId
                FROM DocumentChunks c
                JOIN ParentDocuments p ON c.ParentId = p.Id
                {extraJoin}
                WHERE c.TextContent ILIKE @pattern
                {extraWhereStr}
            ),
            metadata_matches AS (
                SELECT p.Id as ParentId
                FROM ParentDocuments p
                {(datasetFilter ? "JOIN DataSets dd2 ON p.DataSetId = dd2.Id" : "")}
                WHERE (p.Metadata->>'Title' ILIKE @pattern
                   OR p.Metadata->>'Names' ILIKE @pattern
                   OR p.Metadata->>'FileName' ILIKE @pattern
                   OR p.Metadata->>'DataSetName' ILIKE @pattern
                   OR p.FileName ILIKE @pattern)
                {(datasetFilter ? "AND dd2.Name = ANY(@datasetNames)" : "")}
                {(namesFilter ? "AND " + NamesAndClause("p") : "")}
            ),
            matching_docs AS (
                SELECT DISTINCT ParentId FROM (
                    SELECT ParentId FROM text_matches
                    UNION
                    SELECT ParentId FROM metadata_matches
                ) combined
                {(limit > 0 ? "LIMIT @limit" : "")}
            )
            SELECT p.FileName,
                   p.FilePath,
                   d.pdffolder as PdfFolder,
                   COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                   (SELECT TextContent FROM DocumentChunks WHERE ParentId = p.Id AND TextContent ILIKE @pattern LIMIT 1) as FullText,
                   1.0 as score,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                   s.Name as SourceName,
                   d.Name as DataSetName,
                   p.Metadata->>'Names' as Names,
                   p.Metadata->>'Terms' as Terms,
                   p.Metadata::text as MetadataJson,
                   s.Url as SourceUrl
            FROM matching_docs md
            JOIN ParentDocuments p ON md.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id;
        ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("pattern", $"%{query}%");
            if (limit > 0) cmd.Parameters.AddWithValue("limit", limit);
            if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
            if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSearchResult(reader));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exact match search failed: {ex.Message}");
        }

        return results;
    }

    public List<DocumentSearchResult> GetRecentDocuments(int limit = 10, List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        var results = new List<DocumentSearchResult>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            var datasetFilter = datasetNames is { Count: > 0 };
            var namesFilter = nameValues is { Count: > 0 };

            // Sample recent docs — when limit=0 (unlimited), show all per dataset
            int dataSetCount = datasetFilter ? datasetNames!.Count : GetDataSetCount(conn);
            int perDataSet = limit > 0 ? Math.Max(3, limit / Math.Max(1, dataSetCount)) : int.MaxValue;

            var whereClauses = new List<string>();
            if (datasetFilter) whereClauses.Add("d.Name = ANY(@datasetNames)");
            if (namesFilter) whereClauses.Add(NamesAndClause("p"));
            var innerWhere = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

            string sql = $@"
                WITH ranked AS (
                    SELECT p.*,
                           d.pdffolder,
                           COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                           d.Name as DataSetName,
                           s.Name as SourceName,
                           s.Url as SourceUrl,
                           ROW_NUMBER() OVER (PARTITION BY p.DataSetId ORDER BY p.ProcessedAt DESC) as rn
                    FROM ParentDocuments p
                    LEFT JOIN DataSets d ON p.DataSetId = d.Id
                    LEFT JOIN Sources s ON d.SourceId = s.Id
                    {innerWhere}
                )
                SELECT r.FileName,
                       r.FilePath,
                       r.pdffolder as PdfFolder,
                       r.ImageFolder,
                       (SELECT string_agg(elem, ' ') FROM jsonb_array_elements_text(r.Sentences) elem) as Text,
                       0.0 as Distance,
                       (r.Metadata->>'DeducedDate')::timestamp as DocDate,
                       (r.Metadata->>'PageCount')::int as PageCount,
                       (SELECT filename FROM DocumentImages WHERE ParentId = r.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                       (SELECT filename FROM DocumentImages WHERE ParentId = r.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                       r.SourceName,
                       r.DataSetName,
                       r.Metadata->>'Names' as Names,
                       r.Metadata->>'Terms' as Terms,
                       r.Metadata::text as MetadataJson,
                       r.SourceUrl
                FROM ranked r
                WHERE r.rn <= @perDataSet
                ORDER BY r.ProcessedAt DESC
                {(limit > 0 ? "LIMIT @limit" : "")};
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("perDataSet", perDataSet);
            if (limit > 0) cmd.Parameters.AddWithValue("limit", limit);
            if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
            if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadSearchResult(reader));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting recent docs: {ex.Message}\n{ex.StackTrace}");
            if (ex.InnerException != null) Console.WriteLine($"  Inner: {ex.InnerException.Message}");
        }
        return results;
    }

    /// <summary>
    /// Server-side paged search: returns a page of results + total count for virtual scrolling.
    /// Supports text search (vector+fulltext), exact match, or recent (no query).
    /// </summary>
    public async Task<(List<DocumentSearchResult> Items, int TotalCount)> SearchPagedAsync(
        string? query, int skip, int take, bool exactMatch = false,
        List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            var datasetFilter = datasetNames is { Count: > 0 };
            var namesFilter = nameValues is { Count: > 0 };

            // Build shared WHERE clause for filtering
            var whereClauses = new List<string>();
            if (datasetFilter) whereClauses.Add("d.Name = ANY(@datasetNames)");
            if (namesFilter) whereClauses.Add(NamesAndClause("p"));
            var whereClause = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

            if (string.IsNullOrWhiteSpace(query))
            {
                // No search query — return recent documents, paged
                return GetRecentDocumentsPaged(conn, skip, take, whereClause, datasetFilter, namesFilter, datasetNames, nameValues);
            }

            if (exactMatch)
            {
                return SearchExactMatchPaged(conn, query, skip, take, datasetNames, nameValues);
            }

            // Text search (fulltext) — paged
            return await SearchByTextPagedAsync(conn, query, skip, take, datasetNames, nameValues);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Paged search failed: {ex.Message}\n{ex.StackTrace}");
            if (ex.InnerException != null) Console.WriteLine($"  Inner: {ex.InnerException.Message}");
            return (new List<DocumentSearchResult>(), 0);
        }
    }

    private (List<DocumentSearchResult> Items, int TotalCount) GetRecentDocumentsPaged(
        NpgsqlConnection conn, int skip, int take, string whereClause,
        bool datasetFilter, bool namesFilter,
        List<string>? datasetNames, List<string>? nameValues)
    {
        // Count total matching documents
        var countSql = $@"SELECT COUNT(*) FROM ParentDocuments p
            LEFT JOIN DataSets d ON p.DataSetId = d.Id {whereClause}";
        using var countCmd = new NpgsqlCommand(countSql, conn);
        countCmd.CommandTimeout = 120;
        if (datasetFilter) countCmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) countCmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        var totalCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        // Fetch the page
        var sql = $@"
            SELECT p.FileName,
                   p.FilePath,
                   d.pdffolder as PdfFolder,
                   COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                   (SELECT string_agg(elem, ' ') FROM jsonb_array_elements_text(p.Sentences) elem) as Text,
                   0.0 as Distance,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                   s.Name as SourceName,
                   d.Name as DataSetName,
                   p.Metadata->>'Names' as Names,
                   p.Metadata->>'Terms' as Terms,
                   p.Metadata::text as MetadataJson,
                   s.Url as SourceUrl
            FROM ParentDocuments p
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            {whereClause}
            ORDER BY p.ProcessedAt DESC
            OFFSET @skip LIMIT @take;";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 120;
        cmd.Parameters.AddWithValue("skip", skip);
        cmd.Parameters.AddWithValue("take", take);
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());

        var results = new List<DocumentSearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(ReadSearchResult(reader));
        return (results, totalCount);
    }

    private async Task<(List<DocumentSearchResult> Items, int TotalCount)> SearchByTextPagedAsync(
        NpgsqlConnection conn, string query, int skip, int take,
        List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };

        // Option A: Search via DocumentChunks
        var extraJoin = datasetFilter ? "JOIN DataSets dd ON p.DataSetId = dd.Id" : "";
        var extraWhere = new List<string>();
        if (datasetFilter) extraWhere.Add("dd.Name = ANY(@datasetNames)");
        if (namesFilter) extraWhere.Add(NamesAndClause("p"));
        var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

        // CTE that finds all matching doc IDs with scores
        var matchesCte = $@"
            WITH text_matches AS (
                SELECT c.ParentId,
                       MAX(ts_rank(to_tsvector('english', c.TextContent), plainto_tsquery('english', @query))) as score
                FROM DocumentChunks c
                JOIN ParentDocuments p ON c.ParentId = p.Id
                {extraJoin}
                WHERE (to_tsvector('english', c.TextContent) @@ plainto_tsquery('english', @query)
                   OR c.TextContent ILIKE @pattern)
                {extraWhereStr}
                GROUP BY c.ParentId
            ),
            metadata_matches AS (
                SELECT p.Id as ParentId, 0.5 as score
                FROM ParentDocuments p
                {(datasetFilter ? "JOIN DataSets dd2 ON p.DataSetId = dd2.Id" : "")}
                WHERE (p.Metadata->>'Title' ILIKE @pattern
                   OR p.Metadata->>'Names' ILIKE @pattern
                   OR p.Metadata->>'FileName' ILIKE @pattern
                   OR p.Metadata->>'DataSetName' ILIKE @pattern
                   OR p.FileName ILIKE @pattern)
                {(datasetFilter ? "AND dd2.Name = ANY(@datasetNames)" : "")}
                {(namesFilter ? "AND " + NamesAndClause("p") : "")}
            ),
            matching_docs AS (
                SELECT ParentId, MAX(score) as score
                FROM (
                    SELECT ParentId, score FROM text_matches
                    UNION ALL
                    SELECT ParentId, score FROM metadata_matches
                ) combined
                GROUP BY ParentId
            )";

        // Count query
        var countSql = matchesCte + " SELECT COUNT(*) FROM matching_docs;";
        using var countCmd = new NpgsqlCommand(countSql, conn);
        countCmd.CommandTimeout = 120;
        countCmd.Parameters.AddWithValue("query", query);
        countCmd.Parameters.AddWithValue("pattern", $"%{query}%");
        if (datasetFilter) countCmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) countCmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync() ?? 0);

        // Data query with paging
        var dataSql = matchesCte + $@"
            SELECT p.FileName,
                   p.FilePath,
                   d.pdffolder as PdfFolder,
                   COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                   (SELECT TextContent FROM DocumentChunks WHERE ParentId = p.Id ORDER BY ts_rank(to_tsvector('english', TextContent), plainto_tsquery('english', @query)) DESC LIMIT 1) as FullText,
                   md.score,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                   s.Name as SourceName,
                   d.Name as DataSetName,
                   p.Metadata->>'Names' as Names,
                   p.Metadata->>'Terms' as Terms,
                   p.Metadata::text as MetadataJson,
                   s.Url as SourceUrl
            FROM matching_docs md
            JOIN ParentDocuments p ON md.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            ORDER BY md.score DESC
            OFFSET @skip LIMIT @take;";

        using var dataCmd = new NpgsqlCommand(dataSql, conn);
        dataCmd.CommandTimeout = 120;
        dataCmd.Parameters.AddWithValue("query", query);
        dataCmd.Parameters.AddWithValue("pattern", $"%{query}%");
        dataCmd.Parameters.AddWithValue("skip", skip);
        dataCmd.Parameters.AddWithValue("take", take);
        if (datasetFilter) dataCmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) dataCmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());

        var results = new List<DocumentSearchResult>();
        using var reader = await dataCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(ReadSearchResult(reader));
        return (results, totalCount);
    }

    private (List<DocumentSearchResult> Items, int TotalCount) SearchExactMatchPaged(
        NpgsqlConnection conn, string query, int skip, int take,
        List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };

        // Option A: Search DocumentChunks for exact match (paged)
        var extraJoin = datasetFilter ? "JOIN DataSets dd ON p.DataSetId = dd.Id" : "";
        var extraWhere = new List<string>();
        if (datasetFilter) extraWhere.Add("dd.Name = ANY(@datasetNames)");
        if (namesFilter) extraWhere.Add(NamesAndClause("p"));
        var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

        var matchesCte = $@"
            WITH text_matches AS (
                SELECT DISTINCT c.ParentId
                FROM DocumentChunks c
                JOIN ParentDocuments p ON c.ParentId = p.Id
                {extraJoin}
                WHERE c.TextContent ILIKE @pattern
                {extraWhereStr}
            ),
            metadata_matches AS (
                SELECT p.Id as ParentId
                FROM ParentDocuments p
                {(datasetFilter ? "JOIN DataSets dd2 ON p.DataSetId = dd2.Id" : "")}
                WHERE (p.Metadata->>'Title' ILIKE @pattern
                   OR p.Metadata->>'Names' ILIKE @pattern
                   OR p.Metadata->>'FileName' ILIKE @pattern
                   OR p.Metadata->>'DataSetName' ILIKE @pattern
                   OR p.FileName ILIKE @pattern)
                {(datasetFilter ? "AND dd2.Name = ANY(@datasetNames)" : "")}
                {(namesFilter ? "AND " + NamesAndClause("p") : "")}
            ),
            matching_docs AS (
                SELECT DISTINCT ParentId FROM (
                    SELECT ParentId FROM text_matches
                    UNION
                    SELECT ParentId FROM metadata_matches
                ) combined
            )";

        // Count
        var countSql = matchesCte + " SELECT COUNT(*) FROM matching_docs;";
        using var countCmd = new NpgsqlCommand(countSql, conn);
        countCmd.CommandTimeout = 120;
        countCmd.Parameters.AddWithValue("pattern", $"%{query}%");
        if (datasetFilter) countCmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) countCmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        var totalCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        // Data
        var dataSql = matchesCte + $@"
            SELECT p.FileName,
                   p.FilePath,
                   d.pdffolder as PdfFolder,
                   COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                   (SELECT TextContent FROM DocumentChunks WHERE ParentId = p.Id AND TextContent ILIKE @pattern LIMIT 1) as FullText,
                   1.0 as score,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                   s.Name as SourceName,
                   d.Name as DataSetName,
                   p.Metadata->>'Names' as Names,
                   p.Metadata->>'Terms' as Terms,
                   p.Metadata::text as MetadataJson,
                   s.Url as SourceUrl
            FROM matching_docs md
            JOIN ParentDocuments p ON md.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            ORDER BY p.ProcessedAt DESC
            OFFSET @skip LIMIT @take;";

        using var dataCmd = new NpgsqlCommand(dataSql, conn);
        dataCmd.CommandTimeout = 120;
        dataCmd.Parameters.AddWithValue("pattern", $"%{query}%");
        dataCmd.Parameters.AddWithValue("skip", skip);
        dataCmd.Parameters.AddWithValue("take", take);
        if (datasetFilter) dataCmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) dataCmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());

        var results = new List<DocumentSearchResult>();
        using var reader = dataCmd.ExecuteReader();
        while (reader.Read()) results.Add(ReadSearchResult(reader));
        return (results, totalCount);
    }

    /// <summary>
    /// Filename-only search — returns IDs of documents whose FileName matches the query.
    /// </summary>
    public Guid[] SearchByFileNameIds(string query,
        List<string>? datasetNames = null, List<string>? nameValues = null, AdvancedFilters? advanced = null)
    {
        using var conn = _dataSource.OpenConnection();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };
        var advancedFilter = advanced?.HasAny == true;

        var extraJoin = datasetFilter ? "JOIN DataSets dd ON p.DataSetId = dd.Id" : "";
        var extraWhere = new List<string>();
        if (datasetFilter) extraWhere.Add("dd.Name = ANY(@datasetNames)");
        if (namesFilter) extraWhere.Add(NamesAndClause("p"));
        if (advancedFilter) extraWhere.Add(advanced!.BuildWhereFragment("p"));
        var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

        var sql = $@"
            SELECT p.Id FROM ParentDocuments p
            {extraJoin}
            WHERE p.FileName ILIKE @pattern {extraWhereStr}
            ORDER BY p.FileName;";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("pattern", $"%{query}%");
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        if (advancedFilter) advanced!.ApplyParams(cmd);

        var ids = new List<Guid>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    /// <summary>
    /// Lightweight search that returns only matching document IDs (no joins, no Sentences aggregation).
    /// Used by the caching layer — run once, cache the IDs, hydrate pages from cache.
    /// </summary>
    public Guid[] SearchMatchingIds(string query, bool exactMatch,
        List<string>? datasetNames = null, List<string>? nameValues = null)
    {
        using var conn = _dataSource.OpenConnection();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };

        var extraJoin = datasetFilter ? "JOIN DataSets dd ON p.DataSetId = dd.Id" : "";
        var extraWhere = new List<string>();
        if (datasetFilter) extraWhere.Add("dd.Name = ANY(@datasetNames)");
        if (namesFilter) extraWhere.Add(NamesAndClause("p"));
        var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

        if (exactMatch)
        {
            var sql = $@"
                WITH text_matches AS (
                    SELECT DISTINCT c.ParentId
                    FROM DocumentChunks c
                    JOIN ParentDocuments p ON c.ParentId = p.Id
                    {extraJoin}
                    WHERE c.TextContent ILIKE @pattern
                    {extraWhereStr}
                ),
                metadata_matches AS (
                    SELECT p.Id as ParentId
                    FROM ParentDocuments p
                    {(datasetFilter ? "JOIN DataSets dd2 ON p.DataSetId = dd2.Id" : "")}
                    WHERE (p.Metadata->>'Title' ILIKE @pattern
                       OR p.Metadata->>'Names' ILIKE @pattern
                       OR p.Metadata->>'FileName' ILIKE @pattern
                       OR p.Metadata->>'DataSetName' ILIKE @pattern
                       OR p.FileName ILIKE @pattern)
                    {(datasetFilter ? "AND dd2.Name = ANY(@datasetNames)" : "")}
                    {(namesFilter ? "AND " + NamesAndClause("p") : "")}
                ),
                matching_docs AS (
                    SELECT DISTINCT ParentId FROM (
                        SELECT ParentId FROM text_matches
                        UNION
                        SELECT ParentId FROM metadata_matches
                    ) combined
                )
                SELECT md.ParentId
                FROM matching_docs md
                JOIN ParentDocuments p ON md.ParentId = p.Id
                ORDER BY p.ProcessedAt DESC
                LIMIT 50000;";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("pattern", $"%{query}%");
            if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
            if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
            var ids = new List<Guid>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetGuid(0));
            return ids.ToArray();
        }
        else
        {
            // HYBRID RRF for Paged IDs
            var vectorIds = new List<Guid>();
            if (_embeddingService != null)
            {
                try
                {
                    var queryEmbedding = _embeddingService.GetEmbeddingAsync(query).GetAwaiter().GetResult();
                    var vectorSql = $@"
                        SELECT c.ParentId
                        FROM DocumentChunks c
                        JOIN ParentDocuments p ON c.ParentId = p.Id
                        LEFT JOIN DataSets d ON p.DataSetId = d.Id
                        WHERE c.Embedding IS NOT NULL
                        {(datasetFilter ? "AND d.Name = ANY(@datasetNames)" : "")}
                        {(namesFilter ? "AND " + NamesAndClause("p") : "")}
                        ORDER BY c.Embedding <=> @queryVector
                        LIMIT 2000;";
                    using var cmd = new NpgsqlCommand(vectorSql, conn);
                    cmd.Parameters.AddWithValue("queryVector", new Vector(queryEmbedding));
                    if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
                    if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.GetGuid(0);
                        if (!vectorIds.Contains(id)) vectorIds.Add(id);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Vector match ids failed: {ex.Message}"); }
            }

            var textIds = new List<Guid>();
            var textSql = $@"
                WITH text_matches AS (
                    SELECT c.ParentId, MAX(ts_rank(to_tsvector('english', c.TextContent), plainto_tsquery('english', @query))) as score
                    FROM DocumentChunks c
                    JOIN ParentDocuments p ON c.ParentId = p.Id
                    {extraJoin}
                    WHERE (to_tsvector('english', c.TextContent) @@ plainto_tsquery('english', @query) OR c.TextContent ILIKE @pattern)
                    {extraWhereStr}
                    GROUP BY c.ParentId
                ),
                metadata_matches AS (
                    SELECT p.Id as ParentId, 0.5 as score
                    FROM ParentDocuments p
                    {(datasetFilter ? "JOIN DataSets dd2 ON p.DataSetId = dd2.Id" : "")}
                    WHERE (p.Metadata->>'Title' ILIKE @pattern OR p.Metadata->>'Names' ILIKE @pattern OR p.Metadata->>'FileName' ILIKE @pattern OR p.FileName ILIKE @pattern)
                    {(datasetFilter ? "AND dd2.Name = ANY(@datasetNames)" : "")}
                    {(namesFilter ? "AND " + NamesAndClause("p") : "")}
                )
                SELECT ParentId FROM (
                    SELECT ParentId, score FROM text_matches
                    UNION ALL
                    SELECT ParentId, score FROM metadata_matches
                ) combined
                GROUP BY ParentId
                ORDER BY MAX(score) DESC
                LIMIT 2000;";
            using (var cmd = new NpgsqlCommand(textSql, conn))
            {
                cmd.Parameters.AddWithValue("query", query);
                cmd.Parameters.AddWithValue("pattern", $"%{query}%");
                if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
                if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) textIds.Add(reader.GetGuid(0));
            }

            // Combine IDs using RRF
            var rrfScores = new Dictionary<Guid, double>();
            const double k = 60.0;
            for (int i = 0; i < vectorIds.Count; i++) rrfScores[vectorIds[i]] = 1.0 / (k + i + 1);
            for (int i = 0; i < textIds.Count; i++)
            {
                if (rrfScores.TryGetValue(textIds[i], out var score)) rrfScores[textIds[i]] = score + (1.0 / (k + i + 1));
                else rrfScores[textIds[i]] = 1.0 / (k + i + 1);
            }

            return rrfScores.OrderByDescending(x => x.Value).Select(x => x.Key).Take(50000).ToArray();
        }
    }

    /// <summary>
    /// Lightweight query returning document IDs ordered by ProcessedAt DESC (no search predicates).
    /// Returns both the IDs (capped at 50K for cache) and the real total count for UI display.
    /// Used by the browse cache — run once, cache the IDs, hydrate pages via HydrateByIds.
    /// </summary>
    public (Guid[] Ids, int TotalCount) GetBrowseDocumentIds(
        List<string>? datasetNames = null, List<string>? nameValues = null, AdvancedFilters? advanced = null)
    {
        using var conn = _dataSource.OpenConnection();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };
        var advancedFilter = advanced?.HasAny == true;

        var whereClauses = new List<string>();
        if (datasetFilter) whereClauses.Add("d.Name = ANY(@datasetNames)");
        if (namesFilter) whereClauses.Add(NamesAndClause("p"));
        if (advancedFilter) whereClauses.Add(advanced!.BuildWhereFragment("p"));
        var whereClause = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : "";

        // Real total count for UI display
        var countSql = $@"SELECT COUNT(*) FROM ParentDocuments p
            LEFT JOIN DataSets d ON p.DataSetId = d.Id {whereClause}";
        using var countCmd = new NpgsqlCommand(countSql, conn);
        countCmd.CommandTimeout = 120;
        if (datasetFilter) countCmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) countCmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        if (advancedFilter) advanced!.ApplyParams(countCmd);
        var totalCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        // IDs capped at 50K for cache-based paging
        var sql = $@"
            SELECT p.Id
            FROM ParentDocuments p
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            {whereClause}
            ORDER BY p.ProcessedAt DESC
            LIMIT 50000;";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 120;
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        if (advancedFilter) advanced!.ApplyParams(cmd);

        var ids = new List<Guid>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetGuid(0));
        return (ids.ToArray(), totalCount);
    }

    /// <summary>
    /// Hydrate full DocumentSearchResult rows for a page of IDs.
    /// Uses WHERE p.Id = ANY(@ids) + array_position to preserve the search-ranked order.
    /// </summary>
    public List<DocumentSearchResult> HydrateByIds(Guid[] ids, string? query = null)
    {
        if (ids.Length == 0) return new List<DocumentSearchResult>();

        using var conn = _dataSource.OpenConnection();
        var bestChunkSubquery = !string.IsNullOrWhiteSpace(query)
            ? "(SELECT TextContent FROM DocumentChunks WHERE ParentId = p.Id ORDER BY ts_rank(to_tsvector('english', TextContent), plainto_tsquery('english', @query)) DESC LIMIT 1)"
            : "(SELECT string_agg(elem, ' ') FROM jsonb_array_elements_text(p.Sentences) elem)";

        var sql = $@"
            SELECT p.Id,
                   p.FileName,
                   p.FilePath,
                   d.pdffolder as PdfFolder,
                   COALESCE(d.imagefolder, d.pdffolder) as ImageFolder,
                   {bestChunkSubquery} as FullText,
                   0.0 as score,
                   (p.Metadata->>'DeducedDate')::timestamp as DocDate,
                   (p.Metadata->>'PageCount')::int as PageCount,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'thumb' LIMIT 1) as ThumbFileName,
                   (SELECT filename FROM DocumentImages WHERE ParentId = p.Id AND ImageSize = 'full' LIMIT 1) as FullImgFileName,
                   s.Name as SourceName,
                   d.Name as DataSetName,
                   p.Metadata->>'Names' as Names,
                   p.Metadata->>'Terms' as Terms,
                   p.Metadata::text as MetadataJson,
                   s.Url as SourceUrl
            FROM ParentDocuments p
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            LEFT JOIN Sources s ON d.SourceId = s.Id
            WHERE p.Id = ANY(@ids)
            ORDER BY array_position(@ids, p.Id);";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = 120;
        cmd.Parameters.AddWithValue("ids", ids);
        if (!string.IsNullOrWhiteSpace(query)) cmd.Parameters.AddWithValue("query", query);

        var results = new List<DocumentSearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var result = ReadSearchResult(reader, idOffset: 1);
            result.Id = reader.GetGuid(0);
            results.Add(result);
        }
        return results;
    }

    private static int GetDataSetCount(NpgsqlConnection conn)
    {
        try
        {
            using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM DataSets", conn);
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 1);
        }
        catch { return 1; }
    }

    public (long Docs, long Images, long Sentences) GetCounts()
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmdDocs = new NpgsqlCommand("SELECT count(*) FROM ParentDocuments", conn);
            long docs = (long)(cmdDocs.ExecuteScalar() ?? 0L);

            using var cmdImages = new NpgsqlCommand("SELECT count(*) FROM DocumentImages", conn);
            long images = (long)(cmdImages.ExecuteScalar() ?? 0L);

            // Option B: Count total sentences across all documents
            using var cmdSentences = new NpgsqlCommand("SELECT COALESCE(SUM(jsonb_array_length(Sentences)), 0) FROM ParentDocuments WHERE Sentences IS NOT NULL", conn);
            cmdSentences.CommandTimeout = 120;
            long sentences = (long)(cmdSentences.ExecuteScalar() ?? 0L);

            return (docs, images, sentences);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting counts: {ex.Message}");
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// Shared reader for the standardized 16-column search result layout.
    /// Column order: FileName, FilePath, PdfFolder, ImageFolder, Text, Distance/Score,
    /// DocDate, PageCount, ThumbFileName, FullImgFileName, SourceName, DataSetName, Names, Terms, MetadataJson, SourceUrl
    /// idOffset allows prepending extra columns (e.g. p.Id) before the standard 16.
    /// </summary>
    private static DocumentSearchResult ReadSearchResult(NpgsqlDataReader reader, int idOffset = 0)
    {
        int o = idOffset;
        return new DocumentSearchResult
        {
            FileName           = reader.IsDBNull(0+o) ? "" : reader.GetString(0+o),
            FilePath           = reader.IsDBNull(1+o) ? null : reader.GetString(1+o),
            PdfFolder          = reader.IsDBNull(2+o) ? null : reader.GetString(2+o),
            ImageFolder        = reader.IsDBNull(3+o) ? null : reader.GetString(3+o),
            Text               = reader.IsDBNull(4+o) ? "No text content" : reader.GetString(4+o),
            Distance           = reader.IsDBNull(5+o) ? 0.0 : reader.GetDouble(5+o),
            Date               = reader.IsDBNull(6+o) ? null : reader.GetDateTime(6+o),
            PageCount          = reader.IsDBNull(7+o) ? 0 : reader.GetInt32(7+o),
            ThumbnailFileName  = reader.IsDBNull(8+o) ? null : reader.GetString(8+o),
            FullImageFileName  = reader.IsDBNull(9+o) ? null : reader.GetString(9+o),
            SourceName         = reader.IsDBNull(10+o) ? null : reader.GetString(10+o),
            DataSetName        = reader.IsDBNull(11+o) ? null : reader.GetString(11+o),
            Names              = reader.IsDBNull(12+o) ? null : reader.GetString(12+o),
            Terms              = reader.IsDBNull(13+o) ? null : reader.GetString(13+o),
            MetadataJson       = reader.IsDBNull(14+o) ? "{}" : reader.GetString(14+o),
            SourceUrl          = reader.IsDBNull(15+o) ? null : reader.GetString(15+o)
        };
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
            // Option B: No more DocumentChunks join — sentence count from JSONB
            string sql = @"
                SELECT
                    COALESCE(s.Name, 'Unknown') as SourceName,
                    COALESCE(d.Name, 'Unassigned') as DataSetName,
                    COUNT(DISTINCT p.Id) as DocumentCount,
                    COALESCE(SUM((p.Metadata->>'PageCount')::int), 0) as TotalPages,
                    COUNT(DISTINCT i.Id) as ImageCount,
                    COALESCE(SUM(jsonb_array_length(p.Sentences)), 0) as SentenceCount,
                    MIN(p.ProcessedAt) as FirstProcessed,
                    MAX(p.ProcessedAt) as LastProcessed
                FROM ParentDocuments p
                LEFT JOIN DataSets d ON p.DataSetId = d.Id
                LEFT JOIN Sources s ON d.SourceId = s.Id
                LEFT JOIN DocumentImages i ON i.ParentId = p.Id
                GROUP BY s.Name, d.Name
                ORDER BY s.Name, d.Name;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 120;
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
                    SentenceCount = Convert.ToInt64(reader.GetValue(5)),
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
            var (docs, images, sentences) = GetCounts();
            stats.TotalDocuments = docs;
            stats.TotalImages = images;
            stats.TotalSentences = sentences;

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

            // Documents with embeddings (Option B: on ParentDocuments directly)
            using (var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM ParentDocuments WHERE Embedding IS NOT NULL;", conn))
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
    /// <summary>
    /// Get distinct dataset names that have documents.
    /// </summary>
    public List<string> GetDataSetNames()
    {
        var names = new List<string>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT DISTINCT d.Name 
                FROM DataSets d 
                INNER JOIN ParentDocuments p ON p.DataSetId = d.Id 
                ORDER BY d.Name;", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) names.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting dataset names: {ex.Message}");
        }
        return names;
    }
} // end DbService

public class DataSetStats
{
    public string SourceName { get; set; } = "";
    public string DataSetName { get; set; } = "";
    public long DocumentCount { get; set; }
    public long TotalPages { get; set; }
    public long ImageCount { get; set; }
    public long SentenceCount { get; set; }
    public DateTime? FirstProcessed { get; set; }
    public DateTime? LastProcessed { get; set; }
}



public class SystemStats
{
    public long TotalDocuments { get; set; }
    public long TotalPages { get; set; }
    public long TotalImages { get; set; }
    public long TotalSentences { get; set; }
    public long SourceCount { get; set; }
    public long DataSetCount { get; set; }
    public long DocumentsWithEmbeddings { get; set; }
    public double AvgPagesPerDocument { get; set; }
    public DateTime? LastProcessedAt { get; set; }
}

public partial class DbService
{
    // ============================================================
    // REPROCESS MODE helpers
    // ============================================================

    /// <summary>
    /// Get a batch of documents (Id, FilePath, Metadata JSON) for reprocessing.
    /// </summary>
    public List<(Guid Id, string FilePath, string MetadataJson)> GetDocumentsForReprocessing(int limit = 500, int offset = 0)
    {
        var results = new List<(Guid, string, string)>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT Id, COALESCE(FileName, FilePath, ''), Metadata::text
                FROM ParentDocuments p
                ORDER BY Id
                LIMIT @limit OFFSET @offset;", conn);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            cmd.CommandTimeout = 120;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2)
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting documents for reprocessing: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Get the full text of a document by joining its Sentences JSONB array.
    /// </summary>
    public string GetDocumentFullText(Guid parentId)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                SELECT string_agg(elem, E'\n') 
                FROM jsonb_array_elements_text(
                    (SELECT Sentences FROM ParentDocuments WHERE Id = @pid)
                ) AS elem;", conn);
            cmd.Parameters.AddWithValue("pid", parentId);
            cmd.CommandTimeout = 30;

            var result = cmd.ExecuteScalar();
            return result as string ?? "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting full text for doc {parentId}: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Update only the metadata JSONB for a document (no chunk changes).
    /// </summary>
    public void UpdateDocumentMetadata(Guid parentId, string metadataJson)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                UPDATE ParentDocuments 
                SET Metadata = @meta::jsonb 
                WHERE Id = @id;", conn);
            cmd.Parameters.AddWithValue("id", parentId);
            cmd.Parameters.AddWithValue("meta", metadataJson);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating metadata for doc {parentId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Get documents that have no embedding vector, with optional limit.
    /// Option B: Reads Sentences JSONB from ParentDocuments, concatenates for embedding text.
    /// Returns (DocId, SentencesText, FilePath).
    /// </summary>
    public List<(Guid DocId, string SentencesText, string FilePath)> GetDocsWithoutEmbeddings(int limit = 1000)
    {
        var results = new List<(Guid, string, string)>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                SELECT p.Id,
                       (SELECT string_agg(elem, ' ') FROM jsonb_array_elements_text(p.Sentences) elem) as SentencesText,
                       COALESCE(p.FileName, p.FilePath, '')
                FROM ParentDocuments p
                WHERE p.Embedding IS NULL AND p.Sentences IS NOT NULL AND jsonb_array_length(p.Sentences) > 0
                ORDER BY p.Id
                LIMIT @limit;
            ";
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.CommandTimeout = 120;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.GetString(2)
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting docs without embeddings: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Update the embedding vector for a document by Id.
    /// </summary>
    public bool UpdateDocumentEmbedding(Guid docId, float[] embedding)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                UPDATE ParentDocuments
                SET Embedding = @emb
                WHERE Id = @id;
            ";
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", docId);
            cmd.Parameters.AddWithValue("emb", new Vector(embedding));
            return cmd.ExecuteNonQuery() > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating doc embedding {docId}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Count total documents without embeddings.
    /// </summary>
    public long CountDocsWithoutEmbeddings()
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM ParentDocuments WHERE Embedding IS NULL AND Sentences IS NOT NULL AND jsonb_array_length(Sentences) > 0;", conn);
            cmd.CommandTimeout = 120;
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error counting docs without embeddings: {ex.Message}");
            return -1;
        }
    }

    public List<(string ImageType, string ImageSize, string FilePath, string FileName, int Width, int Height, byte[] ImageData)> GetDocumentImages(string parentFilePath)
    {
        var results = new List<(string, string, string, string, int, int, byte[])>();
        try
        {
            using var conn = _dataSource.OpenConnection();
            string fileName = ExtractFileName(parentFilePath);
            string sql = @"
                SELECT i.ImageType, i.ImageSize, i.FilePath, i.FileName, i.Width, i.Height, i.ImageData
                FROM DocumentImages i
                JOIN ParentDocuments p ON i.ParentId = p.Id
                WHERE p.FileName = @fn
                ORDER BY i.ImageSize;
            ";
            
            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("fn", fileName);
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? Array.Empty<byte>() : (byte[])reader[6]
                ));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting images: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Get image binary data directly from DB by file path.
    /// Used as fallback when image file is not available on the local OS.
    /// </summary>
    public byte[] GetImageData(string imagePath)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            string fileName = ExtractFileName(imagePath);

            // Try matching by FileName in documentimages
            string sql = @"
                SELECT ImageData FROM DocumentImages 
                WHERE FileName = @fn AND ImageData IS NOT NULL
                LIMIT 1;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("fn", fileName);

            var result = cmd.ExecuteScalar();
            return result as byte[] ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting image data: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Serves image blob directly from DB by parent document Id and size.
    /// </summary>
    public byte[] GetImageDataByParentId(Guid parentId, string size)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                SELECT ImageData FROM DocumentImages 
                WHERE ParentId = @id AND ImageSize = @size AND ImageData IS NOT NULL
                LIMIT 1;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", parentId);
            cmd.Parameters.AddWithValue("size", size);

            var result = cmd.ExecuteScalar();
            return result as byte[] ?? Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting image data by parent ID: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Resolves the absolute file path for a parent document.
    /// </summary>
    public string? GetFilePathByParentId(Guid parentId)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                SELECT p.FileName, p.FilePath, d.PdfFolder 
                FROM ParentDocuments p
                LEFT JOIN DataSets d ON p.DataSetId = d.Id
                WHERE p.Id = @id;
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", parentId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                string fileName = reader.GetString(0);
                string? filePath = reader.IsDBNull(1) ? null : reader.GetString(1);
                string? pdfFolder = reader.IsDBNull(2) ? null : reader.GetString(2);

                return BuildPath(pdfFolder, fileName) 
                    ?? (filePath != null ? ResolveFilePathForCurrentOs(filePath) : null);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting file path: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Inserts or updates a page image in the DocumentImages table.
    /// </summary>
    public void InsertPageImage(Guid parentId, string size, byte[] data, int width, int height)
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            string sql = @"
                INSERT INTO DocumentImages (ParentId, ImageType, ImageSize, ImageData, Width, Height, CreatedAt)
                VALUES (@parentId, 'Page', @size, @data, @width, @height, NOW())
                ON CONFLICT (ParentId, ImageSize) DO UPDATE SET
                    ImageData = EXCLUDED.ImageData,
                    Width = EXCLUDED.Width,
                    Height = EXCLUDED.Height,
                    CreatedAt = NOW();
            ";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("parentId", parentId);
            cmd.Parameters.AddWithValue("size", size);
            cmd.Parameters.AddWithValue("data", data);
            cmd.Parameters.AddWithValue("width", width);
            cmd.Parameters.AddWithValue("height", height);

            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inserting page image: {ex.Message}");
        }
    }
}
