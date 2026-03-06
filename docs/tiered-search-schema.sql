-- =============================================================================
-- Discovery City: Tiered Search Cache Schema
-- =============================================================================
-- Design principles:
--   - One dedicated table per search tier (different fields, independent reset)
--   - QueryHash links all tiers for a given search
--   - SearchQueries is the coordination table (tracks tier completion state)
--   - Mirrors PostgreSQL structure for future SQLite WASM client-side cache
--   - RRF merge runs 3 times: first-return, mid-point, all-complete
-- =============================================================================

-- ---------------------------------------------------------------------------
-- 1. SEARCH QUERY COORDINATION TABLE
-- ---------------------------------------------------------------------------
-- One row per unique search. Tracks which tiers are done and when to re-merge.
-- The API polls this to know if enrichment is still in progress.

CREATE TABLE IF NOT EXISTS SearchQueries (
    Id              SERIAL PRIMARY KEY,
    QueryHash       TEXT NOT NULL UNIQUE,       -- SHA256(query|exactMatch|datasets|names|filenameOnly)
    QueryText       TEXT,                        -- original search text (for debugging/display)
    ExactMatch      BOOLEAN NOT NULL DEFAULT FALSE,
    FilenameOnly    BOOLEAN NOT NULL DEFAULT FALSE,
    DataSetFilter   TEXT[],                      -- array of dataset names, NULL = all
    NamesFilter     TEXT[],                      -- array of name filters, NULL = all

    -- Tier completion tracking
    Tier1Done       BOOLEAN NOT NULL DEFAULT FALSE,  -- filename
    Tier2Done       BOOLEAN NOT NULL DEFAULT FALSE,  -- metadata
    Tier3Done       BOOLEAN NOT NULL DEFAULT FALSE,  -- full-text
    Tier4Done       BOOLEAN NOT NULL DEFAULT FALSE,  -- vector
    Tier5Done       BOOLEAN NOT NULL DEFAULT FALSE,  -- grammar (future)

    -- RRF merge checkpoints
    MergeCount      INT NOT NULL DEFAULT 0,          -- how many merges done (0,1,2,3)
    LastMergeAt     TIMESTAMPTZ,

    -- Counts per tier (for TotalCount estimation before all tiers finish)
    Tier1Count      INT NOT NULL DEFAULT 0,
    Tier2Count      INT NOT NULL DEFAULT 0,
    Tier3Count      INT NOT NULL DEFAULT 0,
    Tier4Count      INT NOT NULL DEFAULT 0,
    Tier5Count      INT NOT NULL DEFAULT 0,
    MergedCount     INT NOT NULL DEFAULT 0,          -- deduplicated total after last merge

    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    ExpiresAt       TIMESTAMPTZ NOT NULL DEFAULT NOW() + INTERVAL '30 minutes'
);

CREATE INDEX IF NOT EXISTS idx_sq_hash ON SearchQueries(QueryHash);
CREATE INDEX IF NOT EXISTS idx_sq_expires ON SearchQueries(ExpiresAt);


-- ---------------------------------------------------------------------------
-- 2. TIER 1: FILENAME MATCHES (fastest, ~10ms)
-- ---------------------------------------------------------------------------
-- Simple ILIKE on ParentDocuments.FileName
-- Minimal fields: just the match and a similarity indicator

CREATE TABLE IF NOT EXISTS SearchTier1_Filename (
    Id              SERIAL PRIMARY KEY,
    QueryHash       TEXT NOT NULL,
    DocumentId      INT NOT NULL,
    FileName        TEXT NOT NULL,
    Rank            INT NOT NULL,                -- position within this tier
    Similarity      REAL,                        -- pg_trgm similarity() score 0.0-1.0
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(QueryHash, DocumentId)
);

CREATE INDEX IF NOT EXISTS idx_t1_query ON SearchTier1_Filename(QueryHash, Rank);


-- ---------------------------------------------------------------------------
-- 3. TIER 2: METADATA MATCHES (fast, ~50ms)
-- ---------------------------------------------------------------------------
-- ILIKE on JSONB fields: Title, Names, Terms, DataSetName
-- Tracks WHICH field matched and the matched value (useful for highlighting)

CREATE TABLE IF NOT EXISTS SearchTier2_Metadata (
    Id              SERIAL PRIMARY KEY,
    QueryHash       TEXT NOT NULL,
    DocumentId      INT NOT NULL,
    MatchedField    TEXT NOT NULL,                -- 'Title', 'Names', 'Terms', 'DataSetName'
    MatchedValue    TEXT,                         -- the actual value that matched
    Rank            INT NOT NULL,
    Similarity      REAL,                        -- trigram similarity score
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(QueryHash, DocumentId, MatchedField)
);

