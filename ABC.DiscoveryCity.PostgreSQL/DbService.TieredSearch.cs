using Npgsql;
using Pgvector;
using System.Security.Cryptography;
using System.Text;

namespace ABC.DiscoveryCity.PostgreSQL;

/// <summary>
/// Tiered search cache: dedicated tables per search type with fan-out and RRF merge.
/// Tiers: 1=Filename, 2=Metadata, 3=FullText, 4=Vector, 5=Grammar (future).
/// </summary>
public partial class DbService
{
    private const double RrfK = 60.0;
    private const int MaxCachedResults = 50000;
    private const int DbCommandTimeout = 120; // seconds
    // -----------------------------------------------------------------------
    // Schema creation (called from InitDb migration block)
    // -----------------------------------------------------------------------

    public void InitTieredSearchTables()
    {
        using var conn = _dataSource.OpenConnection();

        var ddl = @"
            -- MIGRATION: Drop old INT-based tier tables if they were accidentally created
            DROP TABLE IF EXISTS SearchTier1_Filename CASCADE;
            DROP TABLE IF EXISTS SearchTier2_Metadata CASCADE;
            DROP TABLE IF EXISTS SearchTier3_FullText CASCADE;
            DROP TABLE IF EXISTS SearchTier4_Vector CASCADE;
            DROP TABLE IF EXISTS SearchTier5_Grammar CASCADE;
            DROP TABLE IF EXISTS SearchResultsMerged CASCADE;

            CREATE TABLE IF NOT EXISTS SearchQueries (
                Id              SERIAL PRIMARY KEY,
                QueryHash       TEXT NOT NULL UNIQUE,
                QueryText       TEXT,
                ExactMatch      BOOLEAN NOT NULL DEFAULT FALSE,
                FilenameOnly    BOOLEAN NOT NULL DEFAULT FALSE,
                DataSetFilter   TEXT[],
                NamesFilter     TEXT[],
                Tier1Done       BOOLEAN NOT NULL DEFAULT FALSE,
                Tier2Done       BOOLEAN NOT NULL DEFAULT FALSE,
                Tier3Done       BOOLEAN NOT NULL DEFAULT FALSE,
                Tier4Done       BOOLEAN NOT NULL DEFAULT FALSE,
                Tier5Done       BOOLEAN NOT NULL DEFAULT FALSE,
                MergeCount      INT NOT NULL DEFAULT 0,
                LastMergeAt     TIMESTAMPTZ,
                Tier1Count      INT NOT NULL DEFAULT 0,
                Tier2Count      INT NOT NULL DEFAULT 0,
                Tier3Count      INT NOT NULL DEFAULT 0,
                Tier4Count      INT NOT NULL DEFAULT 0,
                Tier5Count      INT NOT NULL DEFAULT 0,
                MergedCount     INT NOT NULL DEFAULT 0,
                CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                ExpiresAt       TIMESTAMPTZ NOT NULL DEFAULT NOW() + INTERVAL '30 minutes'
            );
            CREATE INDEX IF NOT EXISTS idx_sq_hash ON SearchQueries(QueryHash);
            CREATE INDEX IF NOT EXISTS idx_sq_expires ON SearchQueries(ExpiresAt);

            CREATE TABLE IF NOT EXISTS SearchTier1_Filename (
                Id              SERIAL PRIMARY KEY,
                QueryHash       TEXT NOT NULL,
                DocumentId      UUID NOT NULL,
                FileName        TEXT NOT NULL,
                Rank            INT NOT NULL,
                Similarity      REAL,
                CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(QueryHash, DocumentId)
            );
            CREATE INDEX IF NOT EXISTS idx_t1_query ON SearchTier1_Filename(QueryHash, Rank);

            CREATE TABLE IF NOT EXISTS SearchTier2_Metadata (
                Id              SERIAL PRIMARY KEY,
                QueryHash       TEXT NOT NULL,
                DocumentId      UUID NOT NULL,
                MatchedField    TEXT NOT NULL,
                MatchedValue    TEXT,
                Rank            INT NOT NULL,
                Similarity      REAL,
                CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(QueryHash, DocumentId, MatchedField)
            );
            CREATE INDEX IF NOT EXISTS idx_t2_query ON SearchTier2_Metadata(QueryHash, Rank);
            CREATE INDEX IF NOT EXISTS idx_t2_doc ON SearchTier2_Metadata(QueryHash, DocumentId);

            CREATE TABLE IF NOT EXISTS SearchTier3_FullText (
                Id              SERIAL PRIMARY KEY,
                QueryHash       TEXT NOT NULL,
                DocumentId      UUID NOT NULL,
                ChunkId         UUID,
                ChunkIndex      INT,
                MatchingChunks  INT NOT NULL DEFAULT 1,
                Snippet         TEXT,
                TsRank          REAL NOT NULL,
                Rank            INT NOT NULL,
                CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(QueryHash, DocumentId)
            );
            CREATE INDEX IF NOT EXISTS idx_t3_query ON SearchTier3_FullText(QueryHash, Rank);

            CREATE TABLE IF NOT EXISTS SearchTier4_Vector (
                Id              SERIAL PRIMARY KEY,
                QueryHash       TEXT NOT NULL,
                DocumentId      UUID NOT NULL,
                ChunkId         UUID,
                ChunkIndex      INT,
                Distance        REAL NOT NULL,
                Snippet         TEXT,
                Rank            INT NOT NULL,
                CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(QueryHash, DocumentId)
            );
            CREATE INDEX IF NOT EXISTS idx_t4_query ON SearchTier4_Vector(QueryHash, Rank);

            CREATE TABLE IF NOT EXISTS SearchTier5_Grammar (
                Id              SERIAL PRIMARY KEY,
                QueryHash       TEXT NOT NULL,
                DocumentId      UUID NOT NULL,
                MatchType       TEXT NOT NULL,
                RuleName        TEXT,
                MatchedText     TEXT,
                MatchPosition   INT,
                Confidence      REAL,
                Rank            INT NOT NULL,
                ScopeDataSetId  INT,
                CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(QueryHash, DocumentId, MatchType)
            );
            CREATE INDEX IF NOT EXISTS idx_t5_query ON SearchTier5_Grammar(QueryHash, Rank);
            CREATE INDEX IF NOT EXISTS idx_t5_scope ON SearchTier5_Grammar(ScopeDataSetId);

            CREATE TABLE IF NOT EXISTS SearchResultsMerged (
                Id              SERIAL PRIMARY KEY,
                QueryHash       TEXT NOT NULL,
                DocumentId      UUID NOT NULL,
                FinalRank       INT NOT NULL,
                RrfScore        DOUBLE PRECISION NOT NULL,
                Tier1Rank       INT,
                Tier2Rank       INT,
                Tier3Rank       INT,
                Tier4Rank       INT,
                Tier5Rank       INT,
                BestSnippet     TEXT,
                SnippetSource   TEXT,
                MergeVersion    INT NOT NULL DEFAULT 1,
                CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE(QueryHash, DocumentId)
            );
            CREATE INDEX IF NOT EXISTS idx_merged_query ON SearchResultsMerged(QueryHash, FinalRank);
        ";

        using var cmd = new NpgsqlCommand(ddl, conn);
        //cmd.CommandTimeout = DbCommandTimeout;
        cmd.ExecuteNonQuery();
        Console.WriteLine("Tiered search cache tables initialized.");
    }