CREATE INDEX IF NOT EXISTS idx_t2_query ON SearchTier2_Metadata(QueryHash, Rank);
CREATE INDEX IF NOT EXISTS idx_t2_doc ON SearchTier2_Metadata(QueryHash, DocumentId);


-- ---------------------------------------------------------------------------
-- 4. TIER 3: FULL-TEXT SEARCH MATCHES (medium, ~200ms)
-- ---------------------------------------------------------------------------
-- tsvector @@ tsquery on DocumentChunks.TextContent
-- Stores the matching chunk for snippet display + ts_rank score

CREATE TABLE IF NOT EXISTS SearchTier3_FullText (
    Id              SERIAL PRIMARY KEY,
    QueryHash       TEXT NOT NULL,
    DocumentId      INT NOT NULL,
    ChunkId         INT,                         -- which chunk matched best
    ChunkIndex      INT,                         -- ordinal within document
    MatchingChunks  INT NOT NULL DEFAULT 1,      -- COUNT(*) of matching chunks in this doc
    Snippet         TEXT,                        -- best matching chunk text (for preview)
    TsRank          REAL NOT NULL,               -- ts_rank score
    Rank            INT NOT NULL,
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(QueryHash, DocumentId)
);

CREATE INDEX IF NOT EXISTS idx_t3_query ON SearchTier3_FullText(QueryHash, Rank);


-- ---------------------------------------------------------------------------
-- 5. TIER 4: VECTOR SIMILARITY MATCHES (slow, ~500ms)
-- ---------------------------------------------------------------------------
-- Embedding <=> queryVector cosine distance on DocumentChunks
-- Stores distance and the matching chunk for context

CREATE TABLE IF NOT EXISTS SearchTier4_Vector (
    Id              SERIAL PRIMARY KEY,
    QueryHash       TEXT NOT NULL,
    DocumentId      INT NOT NULL,
    ChunkId         INT,                         -- which chunk was nearest
    ChunkIndex      INT,                         -- ordinal within document
    Distance        REAL NOT NULL,               -- cosine distance (0 = identical, 2 = opposite)
    Snippet         TEXT,                        -- nearest chunk text
    Rank            INT NOT NULL,
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(QueryHash, DocumentId)
);

CREATE INDEX IF NOT EXISTS idx_t4_query ON SearchTier4_Vector(QueryHash, Rank);


-- ---------------------------------------------------------------------------
-- 6. TIER 5: GRAMMAR / LINGUISTIC MATCHES (future, variable speed)
-- ---------------------------------------------------------------------------
-- Pattern-based: regex, ontology, entity extraction, grammar rules
-- Can be run selectively on subsets (e.g., specific datasets/books)
-- Has its own scope fields for partial re-processing

CREATE TABLE IF NOT EXISTS SearchTier5_Grammar (
    Id              SERIAL PRIMARY KEY,
    QueryHash       TEXT NOT NULL,
    DocumentId      INT NOT NULL,
    MatchType       TEXT NOT NULL,                -- 'regex', 'ontology', 'entity', 'grammar_rule'
    RuleName        TEXT,                         -- which grammar rule matched
    MatchedText     TEXT,                         -- the actual matched span
    MatchPosition   INT,                          -- char offset in document
    Confidence      REAL,                         -- 0.0-1.0 confidence
    Rank            INT NOT NULL,
    ScopeDataSetId  INT,                          -- if search was scoped to specific dataset
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(QueryHash, DocumentId, MatchType, MatchPosition)
);

CREATE INDEX IF NOT EXISTS idx_t5_query ON SearchTier5_Grammar(QueryHash, Rank);
CREATE INDEX IF NOT EXISTS idx_t5_scope ON SearchTier5_Grammar(ScopeDataSetId);


-- ---------------------------------------------------------------------------
-- 7. MERGED RESULTS TABLE (RRF output)
-- ---------------------------------------------------------------------------
-- After each merge checkpoint, the RRF-combined ranking is written here.
-- This is what the API actually reads for paged results.
-- Re-written on each merge (merge 1 = partial, merge 2 = mid, merge 3 = final).

CREATE TABLE IF NOT EXISTS SearchResultsMerged (
    Id              SERIAL PRIMARY KEY,
    QueryHash       TEXT NOT NULL,
    DocumentId      INT NOT NULL,
    FinalRank       INT NOT NULL,                -- RRF-combined rank (1 = best)
    RrfScore        DOUBLE PRECISION NOT NULL,   -- combined RRF score

    -- Per-tier contribution (NULL if not yet available at merge time)
    Tier1Rank       INT,
    Tier2Rank       INT,
    Tier3Rank       INT,
    Tier4Rank       INT,
    Tier5Rank       INT,

    -- Best snippet across tiers (for preview without re-querying chunks)
    BestSnippet     TEXT,
    SnippetSource   TEXT,                        -- which tier provided the snippet: 'fts', 'vector'

    MergeVersion    INT NOT NULL DEFAULT 1,       -- 1=first, 2=mid, 3=final
    CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(QueryHash, DocumentId)
);

CREATE INDEX IF NOT EXISTS idx_merged_query ON SearchResultsMerged(QueryHash, FinalRank);
CREATE INDEX IF NOT EXISTS idx_merged_page ON SearchResultsMerged(QueryHash, FinalRank)
    INCLUDE (DocumentId, RrfScore);              -- covering index for paged reads


-- ---------------------------------------------------------------------------
-- 8. CACHE MAINTENANCE
-- ---------------------------------------------------------------------------

-- Expire old search results (run periodically or on new search)
-- DELETE FROM SearchQueries WHERE ExpiresAt < NOW();
-- Cascade approach: each tier table references QueryHash, bulk delete by hash.

-- Per-tier reset (e.g., re-run grammar on DataSet 10 only):
-- DELETE FROM SearchTier5_Grammar WHERE ScopeDataSetId = 10;
-- UPDATE SearchQueries SET Tier5Done = FALSE WHERE ...;

-- Full reset for a query:
-- DELETE FROM SearchQueries WHERE QueryHash = @hash;
-- (then delete from all tier tables by QueryHash)


-- ---------------------------------------------------------------------------
-- 9. HELPER: CLEANUP FUNCTION
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION cleanup_expired_searches() RETURNS void AS $$
DECLARE
    expired_hashes TEXT[];
BEGIN
    SELECT array_agg(QueryHash) INTO expired_hashes
    FROM SearchQueries WHERE ExpiresAt < NOW();

    IF expired_hashes IS NOT NULL THEN
        DELETE FROM SearchTier1_Filename  WHERE QueryHash = ANY(expired_hashes);
        DELETE FROM SearchTier2_Metadata  WHERE QueryHash = ANY(expired_hashes);
        DELETE FROM SearchTier3_FullText  WHERE QueryHash = ANY(expired_hashes);
        DELETE FROM SearchTier4_Vector    WHERE QueryHash = ANY(expired_hashes);
        DELETE FROM SearchTier5_Grammar   WHERE QueryHash = ANY(expired_hashes);
        DELETE FROM SearchResultsMerged   WHERE QueryHash = ANY(expired_hashes);
        DELETE FROM SearchQueries         WHERE ExpiresAt < NOW();
    END IF;
END;
$$ LANGUAGE plpgsql;


-- ---------------------------------------------------------------------------
-- 10. RRF MERGE AS SQL (can also be done in C#)
-- ---------------------------------------------------------------------------
-- This query merges all available tiers using RRF with k=60.
-- Run after each checkpoint (tiers 1+2 done, tiers 1-3 done, all done).

-- Example: merge all available tiers for a given query hash
/*
WITH tier_ranks AS (
    SELECT DocumentId, Rank as TierRank, 'tier1' as Source FROM SearchTier1_Filename  WHERE QueryHash = @hash
    UNION ALL
    SELECT DocumentId, MIN(Rank), 'tier2' FROM SearchTier2_Metadata  WHERE QueryHash = @hash GROUP BY DocumentId
    UNION ALL
    SELECT DocumentId, Rank, 'tier3' FROM SearchTier3_FullText  WHERE QueryHash = @hash
    UNION ALL
    SELECT DocumentId, Rank, 'tier4' FROM SearchTier4_Vector    WHERE QueryHash = @hash
    UNION ALL
    SELECT DocumentId, Rank, 'tier5' FROM SearchTier5_Grammar   WHERE QueryHash = @hash
),
rrf_scores AS (
    SELECT DocumentId,
           SUM(1.0 / (60 + TierRank + 1)) as RrfScore
    FROM tier_ranks
    GROUP BY DocumentId
),
ranked AS (
    SELECT DocumentId, RrfScore,
           ROW_NUMBER() OVER (ORDER BY RrfScore DESC) as FinalRank
    FROM rrf_scores
)
INSERT INTO SearchResultsMerged (QueryHash, DocumentId, FinalRank, RrfScore, MergeVersion)
SELECT @hash, DocumentId, FinalRank, RrfScore, @mergeVersion
FROM ranked
ON CONFLICT (QueryHash, DocumentId)
DO UPDATE SET FinalRank = EXCLUDED.FinalRank,
              RrfScore = EXCLUDED.RrfScore,
              MergeVersion = EXCLUDED.MergeVersion,
              CreatedAt = NOW();
*/