    // -----------------------------------------------------------------------
    // Query hash
    // -----------------------------------------------------------------------

    public static string ComputeQueryHash(string? query, bool exactMatch, bool filenameOnly,
        List<string>? datasets, List<string>? names, AdvancedFilters? advanced = null)
    {
        var sb = new StringBuilder();
        sb.Append(query?.ToLowerInvariant() ?? "");
        sb.Append('|');
        sb.Append(exactMatch ? '1' : '0');
        sb.Append('|');
        sb.Append(filenameOnly ? '1' : '0');
        if (datasets is { Count: > 0 })
        {
            sb.Append("|ds=");
            sb.Append(string.Join(",", datasets.OrderBy(d => d)));
        }
        if (advanced?.HasAny == true)
        {
            sb.Append('|');
            sb.Append(advanced.ToFingerprint());
        }
        if (names is { Count: > 0 })
        {
            sb.Append("|nm=");
            sb.Append(string.Join(",", names.OrderBy(n => n)));
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    // -----------------------------------------------------------------------
    // SearchQuery coordination record
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get or create a SearchQueries record. Returns (queryHash, isNew).
    /// If the record already exists and is not expired, returns isNew=false.
    /// </summary>
    public (string QueryHash, bool IsNew, SearchQueryStatus? Status) GetOrCreateSearchQuery(
        string? query, bool exactMatch, bool filenameOnly,
        List<string>? datasets, List<string>? names, AdvancedFilters? advanced = null)
    {
        var hash = ComputeQueryHash(query, exactMatch, filenameOnly, datasets, names, advanced);

        using var conn = _dataSource.OpenConnection();

        // Check existing
        using (var cmd = new NpgsqlCommand(@"
            SELECT Tier1Done, Tier2Done, Tier3Done, Tier4Done, Tier5Done,
                   MergeCount, Tier1Count, Tier2Count, Tier3Count, Tier4Count, Tier5Count, MergedCount
            FROM SearchQueries WHERE QueryHash = @hash AND ExpiresAt > NOW()", conn))
        {
            cmd.Parameters.AddWithValue("hash", hash);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var status = new SearchQueryStatus
                {
                    Tier1Done = reader.GetBoolean(0),
                    Tier2Done = reader.GetBoolean(1),
                    Tier3Done = reader.GetBoolean(2),
                    Tier4Done = reader.GetBoolean(3),
                    Tier5Done = reader.GetBoolean(4),
                    MergeCount = reader.GetInt32(5),
                    Tier1Count = reader.GetInt32(6),
                    Tier2Count = reader.GetInt32(7),
                    Tier3Count = reader.GetInt32(8),
                    Tier4Count = reader.GetInt32(9),
                    Tier5Count = reader.GetInt32(10),
                    MergedCount = reader.GetInt32(11)
                };
                return (hash, false, status);
            }
        }

        // Create new
        using (var cmd = new NpgsqlCommand(@"
            INSERT INTO SearchQueries (QueryHash, QueryText, ExactMatch, FilenameOnly, DataSetFilter, NamesFilter)
            VALUES (@hash, @text, @exact, @fnOnly, @ds, @nm)
            ON CONFLICT (QueryHash) DO UPDATE SET
                ExpiresAt = NOW() + INTERVAL '30 minutes',
                Tier1Done = FALSE, Tier2Done = FALSE, Tier3Done = FALSE, Tier4Done = FALSE, Tier5Done = FALSE,
                MergeCount = 0, MergedCount = 0,
                Tier1Count = 0, Tier2Count = 0, Tier3Count = 0, Tier4Count = 0, Tier5Count = 0", conn))
        {
            cmd.Parameters.AddWithValue("hash", hash);
            cmd.Parameters.AddWithValue("text", (object?)query ?? DBNull.Value);
            cmd.Parameters.AddWithValue("exact", exactMatch);
            cmd.Parameters.AddWithValue("fnOnly", filenameOnly);
            cmd.Parameters.AddWithValue("ds", datasets?.ToArray() ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("nm", names?.ToArray() ?? (object)DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        // Clean old tier data for this hash (in case of re-use after expiry)
        CleanTierData(conn, hash);

        return (hash, true, null);
    }

    private static void CleanTierData(NpgsqlConnection conn, string hash)
    {
        var tables = new[] { "SearchTier1_Filename", "SearchTier2_Metadata", "SearchTier3_FullText",
                             "SearchTier4_Vector", "SearchTier5_Grammar", "SearchResultsMerged" };
        foreach (var table in tables)
        {
            using var cmd = new NpgsqlCommand($"DELETE FROM {table} WHERE QueryHash = @hash", conn);
            cmd.Parameters.AddWithValue("hash", hash);
            cmd.ExecuteNonQuery();
        }
    }

    // -----------------------------------------------------------------------
    // Tier 1: Filename ILIKE (~10ms)
    // -----------------------------------------------------------------------

    public int ExecuteTier1_Filename(string queryHash, string query,
        List<string>? datasetNames, List<string>? nameValues, AdvancedFilters? advanced = null)
    {
        using var conn = _dataSource.OpenConnection();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };

        var whereClauses = new List<string> { "p.FileName ILIKE @pattern" };
        if (datasetFilter) whereClauses.Add("d.Name = ANY(@datasetNames)");
        if (namesFilter) whereClauses.Add(NamesAndClause("p"));
        var advFragment = advanced?.BuildWhereFragment("p");
        if (!string.IsNullOrEmpty(advFragment)) whereClauses.Add(advFragment);
        var whereClause = "WHERE " + string.Join(" AND ", whereClauses);

        var sql = $@"
            INSERT INTO SearchTier1_Filename (QueryHash, DocumentId, FileName, Rank, Similarity)
            SELECT @hash, p.Id, p.FileName,
                   ROW_NUMBER() OVER (ORDER BY p.ProcessedAt DESC) as Rank,
                   NULL as Similarity
            FROM ParentDocuments p
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            {whereClause}
            ORDER BY p.ProcessedAt DESC
            LIMIT 2000
            ON CONFLICT (QueryHash, DocumentId) DO NOTHING";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = DbCommandTimeout;
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.Parameters.AddWithValue("pattern", $"%{query}%");
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        advanced?.ApplyParams(cmd);

        var count = cmd.ExecuteNonQuery();

        // Update coordination
        using var upd = new NpgsqlCommand(
            "UPDATE SearchQueries SET Tier1Done = TRUE, Tier1Count = @count WHERE QueryHash = @hash", conn);
        upd.Parameters.AddWithValue("hash", queryHash);
        upd.Parameters.AddWithValue("count", count);
        upd.ExecuteNonQuery();

        return count;
    }

    // -----------------------------------------------------------------------
    // Tier 2: Metadata ILIKE (~50ms)
    // -----------------------------------------------------------------------

    public int ExecuteTier2_Metadata(string queryHash, string query,
        List<string>? datasetNames, List<string>? nameValues, AdvancedFilters? advanced = null)
    {
        using var conn = _dataSource.OpenConnection();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };
        var pattern = $"%{query}%";

        // Search each metadata field separately to track which field matched
        var fields = new[] {
            ("Title", "p.Metadata->>'Title'"),
            ("Names", "p.Metadata->>'Names'"),
            ("Terms", "p.Metadata->>'Terms'"),
            ("DataSetName", "p.Metadata->>'DataSetName'"),
            ("FileName", "p.FileName")
        };

        int totalCount = 0;
        int globalRank = 0;

        foreach (var (fieldName, fieldExpr) in fields)
        {
            var extraWhere = new List<string>();
            if (datasetFilter) extraWhere.Add("d.Name = ANY(@datasetNames)");
            if (namesFilter) extraWhere.Add(NamesAndClause("p"));
            var advFragment = advanced?.BuildWhereFragment("p");
            if (!string.IsNullOrEmpty(advFragment)) extraWhere.Add(advFragment);
            var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

            var sql = $@"
                SELECT p.Id, {fieldExpr} as MatchedValue,
                       0.0::real as sim
                FROM ParentDocuments p
                LEFT JOIN DataSets d ON p.DataSetId = d.Id
                WHERE {fieldExpr} ILIKE @pattern
                {extraWhereStr}
                ORDER BY p.ProcessedAt DESC
                LIMIT 1000";

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = DbCommandTimeout;
            cmd.Parameters.AddWithValue("pattern", pattern);
            if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
            if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
            advanced?.ApplyParams(cmd);

            using var reader = cmd.ExecuteReader();
            var batch = new List<(Guid docId, string? matchedValue, float similarity)>();
            while (reader.Read())
            {
                batch.Add((reader.GetGuid(0),
                           reader.IsDBNull(1) ? null : reader.GetString(1),
                           reader.IsDBNull(2) ? 0f : reader.GetFloat(2)));
            }
            reader.Close();

            // Batch insert
            foreach (var (docId, matchedValue, sim) in batch)
            {
                globalRank++;
                using var ins = new NpgsqlCommand(@"
                    INSERT INTO SearchTier2_Metadata (QueryHash, DocumentId, MatchedField, MatchedValue, Rank, Similarity)
                    VALUES (@hash, @docId, @field, @val, @rank, @sim)
                    ON CONFLICT (QueryHash, DocumentId, MatchedField) DO NOTHING", conn);
                ins.Parameters.AddWithValue("hash", queryHash);
                ins.Parameters.AddWithValue("docId", docId);
                ins.Parameters.AddWithValue("field", fieldName);
                ins.Parameters.AddWithValue("val", (object?)matchedValue ?? DBNull.Value);
                ins.Parameters.AddWithValue("rank", globalRank);
                ins.Parameters.AddWithValue("sim", sim);
                ins.ExecuteNonQuery();
                totalCount++;
            }
        }

        using var upd = new NpgsqlCommand(
            "UPDATE SearchQueries SET Tier2Done = TRUE, Tier2Count = @count WHERE QueryHash = @hash", conn);
        upd.Parameters.AddWithValue("hash", queryHash);
        upd.Parameters.AddWithValue("count", totalCount);
        upd.ExecuteNonQuery();

        return totalCount;
    }

    // -----------------------------------------------------------------------
    // Tier 3: Full-Text Search (~200ms)
    // -----------------------------------------------------------------------

    public int ExecuteTier3_FullText(string queryHash, string query,
        List<string>? datasetNames, List<string>? nameValues, AdvancedFilters? advanced = null)
    {
        using var conn = _dataSource.OpenConnection();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };
        var advFragment = advanced?.BuildWhereFragment("p");
        var needsParentJoin = datasetFilter || namesFilter || !string.IsNullOrEmpty(advFragment);

        var extraJoin = datasetFilter ? "JOIN ParentDocuments p ON c.ParentId = p.Id JOIN DataSets dd ON p.DataSetId = dd.Id" : "";
        var extraWhere = new List<string>();
        if (datasetFilter) extraWhere.Add("dd.Name = ANY(@datasetNames)");
        if (namesFilter)
        {
            if (!datasetFilter) extraJoin = "JOIN ParentDocuments p ON c.ParentId = p.Id";
            extraWhere.Add(NamesAndClause("p"));
        }
        if (!string.IsNullOrEmpty(advFragment))
        {
            if (string.IsNullOrEmpty(extraJoin)) extraJoin = "JOIN ParentDocuments p ON c.ParentId = p.Id";
            extraWhere.Add(advFragment);
        }
        var extraWhereStr = extraWhere.Count > 0 ? "AND " + string.Join(" AND ", extraWhere) : "";

        // Find matching documents with best chunk and count
        var sql = $@"
            WITH chunk_matches AS (
                SELECT c.ParentId, c.Id as ChunkId, c.ChunkIndex,
                       c.TextContent,
                       COUNT(*) OVER (PARTITION BY c.ParentId) as MatchingChunks,
                       ROW_NUMBER() OVER (PARTITION BY c.ParentId
                           ORDER BY ts_rank(to_tsvector('english', c.TextContent), plainto_tsquery('english', @query)) DESC) as rn
                FROM DocumentChunks c
                {extraJoin}
                WHERE to_tsvector('english', c.TextContent) @@ plainto_tsquery('english', @query)
                {extraWhereStr}
            )
            INSERT INTO SearchTier3_FullText (QueryHash, DocumentId, ChunkId, ChunkIndex, MatchingChunks, Snippet, TsRank, Rank)
            SELECT @hash, cm.ParentId, cm.ChunkId, cm.ChunkIndex, cm.MatchingChunks,
                   LEFT(cm.TextContent, 500),
                   cm.MatchingChunks::real,
                   ROW_NUMBER() OVER (ORDER BY cm.MatchingChunks DESC) as Rank
            FROM chunk_matches cm
            WHERE cm.rn = 1
            ORDER BY cm.MatchingChunks DESC
            LIMIT 5000
            ON CONFLICT (QueryHash, DocumentId) DO NOTHING";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = DbCommandTimeout;
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.Parameters.AddWithValue("query", query);
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        advanced?.ApplyParams(cmd);

        var count = cmd.ExecuteNonQuery();

        using var upd = new NpgsqlCommand(
            "UPDATE SearchQueries SET Tier3Done = TRUE, Tier3Count = @count WHERE QueryHash = @hash", conn);
        upd.Parameters.AddWithValue("hash", queryHash);
        upd.Parameters.AddWithValue("count", count);
        upd.ExecuteNonQuery();

        return count;
    }

    // -----------------------------------------------------------------------
    // Tier 4: Vector Similarity (~500ms)
    // -----------------------------------------------------------------------

    public async Task<int> ExecuteTier4_VectorAsync(string queryHash, string query,
        List<string>? datasetNames, List<string>? nameValues, AdvancedFilters? advanced = null)
    {
        if (_embeddingService == null) return 0;

        float[] queryEmbedding;
        try
        {
            queryEmbedding = await _embeddingService.GetEmbeddingAsync(query);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tier4] Embedding failed: {ex.Message}");
            // Mark tier done even on failure so we don't block
            using var conn2 = _dataSource.OpenConnection();
            using var upd2 = new NpgsqlCommand(
                "UPDATE SearchQueries SET Tier4Done = TRUE WHERE QueryHash = @hash", conn2);
            upd2.Parameters.AddWithValue("hash", queryHash);
            upd2.ExecuteNonQuery();
            return 0;
        }

        using var conn = _dataSource.OpenConnection();
        var datasetFilter = datasetNames is { Count: > 0 };
        var namesFilter = nameValues is { Count: > 0 };

        var whereClauses = new List<string> { "1=1" };
        if (datasetFilter) whereClauses.Add("d.Name = ANY(@datasetNames)");
        if (namesFilter) whereClauses.Add(NamesAndClause("p"));
        var advFragment = advanced?.BuildWhereFragment("p");
        if (!string.IsNullOrEmpty(advFragment)) whereClauses.Add(advFragment);
        var whereClause = string.Join(" AND ", whereClauses);

        // --- SQ LOGIC: Quantize query and search for matching signatures ---
        var quantizer = GetSentenceQuantizer();
        var queryGuid = quantizer.Quantize(queryEmbedding);

        var sql = $@"
            INSERT INTO SearchTier4_Vector (QueryHash, DocumentId, ChunkId, ChunkIndex, Distance, Snippet, Rank)
            SELECT @hash, p.Id, NULL, s.""Ordinal"", 0.0,
                   p.Sentences->>s.""Ordinal"",
                   ROW_NUMBER() OVER (ORDER BY s.SemanticId = @queryGuid DESC) as Rank
            FROM SentenceSignatures s
            JOIN ParentDocuments p ON s.ParentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            WHERE s.SemanticId = @queryGuid
            AND {whereClause}
            LIMIT 2000
            ON CONFLICT (QueryHash, DocumentId) DO NOTHING";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = DbCommandTimeout;
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.Parameters.AddWithValue("queryGuid", queryGuid);
        if (datasetFilter) cmd.Parameters.AddWithValue("datasetNames", datasetNames!.ToArray());
        if (namesFilter) cmd.Parameters.AddWithValue("nameValues", nameValues!.ToArray());
        advanced?.ApplyParams(cmd);

        var count = await cmd.ExecuteNonQueryAsync();

        using var upd = new NpgsqlCommand(
            "UPDATE SearchQueries SET Tier4Done = TRUE, Tier4Count = @count WHERE QueryHash = @hash", conn);
        upd.Parameters.AddWithValue("hash", queryHash);
        upd.Parameters.AddWithValue("count", count);
        upd.ExecuteNonQuery();

        return count;
    }

    // -----------------------------------------------------------------------
    // RRF Merge (runs at each checkpoint)
    // -----------------------------------------------------------------------

    public int ExecuteRrfMerge(string queryHash, int mergeVersion)
    {
        using var conn = _dataSource.OpenConnection();

        // Delete existing merged results for this hash (re-merge from scratch)
        using (var del = new NpgsqlCommand("DELETE FROM SearchResultsMerged WHERE QueryHash = @hash", conn))
        {
            del.Parameters.AddWithValue("hash", queryHash);
            del.ExecuteNonQuery();
        }

        // Collect per-tier ranks and compute RRF in SQL
        var sql = @"
            WITH tier_ranks AS (
                SELECT DocumentId, Rank as TierRank, 'tier1' as Source, NULL::text as Snippet
                FROM SearchTier1_Filename WHERE QueryHash = @hash
                UNION ALL
                SELECT DocumentId, MIN(Rank), 'tier2', NULL
                FROM SearchTier2_Metadata WHERE QueryHash = @hash GROUP BY DocumentId
                UNION ALL
                SELECT DocumentId, Rank, 'tier3', Snippet
                FROM SearchTier3_FullText WHERE QueryHash = @hash
                UNION ALL
                SELECT DocumentId, Rank, 'tier4', Snippet
                FROM SearchTier4_Vector WHERE QueryHash = @hash
                UNION ALL
                SELECT DocumentId, Rank, 'tier5', MatchedText
                FROM SearchTier5_Grammar WHERE QueryHash = @hash
            ),
            rrf_scores AS (
                SELECT DocumentId,
                       SUM(1.0 / (60 + TierRank + 1)) as RrfScore,
                       (array_agg(Snippet ORDER BY CASE Source WHEN 'tier3' THEN 1 WHEN 'tier4' THEN 2 ELSE 3 END) FILTER (WHERE Snippet IS NOT NULL))[1] as BestSnippet,
                       (array_agg(Source ORDER BY CASE Source WHEN 'tier3' THEN 1 WHEN 'tier4' THEN 2 ELSE 3 END) FILTER (WHERE Snippet IS NOT NULL))[1] as SnippetSource
                FROM tier_ranks
                GROUP BY DocumentId
            ),
            ranked AS (
                SELECT DocumentId, RrfScore, BestSnippet, SnippetSource,
                       ROW_NUMBER() OVER (ORDER BY RrfScore DESC) as FinalRank
                FROM rrf_scores
            )
            INSERT INTO SearchResultsMerged (QueryHash, DocumentId, FinalRank, RrfScore,
                Tier1Rank, Tier2Rank, Tier3Rank, Tier4Rank, Tier5Rank,
                BestSnippet, SnippetSource, MergeVersion)
            SELECT @hash, r.DocumentId, r.FinalRank, r.RrfScore,
                (SELECT Rank FROM SearchTier1_Filename WHERE QueryHash = @hash AND DocumentId = r.DocumentId LIMIT 1),
                (SELECT MIN(Rank) FROM SearchTier2_Metadata WHERE QueryHash = @hash AND DocumentId = r.DocumentId),
                (SELECT Rank FROM SearchTier3_FullText WHERE QueryHash = @hash AND DocumentId = r.DocumentId LIMIT 1),
                (SELECT Rank FROM SearchTier4_Vector WHERE QueryHash = @hash AND DocumentId = r.DocumentId LIMIT 1),
                (SELECT Rank FROM SearchTier5_Grammar WHERE QueryHash = @hash AND DocumentId = r.DocumentId LIMIT 1),
                r.BestSnippet, r.SnippetSource, @mergeVersion
            FROM ranked r
            WHERE r.FinalRank <= @maxResults
            ON CONFLICT (QueryHash, DocumentId) DO UPDATE SET
                FinalRank = EXCLUDED.FinalRank,
                RrfScore = EXCLUDED.RrfScore,
                Tier1Rank = EXCLUDED.Tier1Rank,
                Tier2Rank = EXCLUDED.Tier2Rank,
                Tier3Rank = EXCLUDED.Tier3Rank,
                Tier4Rank = EXCLUDED.Tier4Rank,
                Tier5Rank = EXCLUDED.Tier5Rank,
                BestSnippet = EXCLUDED.BestSnippet,
                SnippetSource = EXCLUDED.SnippetSource,
                MergeVersion = EXCLUDED.MergeVersion,
                CreatedAt = NOW()";

        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.CommandTimeout = DbCommandTimeout;
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.Parameters.AddWithValue("mergeVersion", mergeVersion);
        cmd.Parameters.AddWithValue("maxResults", MaxCachedResults);

        var count = cmd.ExecuteNonQuery();

        // Update coordination
        using var upd = new NpgsqlCommand(
            "UPDATE SearchQueries SET MergeCount = @mv, MergedCount = @count, LastMergeAt = NOW() WHERE QueryHash = @hash", conn);
        upd.Parameters.AddWithValue("hash", queryHash);
        upd.Parameters.AddWithValue("mv", mergeVersion);
        upd.Parameters.AddWithValue("count", count);
        upd.ExecuteNonQuery();

        return count;
    }

    // -----------------------------------------------------------------------
    // Read merged results (paged)
    // -----------------------------------------------------------------------

    public List<DocumentSearchResult> GetMergedResults(string queryHash, int skip = 0, int take = 50)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT m.DocumentId, p.FileName, p.FilePath, m.BestSnippet, p.Metadata, d.Name, m.RrfScore as Distance, p.PageCount, p.Date, m.SnippetSource
            FROM SearchResultsMerged m
            JOIN ParentDocuments p ON m.DocumentId = p.Id
            LEFT JOIN DataSets d ON p.DataSetId = d.Id
            WHERE m.QueryHash = @hash
            ORDER BY m.FinalRank
            LIMIT @take OFFSET @skip", conn);
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.Parameters.AddWithValue("skip", skip);
        cmd.Parameters.AddWithValue("take", take);

        var results = new List<DocumentSearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var r = new DocumentSearchResult();
            r.Id = reader.GetGuid(0);
            r.FileName = reader.GetString(1);
            r.FilePath = reader.IsDBNull(2) ? null : reader.GetString(2);
            r.Text = reader.IsDBNull(3) ? "" : reader.GetString(3);
            r.MetadataJson = reader.IsDBNull(4) ? "{}" : reader.GetString(4);
            r.DataSetName = reader.IsDBNull(5) ? null : reader.GetString(5);
            r.Distance = reader.GetDouble(6);
            r.PageCount = reader.GetInt32(7);
            r.Date = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
            r.SnippetSource = reader.IsDBNull(9) ? null : reader.GetString(9);
            results.Add(r);
        }
        return results;
    }

    /// <summary>
    /// Read paged results from the merged cache table.
    /// Returns (IDs in rank order, totalMergedCount).
    /// </summary>
    public (Guid[] Ids, int TotalCount) GetMergedResultIds(string queryHash, int skip = 0, int take = 50)
    {
        using var conn = _dataSource.OpenConnection();

        // Total count
        using var countCmd = new NpgsqlCommand(
            "SELECT MergedCount FROM SearchQueries WHERE QueryHash = @hash", conn);
        countCmd.Parameters.AddWithValue("hash", queryHash);
        var totalCount = Convert.ToInt32(countCmd.ExecuteScalar() ?? 0);

        // Paged IDs
        using var cmd = new NpgsqlCommand(@"
            SELECT DocumentId FROM SearchResultsMerged
            WHERE QueryHash = @hash
            ORDER BY FinalRank
            OFFSET @skip LIMIT @take", conn);
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.Parameters.AddWithValue("skip", skip);
        cmd.Parameters.AddWithValue("take", take);

        var ids = new List<Guid>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetGuid(0));

        return (ids.ToArray(), totalCount);
    }

    /// <summary>
    /// Get all merged IDs for this query hash (up to 50K, for the controller's ID cache).
    /// </summary>
    public Guid[] GetAllMergedIds(string queryHash)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT DocumentId FROM SearchResultsMerged
            WHERE QueryHash = @hash
            ORDER BY FinalRank
            LIMIT @maxResults", conn);
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.Parameters.AddWithValue("maxResults", MaxCachedResults);

        var ids = new List<Guid>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    // -----------------------------------------------------------------------
    // Query status check (for polling)
    // -----------------------------------------------------------------------

    public SearchQueryStatus? GetSearchQueryStatus(string queryHash)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT Tier1Done, Tier2Done, Tier3Done, Tier4Done, Tier5Done,
                   MergeCount, Tier1Count, Tier2Count, Tier3Count, Tier4Count, Tier5Count, MergedCount
            FROM SearchQueries WHERE QueryHash = @hash AND ExpiresAt > NOW()", conn);
        cmd.Parameters.AddWithValue("hash", queryHash);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new SearchQueryStatus
        {
            Tier1Done = reader.GetBoolean(0),
            Tier2Done = reader.GetBoolean(1),
            Tier3Done = reader.GetBoolean(2),
            Tier4Done = reader.GetBoolean(3),
            Tier5Done = reader.GetBoolean(4),
            MergeCount = reader.GetInt32(5),
            Tier1Count = reader.GetInt32(6),
            Tier2Count = reader.GetInt32(7),
            Tier3Count = reader.GetInt32(8),
            Tier4Count = reader.GetInt32(9),
            Tier5Count = reader.GetInt32(10),
            MergedCount = reader.GetInt32(11)
        };
    }

    // -----------------------------------------------------------------------
    // Cleanup expired searches
    // -----------------------------------------------------------------------

    public void MarkTierDone(string queryHash, int tier)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand($@"
            UPDATE SearchQueries SET Tier{tier}Done = TRUE 
            WHERE QueryHash = @hash", conn);
        cmd.Parameters.AddWithValue("hash", queryHash);
        cmd.ExecuteNonQuery();
    }

    public void CleanupExpiredSearches()
    {
        try
        {
            using var conn = _dataSource.OpenConnection();
            using var cmd = new NpgsqlCommand(@"
                WITH expired AS (
                    SELECT QueryHash FROM SearchQueries WHERE ExpiresAt < NOW()
                )
                DELETE FROM SearchTier1_Filename WHERE QueryHash IN (SELECT QueryHash FROM expired);
                DELETE FROM SearchTier2_Metadata WHERE QueryHash IN (SELECT QueryHash FROM expired);
                DELETE FROM SearchTier3_FullText WHERE QueryHash IN (SELECT QueryHash FROM expired);
                DELETE FROM SearchTier4_Vector WHERE QueryHash IN (SELECT QueryHash FROM expired);
                DELETE FROM SearchTier5_Grammar WHERE QueryHash IN (SELECT QueryHash FROM expired);
                DELETE FROM SearchResultsMerged WHERE QueryHash IN (SELECT QueryHash FROM expired);
                DELETE FROM SearchQueries WHERE ExpiresAt < NOW()", conn);
            cmd.CommandTimeout = DbCommandTimeout;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TieredSearch] Cleanup failed: {ex.Message}");
        }
    }
}

public class SearchQueryStatus
{
    public bool Tier1Done { get; set; }
    public bool Tier2Done { get; set; }
    public bool Tier3Done { get; set; }
    public bool Tier4Done { get; set; }
    public bool Tier5Done { get; set; }
    public int MergeCount { get; set; }
    public int Tier1Count { get; set; }
    public int Tier2Count { get; set; }
    public int Tier3Count { get; set; }
    public int Tier4Count { get; set; }
    public int Tier5Count { get; set; }
    public int MergedCount { get; set; }

    public bool AllFastDone => Tier1Done && Tier2Done;
    public bool AllMediumDone => AllFastDone && Tier3Done;
    public bool AllDone => AllMediumDone && Tier4Done;
    public bool Enriching => !AllDone;
    public int CompletedTierCount => (Tier1Done ? 1 : 0) + (Tier2Done ? 1 : 0) +
                                     (Tier3Done ? 1 : 0) + (Tier4Done ? 1 : 0) + (Tier5Done ? 1 : 0);
}
