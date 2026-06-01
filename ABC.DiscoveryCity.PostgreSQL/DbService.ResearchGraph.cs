using ABC.DiscoveryCity.Words.Common.Research;
using Npgsql;
using System.Text.Json;

namespace ABC.DiscoveryCity.PostgreSQL;

public partial class DbService
{
    /// <summary>
    /// Creates the first-class research graph tables used for people, places,
    /// aliases, associations, evidence, and researcher-entered findings.
    /// This is additive and does not modify the source document corpus.
    /// </summary>
    public void InitResearchGraphTables()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(ResearchGraphSchemaSql, conn);
        cmd.CommandTimeout = 120;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Scans a document for people names and optionally persists candidate
    /// entities, mentions, and co-mentioned relationship evidence.
    /// </summary>
    public ResearchDocumentNameScanResult ScanDocumentForResearchNames(
        Guid documentId,
        ResearchDocumentScanOptions? options = null)
    {
        options ??= new ResearchDocumentScanOptions();
        InitResearchGraphTables();

        var source = LoadResearchScanDocument(documentId)
            ?? throw new KeyNotFoundException($"Document not found: {documentId}");

        var rawScan = ResearchNameScanner.ScanSentences(source.Sentences, options.MaxCharacters);
        var candidates = rawScan.Candidates
            .Where(c => c.Confidence >= options.MinimumCandidateConfidence)
            .ToList();
        var candidateNames = candidates
            .Select(c => c.NormalizedName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scan = rawScan with
        {
            Candidates = candidates,
            Mentions = rawScan.Mentions
                .Where(m => candidateNames.Contains(m.NormalizedMention))
                .ToList()
        };

        var result = new ResearchDocumentNameScanResult
        {
            DocumentId = documentId,
            FileName = source.FileName,
            FilePath = source.FilePath,
            ExtractorVersion = rawScan.ExtractorVersion,
            SentenceCount = rawScan.SentenceCount,
            CandidateNameCount = scan.Candidates.Count,
            MentionCount = scan.Mentions.Count,
            Candidates = scan.Candidates,
            Persisted = options.Persist
        };

        if (!options.Persist || scan.Mentions.Count == 0)
            return result;

        PersistResearchNameScan(source, scan, options, result);
        return result;
    }

    public ResearchDocumentNameScanResult ScanDocumentForResearchNamesByFilePath(
        string filePath,
        ResearchDocumentScanOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var documentId = DocumentExists(filePath, dataSetId: null)
            ?? throw new KeyNotFoundException($"Document not found for path: {filePath}");

        options ??= new ResearchDocumentScanOptions();
        if (string.IsNullOrWhiteSpace(options.SourceRef))
            options = options with { SourceRef = filePath };

        return ScanDocumentForResearchNames(documentId, options);
    }

    public ResearchDocumentNameScanResult GetPersistedResearchNames(Guid documentId)
    {
        InitResearchGraphTables();

        var source = LoadResearchScanDocumentSummary(documentId)
            ?? throw new KeyNotFoundException($"Document not found: {documentId}");

        using var conn = _dataSource.OpenConnection();
        var candidates = new List<ResearchNameCandidate>();

        using (var cmd = new NpgsqlCommand(@"
            SELECT
                COALESCE(NULLIF(MAX(e.DisplayName), ''), NULLIF(MAX(e.CanonicalName), ''), MIN(m.RawMention)) AS DisplayName,
                m.NormalizedMention,
                MAX(m.Confidence)::numeric AS Confidence,
                COUNT(*)::int AS MentionCount,
                STRING_AGG(DISTINCT COALESCE(m.Metadata->>'SourcePattern', m.SourceKind), ', ') AS Sources
            FROM ResearchEntityMentions m
            LEFT JOIN ResearchEntities e ON e.Id = m.EntityId
            WHERE m.DocumentId = @documentId
              AND m.MentionType = 'name'
            GROUP BY m.NormalizedMention
            ORDER BY MAX(m.Confidence) DESC, COUNT(*) DESC, DisplayName;", conn))
        {
            cmd.Parameters.AddWithValue("documentId", documentId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var sources = reader.IsDBNull(4)
                    ? new List<string>()
                    : reader.GetString(4)
                        .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                candidates.Add(new ResearchNameCandidate(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? 0m : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    sources));
            }
        }

        var relationshipEvidenceCount = 0;
        using (var evidenceCmd = new NpgsqlCommand(@"
            SELECT COUNT(*)::int
            FROM ResearchRelationshipEvidence
            WHERE DocumentId = @documentId;", conn))
        {
            evidenceCmd.Parameters.AddWithValue("documentId", documentId);
            relationshipEvidenceCount = Convert.ToInt32(evidenceCmd.ExecuteScalar() ?? 0);
        }

        var mentionCount = candidates.Sum(c => c.MentionCount);
        return new ResearchDocumentNameScanResult
        {
            DocumentId = documentId,
            FileName = source.FileName,
            FilePath = source.FilePath,
            SentenceCount = source.SentenceCount,
            CandidateNameCount = candidates.Count,
            MentionCount = mentionCount,
            Persisted = true,
            MentionsInserted = mentionCount,
            RelationshipEvidenceInserted = relationshipEvidenceCount,
            Candidates = candidates
        };
    }

    public ResearchDocumentNameScanResult GetPersistedResearchNamesByFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var documentId = DocumentExists(filePath, dataSetId: null)
            ?? throw new KeyNotFoundException($"Document not found for path: {filePath}");

        return GetPersistedResearchNames(documentId);
    }

    public ResearchNameResearchView GetNameResearchView(Guid documentId)
    {
        InitResearchGraphTables();

        var source = LoadResearchScanDocumentSummary(documentId)
            ?? throw new KeyNotFoundException($"Document not found: {documentId}");

        using var conn = _dataSource.OpenConnection();
        var peopleByEntityId = new Dictionary<Guid, ResearchPersonGroup>();

        using (var cmd = new NpgsqlCommand(@"
            SELECT
                e.Id,
                e.CanonicalName,
                COALESCE(NULLIF(e.DisplayName, ''), e.CanonicalName) AS DisplayName,
                e.NormalizedName,
                e.Confidence,
                e.Status,
                m.Id,
                m.RawMention,
                m.NormalizedMention,
                m.SentenceOrdinal,
                m.PageNumber,
                m.Confidence,
                COALESCE(m.Metadata->>'SourcePattern', m.SourceKind) AS SourcePattern,
                m.Metadata->>'SentenceText' AS SentenceText
            FROM ResearchEntityMentions m
            JOIN ResearchEntities e ON e.Id = m.EntityId
            WHERE m.DocumentId = @documentId
              AND e.EntityType = 'person'
              AND m.MentionType = 'name'
            ORDER BY e.CanonicalName, m.SentenceOrdinal NULLS LAST, m.CharacterStart NULLS LAST;", conn))
        {
            cmd.Parameters.AddWithValue("documentId", documentId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var entityId = reader.GetGuid(0);
                if (!peopleByEntityId.TryGetValue(entityId, out var person))
                {
                    person = new ResearchPersonGroup
                    {
                        EntityId = entityId,
                        CanonicalName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        DisplayName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        NormalizedName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Confidence = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4),
                        Status = reader.IsDBNull(5) ? "" : reader.GetString(5)
                    };
                    peopleByEntityId.Add(entityId, person);
                }

                person.Mentions.Add(new ResearchPersonMention
                {
                    MentionId = reader.GetGuid(6),
                    RawMention = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    NormalizedMention = reader.IsDBNull(8) ? "" : reader.GetString(8),
                    SentenceOrdinal = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    PageNumber = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    Confidence = reader.IsDBNull(11) ? 0m : reader.GetDecimal(11),
                    SourcePattern = reader.IsDBNull(12) ? "" : reader.GetString(12),
                    SentenceText = reader.IsDBNull(13) ? null : reader.GetString(13)
                });
            }
        }

        if (peopleByEntityId.Count > 0)
        {
            using var aliasCmd = new NpgsqlCommand(@"
                SELECT Id, EntityId, Alias, NormalizedAlias, AliasType, IsPrimary, Confidence, SourceKind
                FROM ResearchEntityAliases
                WHERE EntityId = ANY(@entityIds)
                ORDER BY IsPrimary DESC, Confidence DESC, Alias;", conn);
            aliasCmd.Parameters.AddWithValue("entityIds", peopleByEntityId.Keys.ToArray());

            using var aliasReader = aliasCmd.ExecuteReader();
            while (aliasReader.Read())
            {
                var entityId = aliasReader.GetGuid(1);
                if (!peopleByEntityId.TryGetValue(entityId, out var person))
                    continue;

                person.Aliases.Add(new ResearchPersonAlias
                {
                    AliasId = aliasReader.GetGuid(0),
                    Alias = aliasReader.IsDBNull(2) ? "" : aliasReader.GetString(2),
                    NormalizedAlias = aliasReader.IsDBNull(3) ? "" : aliasReader.GetString(3),
                    AliasType = aliasReader.IsDBNull(4) ? "" : aliasReader.GetString(4),
                    IsPrimary = !aliasReader.IsDBNull(5) && aliasReader.GetBoolean(5),
                    Confidence = aliasReader.IsDBNull(6) ? 0m : aliasReader.GetDecimal(6),
                    SourceKind = aliasReader.IsDBNull(7) ? "" : aliasReader.GetString(7)
                });
            }
        }

        if (peopleByEntityId.Count > 0)
        {
            using var linkCmd = new NpgsqlCommand(@"
                SELECT EntityId, LinkedName, Confidence
                FROM (
                    SELECT
                        r.FromEntityId AS EntityId,
                        COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS LinkedName,
                        r.Confidence
                    FROM ResearchRelationships r
                    JOIN ResearchEntities other ON other.Id = r.ToEntityId
                    WHERE r.RelationshipType = 'name_link'
                      AND r.Status <> 'archived'
                      AND r.FromEntityId = ANY(@entityIds)
                    UNION ALL
                    SELECT
                        r.ToEntityId AS EntityId,
                        COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS LinkedName,
                        r.Confidence
                    FROM ResearchRelationships r
                    JOIN ResearchEntities other ON other.Id = r.FromEntityId
                    WHERE r.RelationshipType = 'name_link'
                      AND r.Status <> 'archived'
                      AND r.ToEntityId = ANY(@entityIds)
                ) linked
                ORDER BY LinkedName;", conn);
            linkCmd.Parameters.AddWithValue("entityIds", peopleByEntityId.Keys.ToArray());

            using var linkReader = linkCmd.ExecuteReader();
            while (linkReader.Read())
            {
                var entityId = linkReader.GetGuid(0);
                if (!peopleByEntityId.TryGetValue(entityId, out var person))
                    continue;

                var linkedName = linkReader.IsDBNull(1) ? "" : linkReader.GetString(1);
                var confidence = linkReader.IsDBNull(2) ? 0m : linkReader.GetDecimal(2);
                if (!string.IsNullOrWhiteSpace(linkedName))
                    person.LinkedNames.Add($"{linkedName} [{confidence:P0}]");
            }
        }

        var people = peopleByEntityId.Values
            .OrderByDescending(p => p.MentionCount)
            .ThenByDescending(p => p.Confidence)
            .ThenBy(p => p.DisplayName)
            .ToList();

        return new ResearchNameResearchView
        {
            DocumentId = documentId,
            FileName = source.FileName,
            FilePath = source.FilePath,
            SentenceCount = source.SentenceCount,
            PersonCount = people.Count,
            AliasCount = people.Sum(p => p.AliasCount),
            MentionCount = people.Sum(p => p.MentionCount),
            People = people
        };
    }

    public ResearchNameResearchView GetNameResearchViewByFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var documentId = DocumentExists(filePath, dataSetId: null)
            ?? throw new KeyNotFoundException($"Document not found for path: {filePath}");

        return GetNameResearchView(documentId);
    }

    public ResearchPersonAlias AddResearchPersonAlias(
        Guid entityId,
        string alias,
        string aliasType = "",
        bool isPrimary = false,
        decimal confidence = 1.0m,
        string sourceKind = "researcher")
    {
        if (entityId == Guid.Empty)
            throw new ArgumentException("Entity id is required.", nameof(entityId));
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Alias is required.", nameof(alias));

        InitResearchGraphTables();

        var normalizedAlias = ResearchNameScanner.NormalizeName(alias);
        if (string.IsNullOrWhiteSpace(normalizedAlias))
            throw new ArgumentException("Alias is not a valid name-like value.", nameof(alias));

        var cleanAliasType = string.IsNullOrWhiteSpace(aliasType)
            ? isPrimary ? "name" : "Name Link"
            : aliasType.Trim();
        var cleanSourceKind = string.IsNullOrWhiteSpace(sourceKind) ? "researcher" : sourceKind.Trim();
        var cleanConfidence = Math.Clamp(confidence, 0m, 1m);

        using var conn = _dataSource.OpenConnection();
        using var trans = conn.BeginTransaction();

        try
        {
            using (var entityCmd = new NpgsqlCommand(@"
                SELECT 1
                FROM ResearchEntities
                WHERE Id = @entityId AND EntityType = 'person';", conn, trans))
            {
                entityCmd.Parameters.AddWithValue("entityId", entityId);
                if (entityCmd.ExecuteScalar() == null)
                    throw new KeyNotFoundException($"Person entity not found: {entityId}");
            }

            if (isPrimary)
            {
                using var clearPrimaryCmd = new NpgsqlCommand(@"
                    UPDATE ResearchEntityAliases
                    SET IsPrimary = FALSE
                    WHERE EntityId = @entityId;", conn, trans);
                clearPrimaryCmd.Parameters.AddWithValue("entityId", entityId);
                clearPrimaryCmd.ExecuteNonQuery();
            }

            Guid aliasId;
            using (var existingCmd = new NpgsqlCommand(@"
                SELECT Id
                FROM ResearchEntityAliases
                WHERE EntityId = @entityId AND NormalizedAlias = @normalizedAlias
                ORDER BY CreatedAt
                LIMIT 1;", conn, trans))
            {
                existingCmd.Parameters.AddWithValue("entityId", entityId);
                existingCmd.Parameters.AddWithValue("normalizedAlias", normalizedAlias);
                var existing = existingCmd.ExecuteScalar();
                if (existing is Guid existingId)
                {
                    aliasId = existingId;
                    if (isPrimary)
                    {
                        using var primaryCmd = new NpgsqlCommand(@"
                            UPDATE ResearchEntityAliases
                            SET IsPrimary = TRUE,
                                AliasType = @aliasType,
                                Confidence = GREATEST(Confidence, @confidence),
                                SourceKind = @sourceKind
                            WHERE Id = @aliasId;", conn, trans);
                        primaryCmd.Parameters.AddWithValue("aliasId", aliasId);
                        primaryCmd.Parameters.AddWithValue("aliasType", cleanAliasType);
                        primaryCmd.Parameters.AddWithValue("confidence", cleanConfidence);
                        primaryCmd.Parameters.AddWithValue("sourceKind", cleanSourceKind);
                        primaryCmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    using var cmd = new NpgsqlCommand(@"
                        INSERT INTO ResearchEntityAliases
                            (EntityId, Alias, NormalizedAlias, AliasType, IsPrimary, Confidence, SourceKind)
                        VALUES
                            (@entityId, @alias, @normalizedAlias, @aliasType, @isPrimary, @confidence, @sourceKind)
                        RETURNING Id;", conn, trans);
                    cmd.Parameters.AddWithValue("entityId", entityId);
                    cmd.Parameters.AddWithValue("alias", alias.Trim());
                    cmd.Parameters.AddWithValue("normalizedAlias", normalizedAlias);
                    cmd.Parameters.AddWithValue("aliasType", cleanAliasType);
                    cmd.Parameters.AddWithValue("isPrimary", isPrimary);
                    cmd.Parameters.AddWithValue("confidence", cleanConfidence);
                    cmd.Parameters.AddWithValue("sourceKind", cleanSourceKind);
                    aliasId = (Guid)cmd.ExecuteScalar()!;
                }
            }

            trans.Commit();
            return new ResearchPersonAlias
            {
                AliasId = aliasId,
                Alias = alias.Trim(),
                NormalizedAlias = normalizedAlias,
                AliasType = cleanAliasType,
                IsPrimary = isPrimary,
                Confidence = cleanConfidence,
                SourceKind = cleanSourceKind
            };
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    public List<ResearchPersonAdminRow> GetResearchPeopleAdmin(string? search = null, int take = 250)
    {
        InitResearchGraphTables();

        var normalizedSearch = ResearchNameScanner.NormalizeName(search ?? "");
        var trimmedSearch = search?.Trim() ?? "";
        var limit = Math.Clamp(take, 1, 1000);
        var people = new List<ResearchPersonAdminRow>();

        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT
                e.Id,
                e.CanonicalName,
                COALESCE(NULLIF(e.DisplayName, ''), e.CanonicalName) AS DisplayName,
                e.NormalizedName,
                e.Confidence,
                e.Status,
                p.Notes,
                COALESCE(a.AliasCount, 0) AS AliasCount,
                COALESCE(a.AliasSummary, '') AS AliasSummary,
                COALESCE(m.MentionCount, 0) AS MentionCount,
                COALESCE(m.DocumentCount, 0) AS DocumentCount,
                COALESCE(l.LinkedNameCount, 0) AS LinkedNameCount,
                COALESCE(l.LinkedNameSummary, '') AS LinkedNameSummary
            FROM ResearchEntities e
            LEFT JOIN ResearchPeople p ON p.EntityId = e.Id
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int AS AliasCount,
                       STRING_AGG(
                           Alias || ' [' ||
                           CASE
                               WHEN IsPrimary OR lower(AliasType) = 'name' THEN 'Proper Name'
                               ELSE AliasType
                           END || ', ' || ROUND(Confidence * 100)::int || '%]',
                           ', ' ORDER BY IsPrimary DESC, Confidence DESC, Alias) AS AliasSummary
                FROM ResearchEntityAliases
                WHERE EntityId = e.Id
            ) a ON TRUE
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int AS MentionCount,
                       COUNT(DISTINCT DocumentId)::int AS DocumentCount
                FROM ResearchEntityMentions
                WHERE EntityId = e.Id
            ) m ON TRUE
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int AS LinkedNameCount,
                       STRING_AGG(LinkedName || ' [' || ROUND(Confidence * 100)::int || '%]', ', ' ORDER BY LinkedName) AS LinkedNameSummary
                FROM (
                    SELECT COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS LinkedName,
                           r.Confidence
                    FROM ResearchRelationships r
                    JOIN ResearchEntities other ON other.Id = r.ToEntityId
                    WHERE r.RelationshipType = 'name_link'
                      AND r.Status <> 'archived'
                      AND r.FromEntityId = e.Id
                    UNION ALL
                    SELECT COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS LinkedName,
                           r.Confidence
                    FROM ResearchRelationships r
                    JOIN ResearchEntities other ON other.Id = r.FromEntityId
                    WHERE r.RelationshipType = 'name_link'
                      AND r.Status <> 'archived'
                      AND r.ToEntityId = e.Id
                ) linked
            ) l ON TRUE
            WHERE e.EntityType = 'person'
              AND (
                    @search = ''
                    OR e.NormalizedName LIKE @normalizedSearch
                    OR e.DisplayName ILIKE @displaySearch
                    OR e.CanonicalName ILIKE @displaySearch
                    OR EXISTS (
                        SELECT 1
                        FROM ResearchEntityAliases ea
                        WHERE ea.EntityId = e.Id
                          AND (ea.NormalizedAlias LIKE @normalizedSearch OR ea.Alias ILIKE @displaySearch)
                    )
              )
            ORDER BY COALESCE(m.MentionCount, 0) DESC, COALESCE(a.AliasCount, 0) DESC, DisplayName
            LIMIT @limit;", conn);
        cmd.Parameters.AddWithValue("search", trimmedSearch);
        cmd.Parameters.AddWithValue("normalizedSearch", $"%{normalizedSearch}%");
        cmd.Parameters.AddWithValue("displaySearch", $"%{trimmedSearch}%");
        cmd.Parameters.AddWithValue("limit", limit);

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                people.Add(ReadResearchPersonAdminRow(reader));
            }
        }

        PopulateResearchPersonAdminLinkedNames(conn, people);
        return people;
    }

    public ResearchPersonAdminRow CreateResearchPerson(
        string displayName,
        string? notes = null,
        string status = "verified",
        decimal confidence = 1.0m,
        string sourceKind = "researcher")
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        InitResearchGraphTables();

        var normalizedName = ResearchNameScanner.NormalizeName(displayName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new ArgumentException("Display name is not a valid name-like value.", nameof(displayName));

        var cleanStatus = CleanResearchEntityStatus(status, defaultStatus: "verified");
        var cleanSourceKind = string.IsNullOrWhiteSpace(sourceKind) ? "researcher" : sourceKind.Trim();

        using var conn = _dataSource.OpenConnection();
        using var trans = conn.BeginTransaction();
        Guid entityId;
        try
        {
            using (var existingCmd = new NpgsqlCommand(@"
                SELECT Id
                FROM ResearchEntities
                WHERE EntityType = 'person' AND NormalizedName = @normalizedName
                ORDER BY CreatedAt
                LIMIT 1;", conn, trans))
            {
                existingCmd.Parameters.AddWithValue("normalizedName", normalizedName);
                var existing = existingCmd.ExecuteScalar();
                if (existing is Guid existingId)
                {
                    entityId = existingId;
                }
                else
                {
                    using var insertCmd = new NpgsqlCommand(@"
                        INSERT INTO ResearchEntities
                            (EntityType, CanonicalName, NormalizedName, DisplayName, Confidence, Status, SourceKind)
                        VALUES
                            ('person', @displayName, @normalizedName, @displayName, @confidence, @status, @sourceKind)
                        RETURNING Id;", conn, trans);
                    insertCmd.Parameters.AddWithValue("displayName", displayName.Trim());
                    insertCmd.Parameters.AddWithValue("normalizedName", normalizedName);
                    insertCmd.Parameters.AddWithValue("confidence", Math.Clamp(confidence, 0m, 1m));
                    insertCmd.Parameters.AddWithValue("status", cleanStatus);
                    insertCmd.Parameters.AddWithValue("sourceKind", cleanSourceKind);
                    entityId = (Guid)insertCmd.ExecuteScalar()!;
                }
            }

            UpsertResearchPersonDetails(conn, trans, entityId, displayName.Trim());
            UpdateResearchPersonNotes(conn, trans, entityId, notes);
            UpsertResearchAlias(conn, trans, entityId, displayName.Trim(), normalizedName, Math.Clamp(confidence, 0m, 1m), cleanSourceKind, createdBy: null);

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }

        return GetResearchPersonAdmin(entityId) ?? throw new KeyNotFoundException($"Person entity not found after create: {entityId}");
    }

    public ResearchPersonAdminRow UpdateResearchPerson(
        Guid entityId,
        string displayName,
        string status,
        string? notes = null)
    {
        if (entityId == Guid.Empty)
            throw new ArgumentException("Entity id is required.", nameof(entityId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.", nameof(displayName));

        InitResearchGraphTables();

        var normalizedName = ResearchNameScanner.NormalizeName(displayName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new ArgumentException("Display name is not a valid name-like value.", nameof(displayName));

        var cleanStatus = CleanResearchEntityStatus(status, defaultStatus: "candidate");

        using var conn = _dataSource.OpenConnection();
        using var trans = conn.BeginTransaction();
        try
        {
            using (var updateCmd = new NpgsqlCommand(@"
                UPDATE ResearchEntities
                SET CanonicalName = @displayName,
                    DisplayName = @displayName,
                    NormalizedName = @normalizedName,
                    Status = @status,
                    UpdatedAt = NOW()
                WHERE Id = @entityId AND EntityType = 'person';", conn, trans))
            {
                updateCmd.Parameters.AddWithValue("entityId", entityId);
                updateCmd.Parameters.AddWithValue("displayName", displayName.Trim());
                updateCmd.Parameters.AddWithValue("normalizedName", normalizedName);
                updateCmd.Parameters.AddWithValue("status", cleanStatus);
                if (updateCmd.ExecuteNonQuery() == 0)
                    throw new KeyNotFoundException($"Person entity not found: {entityId}");
            }

            UpsertResearchPersonDetails(conn, trans, entityId, displayName.Trim());
            UpdateResearchPersonNotes(conn, trans, entityId, notes);
            UpsertResearchAlias(conn, trans, entityId, displayName.Trim(), normalizedName, 1.0m, "researcher", createdBy: null);

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }

        return GetResearchPersonAdmin(entityId) ?? throw new KeyNotFoundException($"Person entity not found after update: {entityId}");
    }

    public ResearchNameLink AddResearchNameLink(
        Guid sourceEntityId,
        Guid targetEntityId,
        decimal confidence = 1.0m,
        string sourceKind = "researcher")
    {
        if (sourceEntityId == Guid.Empty)
            throw new ArgumentException("Source entity id is required.", nameof(sourceEntityId));
        if (targetEntityId == Guid.Empty)
            throw new ArgumentException("Target entity id is required.", nameof(targetEntityId));
        if (sourceEntityId == targetEntityId)
            throw new ArgumentException("A name cannot link to itself.", nameof(targetEntityId));

        InitResearchGraphTables();

        var cleanConfidence = Math.Clamp(confidence, 0m, 1m);
        var cleanSourceKind = string.IsNullOrWhiteSpace(sourceKind) ? "researcher" : sourceKind.Trim();

        using var conn = _dataSource.OpenConnection();
        using var trans = conn.BeginTransaction();
        try
        {
            _ = GetResearchEntityDisplayName(conn, trans, sourceEntityId)
                ?? throw new KeyNotFoundException($"Source name not found: {sourceEntityId}");
            var targetName = GetResearchEntityDisplayName(conn, trans, targetEntityId)
                ?? throw new KeyNotFoundException($"Target name not found: {targetEntityId}");

            Guid relationshipId;
            using (var existingCmd = new NpgsqlCommand(@"
                SELECT Id
                FROM ResearchRelationships
                WHERE RelationshipType = 'name_link'
                  AND (
                      (FromEntityId = @sourceEntityId AND ToEntityId = @targetEntityId)
                      OR (FromEntityId = @targetEntityId AND ToEntityId = @sourceEntityId)
                  )
                ORDER BY CreatedAt
                LIMIT 1;", conn, trans))
            {
                existingCmd.Parameters.AddWithValue("sourceEntityId", sourceEntityId);
                existingCmd.Parameters.AddWithValue("targetEntityId", targetEntityId);
                var existing = existingCmd.ExecuteScalar();

                if (existing is Guid existingId)
                {
                    relationshipId = existingId;
                    using var updateCmd = new NpgsqlCommand(@"
                        UPDATE ResearchRelationships
                        SET Confidence = GREATEST(Confidence, @confidence),
                            Status = CASE WHEN Status = 'archived' THEN 'candidate' ELSE Status END,
                            UpdatedAt = NOW(),
                            SourceKind = @sourceKind
                        WHERE Id = @relationshipId;", conn, trans);
                    updateCmd.Parameters.AddWithValue("relationshipId", relationshipId);
                    updateCmd.Parameters.AddWithValue("confidence", cleanConfidence);
                    updateCmd.Parameters.AddWithValue("sourceKind", cleanSourceKind);
                    updateCmd.ExecuteNonQuery();
                }
                else
                {
                    var metadata = JsonSerializer.Serialize(new { linkedBy = cleanSourceKind });
                    using var insertCmd = new NpgsqlCommand(@"
                        INSERT INTO ResearchRelationships
                            (FromEntityId, ToEntityId, RelationshipType, Direction, Confidence, Status, SourceKind, Metadata)
                        VALUES
                            (@sourceEntityId, @targetEntityId, 'name_link', 'undirected', @confidence, 'candidate', @sourceKind, @metadata::jsonb)
                        RETURNING Id;", conn, trans);
                    insertCmd.Parameters.AddWithValue("sourceEntityId", sourceEntityId);
                    insertCmd.Parameters.AddWithValue("targetEntityId", targetEntityId);
                    insertCmd.Parameters.AddWithValue("confidence", cleanConfidence);
                    insertCmd.Parameters.AddWithValue("sourceKind", cleanSourceKind);
                    insertCmd.Parameters.AddWithValue("metadata", metadata);
                    relationshipId = (Guid)insertCmd.ExecuteScalar()!;
                }
            }

            trans.Commit();

            return new ResearchNameLink
            {
                RelationshipId = relationshipId,
                SourceEntityId = sourceEntityId,
                TargetEntityId = targetEntityId,
                TargetDisplayName = targetName,
                Confidence = cleanConfidence,
                Status = "candidate"
            };
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    public ResearchPersonMergeResult MergeResearchPeople(
        Guid sourceEntityId,
        Guid targetEntityId,
        string sourceKind = "researcher")
    {
        if (sourceEntityId == Guid.Empty)
            throw new ArgumentException("Source entity id is required.", nameof(sourceEntityId));
        if (targetEntityId == Guid.Empty)
            throw new ArgumentException("Target entity id is required.", nameof(targetEntityId));
        if (sourceEntityId == targetEntityId)
            throw new ArgumentException("Source and target must be different people.", nameof(targetEntityId));

        InitResearchGraphTables();

        using var conn = _dataSource.OpenConnection();
        using var trans = conn.BeginTransaction();
        try
        {
            var sourceName = GetResearchEntityDisplayName(conn, trans, sourceEntityId)
                ?? throw new KeyNotFoundException($"Source person entity not found: {sourceEntityId}");
            var targetName = GetResearchEntityDisplayName(conn, trans, targetEntityId)
                ?? throw new KeyNotFoundException($"Target person entity not found: {targetEntityId}");

            var normalizedSourceName = ResearchNameScanner.NormalizeName(sourceName);
            if (!string.IsNullOrWhiteSpace(normalizedSourceName))
                UpsertResearchAlias(conn, trans, targetEntityId, sourceName, normalizedSourceName, 1.0m, sourceKind, createdBy: null);

            var aliasesMoved = ExecuteCount(conn, trans, @"
                UPDATE ResearchEntityAliases a
                SET EntityId = @targetEntityId
                WHERE a.EntityId = @sourceEntityId
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ResearchEntityAliases targetAlias
                      WHERE targetAlias.EntityId = @targetEntityId
                        AND targetAlias.NormalizedAlias = a.NormalizedAlias
                  );", sourceEntityId, targetEntityId);

            var aliasesDeduped = ExecuteCount(conn, trans, @"
                DELETE FROM ResearchEntityAliases
                WHERE EntityId = @sourceEntityId;", sourceEntityId, targetEntityId);

            var mentionsMoved = ExecuteCount(conn, trans, @"
                UPDATE ResearchEntityMentions
                SET EntityId = @targetEntityId
                WHERE EntityId = @sourceEntityId;", sourceEntityId, targetEntityId);

            var findingsMoved = ExecuteCount(conn, trans, @"
                UPDATE ResearchFindingLinks
                SET EntityId = @targetEntityId
                WHERE EntityId = @sourceEntityId;", sourceEntityId, targetEntityId);

            var subjectAssertionsMoved = ExecuteCount(conn, trans, @"
                UPDATE ResearchAssertions
                SET SubjectEntityId = @targetEntityId,
                    UpdatedAt = NOW()
                WHERE SubjectEntityId = @sourceEntityId;", sourceEntityId, targetEntityId);

            var objectAssertionsMoved = ExecuteCount(conn, trans, @"
                UPDATE ResearchAssertions
                SET ObjectEntityId = @targetEntityId,
                    UpdatedAt = NOW()
                WHERE ObjectEntityId = @sourceEntityId;", sourceEntityId, targetEntityId);

            var fromRelationshipsMoved = ExecuteCount(conn, trans, @"
                UPDATE ResearchRelationships
                SET FromEntityId = @targetEntityId,
                    UpdatedAt = NOW()
                WHERE FromEntityId = @sourceEntityId
                  AND ToEntityId <> @targetEntityId;", sourceEntityId, targetEntityId);

            var toRelationshipsMoved = ExecuteCount(conn, trans, @"
                UPDATE ResearchRelationships
                SET ToEntityId = @targetEntityId,
                    UpdatedAt = NOW()
                WHERE ToEntityId = @sourceEntityId
                  AND FromEntityId <> @targetEntityId;", sourceEntityId, targetEntityId);

            var relationshipsRemoved = ExecuteCount(conn, trans, @"
                DELETE FROM ResearchRelationships
                WHERE FromEntityId = ToEntityId
                   OR FromEntityId = @sourceEntityId
                   OR ToEntityId = @sourceEntityId;", sourceEntityId, targetEntityId);

            var sourceMetadata = JsonSerializer.Serialize(new
            {
                mergedInto = targetEntityId,
                mergedIntoName = targetName,
                mergedAt = DateTimeOffset.UtcNow,
                sourceKind
            });

            using (var archiveCmd = new NpgsqlCommand(@"
                UPDATE ResearchEntities
                SET Status = 'archived',
                    UpdatedAt = NOW(),
                    Metadata = Metadata || @metadata::jsonb
                WHERE Id = @sourceEntityId;", conn, trans))
            {
                archiveCmd.Parameters.AddWithValue("sourceEntityId", sourceEntityId);
                archiveCmd.Parameters.AddWithValue("metadata", sourceMetadata);
                archiveCmd.ExecuteNonQuery();
            }

            trans.Commit();

            return new ResearchPersonMergeResult
            {
                SourceEntityId = sourceEntityId,
                TargetEntityId = targetEntityId,
                SourceDisplayName = sourceName,
                TargetDisplayName = targetName,
                AliasesMoved = aliasesMoved,
                AliasesDeduped = aliasesDeduped,
                MentionsMoved = mentionsMoved,
                RelationshipsMoved = fromRelationshipsMoved + toRelationshipsMoved,
                RelationshipsRemoved = relationshipsRemoved,
                FindingsMoved = findingsMoved,
                AssertionsMoved = subjectAssertionsMoved + objectAssertionsMoved
            };
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    public ResearchPersonAdminRow? GetResearchPersonAdmin(Guid entityId)
    {
        InitResearchGraphTables();

        using var conn = _dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(@"
            SELECT
                e.Id,
                e.CanonicalName,
                COALESCE(NULLIF(e.DisplayName, ''), e.CanonicalName) AS DisplayName,
                e.NormalizedName,
                e.Confidence,
                e.Status,
                p.Notes,
                COALESCE(a.AliasCount, 0) AS AliasCount,
                COALESCE(a.AliasSummary, '') AS AliasSummary,
                COALESCE(m.MentionCount, 0) AS MentionCount,
                COALESCE(m.DocumentCount, 0) AS DocumentCount,
                COALESCE(l.LinkedNameCount, 0) AS LinkedNameCount,
                COALESCE(l.LinkedNameSummary, '') AS LinkedNameSummary
            FROM ResearchEntities e
            LEFT JOIN ResearchPeople p ON p.EntityId = e.Id
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int AS AliasCount,
                       STRING_AGG(
                           Alias || ' [' ||
                           CASE
                               WHEN IsPrimary OR lower(AliasType) = 'name' THEN 'Proper Name'
                               ELSE AliasType
                           END || ', ' || ROUND(Confidence * 100)::int || '%]',
                           ', ' ORDER BY IsPrimary DESC, Confidence DESC, Alias) AS AliasSummary
                FROM ResearchEntityAliases
                WHERE EntityId = e.Id
            ) a ON TRUE
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int AS MentionCount,
                       COUNT(DISTINCT DocumentId)::int AS DocumentCount
                FROM ResearchEntityMentions
                WHERE EntityId = e.Id
            ) m ON TRUE
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int AS LinkedNameCount,
                       STRING_AGG(LinkedName || ' [' || ROUND(Confidence * 100)::int || '%]', ', ' ORDER BY LinkedName) AS LinkedNameSummary
                FROM (
                    SELECT COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS LinkedName,
                           r.Confidence
                    FROM ResearchRelationships r
                    JOIN ResearchEntities other ON other.Id = r.ToEntityId
                    WHERE r.RelationshipType = 'name_link'
                      AND r.Status <> 'archived'
                      AND r.FromEntityId = e.Id
                    UNION ALL
                    SELECT COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS LinkedName,
                           r.Confidence
                    FROM ResearchRelationships r
                    JOIN ResearchEntities other ON other.Id = r.FromEntityId
                    WHERE r.RelationshipType = 'name_link'
                      AND r.Status <> 'archived'
                      AND r.ToEntityId = e.Id
                ) linked
            ) l ON TRUE
            WHERE e.Id = @entityId AND e.EntityType = 'person';", conn);
        cmd.Parameters.AddWithValue("entityId", entityId);

        ResearchPersonAdminRow? person = null;
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
                person = ReadResearchPersonAdminRow(reader);
        }

        if (person != null)
            PopulateResearchPersonAdminLinkedNames(conn, new List<ResearchPersonAdminRow> { person });

        return person;
    }

    private static void PopulateResearchPersonAdminLinkedNames(NpgsqlConnection conn, List<ResearchPersonAdminRow> people)
    {
        if (people.Count == 0)
            return;

        var peopleById = people.ToDictionary(person => person.EntityId);
        var entityIds = peopleById.Keys.ToArray();

        foreach (var person in people)
        {
            person.LinkedNames.Clear();
        }

        using var cmd = new NpgsqlCommand(@"
            SELECT *
            FROM (
                SELECT
                    r.FromEntityId AS OwnerEntityId,
                    r.Id AS RelationshipId,
                    other.Id AS LinkedEntityId,
                    COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS DisplayName,
                    other.NormalizedName,
                    r.Confidence,
                    r.Status
                FROM ResearchRelationships r
                JOIN ResearchEntities other ON other.Id = r.ToEntityId
                WHERE r.RelationshipType = 'name_link'
                  AND r.Status <> 'archived'
                  AND r.FromEntityId = ANY(@entityIds)

                UNION ALL

                SELECT
                    r.ToEntityId AS OwnerEntityId,
                    r.Id AS RelationshipId,
                    other.Id AS LinkedEntityId,
                    COALESCE(NULLIF(other.DisplayName, ''), other.CanonicalName) AS DisplayName,
                    other.NormalizedName,
                    r.Confidence,
                    r.Status
                FROM ResearchRelationships r
                JOIN ResearchEntities other ON other.Id = r.FromEntityId
                WHERE r.RelationshipType = 'name_link'
                  AND r.Status <> 'archived'
                  AND r.ToEntityId = ANY(@entityIds)
            ) linked
            ORDER BY OwnerEntityId, DisplayName;", conn);
        cmd.Parameters.AddWithValue("entityIds", entityIds);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ownerEntityId = reader.GetGuid(0);
            if (!peopleById.TryGetValue(ownerEntityId, out var person))
                continue;

            person.LinkedNames.Add(new ResearchLinkedName
            {
                RelationshipId = reader.GetGuid(1),
                EntityId = reader.GetGuid(2),
                DisplayName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                NormalizedName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Confidence = reader.IsDBNull(5) ? 0m : reader.GetDecimal(5),
                Status = reader.IsDBNull(6) ? "" : reader.GetString(6)
            });
        }

        foreach (var person in people)
        {
            person.LinkedNameCount = person.LinkedNames.Count;
            person.LinkedNameSummary = string.Join(", ",
                person.LinkedNames.Select(link => $"{link.DisplayName} [{Math.Round(link.Confidence * 100m):0}%]"));
        }
    }

    private static ResearchPersonAdminRow ReadResearchPersonAdminRow(NpgsqlDataReader reader)
        => new()
        {
            EntityId = reader.GetGuid(0),
            CanonicalName = reader.IsDBNull(1) ? "" : reader.GetString(1),
            DisplayName = reader.IsDBNull(2) ? "" : reader.GetString(2),
            NormalizedName = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Confidence = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4),
            Status = reader.IsDBNull(5) ? "" : reader.GetString(5),
            Notes = reader.IsDBNull(6) ? "" : reader.GetString(6),
            AliasCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            AliasSummary = reader.IsDBNull(8) ? "" : reader.GetString(8),
            MentionCount = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            DocumentCount = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
            LinkedNameCount = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
            LinkedNameSummary = reader.IsDBNull(12) ? "" : reader.GetString(12)
        };

    private static string CleanResearchEntityStatus(string? status, string defaultStatus)
    {
        var clean = string.IsNullOrWhiteSpace(status) ? defaultStatus : status.Trim().ToLowerInvariant();
        return clean switch
        {
            "candidate" or "verified" or "disputed" or "rejected" or "archived" => clean,
            _ => defaultStatus
        };
    }

    private static void UpdateResearchPersonNotes(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid entityId,
        string? notes)
    {
        using var cmd = new NpgsqlCommand(@"
            INSERT INTO ResearchPeople (EntityId, Notes)
            VALUES (@entityId, @notes)
            ON CONFLICT (EntityId) DO UPDATE
            SET Notes = @notes;", conn, trans);
        cmd.Parameters.AddWithValue("entityId", entityId);
        cmd.Parameters.AddWithValue("notes", (object?)notes?.Trim() ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string? GetResearchEntityDisplayName(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid entityId)
    {
        using var cmd = new NpgsqlCommand(@"
            SELECT COALESCE(NULLIF(DisplayName, ''), CanonicalName)
            FROM ResearchEntities
            WHERE Id = @entityId AND EntityType = 'person';", conn, trans);
        cmd.Parameters.AddWithValue("entityId", entityId);
        return cmd.ExecuteScalar() as string;
    }

    private static int ExecuteCount(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        string sql,
        Guid sourceEntityId,
        Guid targetEntityId)
    {
        using var cmd = new NpgsqlCommand(sql, conn, trans);
        cmd.Parameters.AddWithValue("sourceEntityId", sourceEntityId);
        cmd.Parameters.AddWithValue("targetEntityId", targetEntityId);
        return cmd.ExecuteNonQuery();
    }

    private ResearchScanDocumentSource? LoadResearchScanDocument(Guid documentId)
    {
        using var conn = _dataSource.OpenConnection();
        string fileName;
        string? filePath;
        string? sentencesJson;
        string? sentenceIdsJson;

        using (var docCmd = new NpgsqlCommand(@"
            SELECT FileName, FilePath, Sentences::text, SentenceIds::text
            FROM ParentDocuments
            WHERE Id = @id;", conn))
        {
            docCmd.Parameters.AddWithValue("id", documentId);
            using var reader = docCmd.ExecuteReader();
            if (!reader.Read())
                return null;

            fileName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            filePath = reader.IsDBNull(1) ? null : reader.GetString(1);
            sentencesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
            sentenceIdsJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        var relationalSentences = TryLoadRelationalSentences(conn, documentId);
        if (relationalSentences.Count > 0)
            return new ResearchScanDocumentSource(documentId, fileName, filePath, relationalSentences);

        var sentences = DeserializeJson<List<string>>(sentencesJson) ?? new List<string>();
        var sentenceIds = DeserializeJson<List<Guid>>(sentenceIdsJson) ?? new List<Guid>();
        var scanSentences = new List<NameScanSentence>(sentences.Count);

        for (var i = 0; i < sentences.Count; i++)
        {
            var text = sentences[i];
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var sentenceId = i < sentenceIds.Count ? sentenceIds[i] : (Guid?)null;
            scanSentences.Add(new NameScanSentence(i + 1, text, sentenceId));
        }

        return new ResearchScanDocumentSource(documentId, fileName, filePath, scanSentences);
    }

    private ResearchScanDocumentSummary? LoadResearchScanDocumentSummary(Guid documentId)
    {
        using var conn = _dataSource.OpenConnection();

        using var docCmd = new NpgsqlCommand(@"
            SELECT FileName,
                   FilePath,
                   COALESCE(jsonb_array_length(Sentences), 0) AS SentenceCount
            FROM ParentDocuments
            WHERE Id = @id;", conn);
        docCmd.Parameters.AddWithValue("id", documentId);

        string fileName;
        string? filePath;
        var sentenceCount = 0;
        using (var reader = docCmd.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            fileName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            filePath = reader.IsDBNull(1) ? null : reader.GetString(1);
            sentenceCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        }

        try
        {
            using var sentenceCmd = new NpgsqlCommand(@"
                SELECT COUNT(*)::int
                FROM document_sentences
                WHERE document_id = @id;", conn);
            sentenceCmd.Parameters.AddWithValue("id", documentId);
            var relationalCount = Convert.ToInt32(sentenceCmd.ExecuteScalar() ?? 0);
            if (relationalCount > 0)
                sentenceCount = relationalCount;
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01" || ex.SqlState == "42703")
        {
            // Older databases may not have the relational sentence table yet.
        }

        return new ResearchScanDocumentSummary(documentId, fileName, filePath, sentenceCount);
    }

    private static List<NameScanSentence> TryLoadRelationalSentences(NpgsqlConnection conn, Guid documentId)
    {
        var sentences = new List<NameScanSentence>();
        try
        {
            using var cmd = new NpgsqlCommand(@"
                SELECT ordinal, sentence_id, page_number, text
                FROM document_sentences
                WHERE document_id = @id
                ORDER BY ordinal;", conn);
            cmd.Parameters.AddWithValue("id", documentId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var text = reader.IsDBNull(3) ? "" : reader.GetString(3);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                sentences.Add(new NameScanSentence(
                    reader.IsDBNull(0) ? sentences.Count + 1 : reader.GetInt32(0),
                    text,
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01" || ex.SqlState == "42703")
        {
            // Older databases may not have the relational sentence table yet.
        }

        return sentences;
    }

    private void PersistResearchNameScan(
        ResearchScanDocumentSource source,
        ResearchNameScanResult scan,
        ResearchDocumentScanOptions options,
        ResearchDocumentNameScanResult result)
    {
        using var conn = _dataSource.OpenConnection();
        using var trans = conn.BeginTransaction();

        try
        {
            var sourceKind = CleanSourceKind(options.SourceKind);
            var sourceRef = options.SourceRef ?? source.FilePath ?? source.FileName;
            var createdBy = GetOrCreateResearcherProfile(conn, trans, options.ResearcherDisplayName);

            DeletePriorScanArtifacts(conn, trans, source.DocumentId, sourceKind, scan.ExtractorVersion);

            var entityIdsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in scan.Candidates)
            {
                var entityId = GetOrCreateResearchPersonEntity(
                    conn, trans, candidate, sourceKind, sourceRef, scan.ExtractorVersion, createdBy, out var created);

                entityIdsByName[candidate.NormalizedName] = entityId;
                if (created) result.EntitiesCreated++;
                else result.EntitiesMatched++;
            }

            foreach (var mention in scan.Mentions)
            {
                if (!entityIdsByName.TryGetValue(mention.NormalizedMention, out var entityId))
                    continue;

                InsertResearchMention(conn, trans, source.DocumentId, entityId, mention, sourceKind, sourceRef, scan.ExtractorVersion, createdBy);
                result.MentionsInserted++;
            }

            if (options.IncludeCoMentionRelationships)
            {
                InsertCoMentionRelationships(
                    conn, trans, source.DocumentId, scan, entityIdsByName, sourceKind, sourceRef,
                    scan.ExtractorVersion, createdBy, options.MaxRelationshipNamesPerSentence, result);
            }

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    private static void DeletePriorScanArtifacts(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid documentId,
        string sourceKind,
        string extractorVersion)
    {
        using (var evidenceCmd = new NpgsqlCommand(@"
            DELETE FROM ResearchRelationshipEvidence
            WHERE DocumentId = @documentId
              AND SourceKind = @sourceKind
              AND ExtractorVersion = @extractorVersion;", conn, trans))
        {
            evidenceCmd.Parameters.AddWithValue("documentId", documentId);
            evidenceCmd.Parameters.AddWithValue("sourceKind", sourceKind);
            evidenceCmd.Parameters.AddWithValue("extractorVersion", extractorVersion);
            evidenceCmd.ExecuteNonQuery();
        }

        using (var mentionsCmd = new NpgsqlCommand(@"
            DELETE FROM ResearchEntityMentions
            WHERE DocumentId = @documentId
              AND SourceKind = @sourceKind
              AND ExtractorVersion = @extractorVersion;", conn, trans))
        {
            mentionsCmd.Parameters.AddWithValue("documentId", documentId);
            mentionsCmd.Parameters.AddWithValue("sourceKind", sourceKind);
            mentionsCmd.Parameters.AddWithValue("extractorVersion", extractorVersion);
            mentionsCmd.ExecuteNonQuery();
        }
    }

    private static Guid GetOrCreateResearchPersonEntity(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        ResearchNameCandidate candidate,
        string sourceKind,
        string sourceRef,
        string extractorVersion,
        Guid? createdBy,
        out bool created)
    {
        using (var selectCmd = new NpgsqlCommand(@"
            SELECT Id
            FROM ResearchEntities
            WHERE EntityType = 'person' AND NormalizedName = @normalized
            ORDER BY CreatedAt
            LIMIT 1;", conn, trans))
        {
            selectCmd.Parameters.AddWithValue("normalized", candidate.NormalizedName);
            var existing = selectCmd.ExecuteScalar();
            if (existing is Guid existingId)
            {
                using var updateCmd = new NpgsqlCommand(@"
                    UPDATE ResearchEntities
                    SET Confidence = GREATEST(Confidence, @confidence),
                        UpdatedAt = NOW(),
                        ExtractorVersion = COALESCE(ExtractorVersion, @extractorVersion)
                    WHERE Id = @id;", conn, trans);
                updateCmd.Parameters.AddWithValue("id", existingId);
                updateCmd.Parameters.AddWithValue("confidence", candidate.Confidence);
                updateCmd.Parameters.AddWithValue("extractorVersion", extractorVersion);
                updateCmd.ExecuteNonQuery();

                UpsertResearchPersonDetails(conn, trans, existingId, candidate.DisplayName);
                UpsertResearchAlias(conn, trans, existingId, candidate.DisplayName, candidate.NormalizedName, candidate.Confidence, sourceKind, createdBy);
                created = false;
                return existingId;
            }
        }

        var metadata = JsonSerializer.Serialize(new
        {
            scanner = extractorVersion,
            candidate.MentionCount,
            candidate.Sources
        });

        using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO ResearchEntities
                (EntityType, CanonicalName, NormalizedName, DisplayName, Confidence, Status,
                 SourceKind, SourceRef, ExtractorVersion, CreatedBy, Metadata)
            VALUES
                ('person', @canonical, @normalized, @display, @confidence, 'candidate',
                 @sourceKind, @sourceRef, @extractorVersion, @createdBy, @metadata::jsonb)
            RETURNING Id;", conn, trans);
        insertCmd.Parameters.AddWithValue("canonical", candidate.DisplayName);
        insertCmd.Parameters.AddWithValue("normalized", candidate.NormalizedName);
        insertCmd.Parameters.AddWithValue("display", candidate.DisplayName);
        insertCmd.Parameters.AddWithValue("confidence", candidate.Confidence);
        insertCmd.Parameters.AddWithValue("sourceKind", sourceKind);
        insertCmd.Parameters.AddWithValue("sourceRef", sourceRef);
        insertCmd.Parameters.AddWithValue("extractorVersion", extractorVersion);
        insertCmd.Parameters.AddWithValue("createdBy", (object?)createdBy ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("metadata", metadata);

        var id = (Guid)insertCmd.ExecuteScalar()!;
        UpsertResearchPersonDetails(conn, trans, id, candidate.DisplayName);
        UpsertResearchAlias(conn, trans, id, candidate.DisplayName, candidate.NormalizedName, candidate.Confidence, sourceKind, createdBy);
        created = true;
        return id;
    }

    private static void UpsertResearchPersonDetails(NpgsqlConnection conn, NpgsqlTransaction trans, Guid entityId, string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var given = parts.Length > 0 ? parts[0] : null;
        var family = parts.Length > 1 ? parts[^1] : null;
        var middle = parts.Length > 2 ? string.Join(' ', parts[1..^1]) : null;

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO ResearchPeople (EntityId, GivenNames, MiddleNames, FamilyName)
            VALUES (@entityId, @given, @middle, @family)
            ON CONFLICT (EntityId) DO UPDATE
            SET GivenNames = COALESCE(ResearchPeople.GivenNames, EXCLUDED.GivenNames),
                MiddleNames = COALESCE(ResearchPeople.MiddleNames, EXCLUDED.MiddleNames),
                FamilyName = COALESCE(ResearchPeople.FamilyName, EXCLUDED.FamilyName);", conn, trans);
        cmd.Parameters.AddWithValue("entityId", entityId);
        cmd.Parameters.AddWithValue("given", (object?)given ?? DBNull.Value);
        cmd.Parameters.AddWithValue("middle", (object?)middle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("family", (object?)family ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void UpsertResearchAlias(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid entityId,
        string alias,
        string normalizedAlias,
        decimal confidence,
        string sourceKind,
        Guid? createdBy)
    {
        using var cmd = new NpgsqlCommand(@"
            INSERT INTO ResearchEntityAliases
                (EntityId, Alias, NormalizedAlias, AliasType, IsPrimary, Confidence, SourceKind, CreatedBy)
            SELECT @entityId, @alias, @normalizedAlias, 'name', TRUE, @confidence, @sourceKind, @createdBy
            WHERE NOT EXISTS (
                SELECT 1
                FROM ResearchEntityAliases
                WHERE EntityId = @entityId AND NormalizedAlias = @normalizedAlias
            );", conn, trans);
        cmd.Parameters.AddWithValue("entityId", entityId);
        cmd.Parameters.AddWithValue("alias", alias);
        cmd.Parameters.AddWithValue("normalizedAlias", normalizedAlias);
        cmd.Parameters.AddWithValue("confidence", confidence);
        cmd.Parameters.AddWithValue("sourceKind", sourceKind);
        cmd.Parameters.AddWithValue("createdBy", (object?)createdBy ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void InsertResearchMention(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid documentId,
        Guid entityId,
        ResearchNameMention mention,
        string sourceKind,
        string sourceRef,
        string extractorVersion,
        Guid? createdBy)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            mention.SourcePattern,
            mention.SentenceText
        });

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO ResearchEntityMentions
                (EntityId, RawMention, NormalizedMention, MentionType, DocumentId, SentenceId,
                 SentenceOrdinal, PageNumber, CharacterStart, CharacterEnd, Confidence,
                 SourceKind, SourceRef, ExtractorVersion, CreatedBy, Metadata)
            VALUES
                (@entityId, @raw, @normalized, 'name', @documentId, @sentenceId,
                 @sentenceOrdinal, @pageNumber, @characterStart, @characterEnd, @confidence,
                 @sourceKind, @sourceRef, @extractorVersion, @createdBy, @metadata::jsonb);", conn, trans);
        cmd.Parameters.AddWithValue("entityId", entityId);
        cmd.Parameters.AddWithValue("raw", mention.RawMention);
        cmd.Parameters.AddWithValue("normalized", mention.NormalizedMention);
        cmd.Parameters.AddWithValue("documentId", documentId);
        cmd.Parameters.AddWithValue("sentenceId", (object?)mention.SentenceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sentenceOrdinal", (object?)mention.SentenceOrdinal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pageNumber", (object?)mention.PageNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("characterStart", (object?)mention.CharacterStart ?? DBNull.Value);
        cmd.Parameters.AddWithValue("characterEnd", (object?)mention.CharacterEnd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("confidence", mention.Confidence);
        cmd.Parameters.AddWithValue("sourceKind", sourceKind);
        cmd.Parameters.AddWithValue("sourceRef", sourceRef);
        cmd.Parameters.AddWithValue("extractorVersion", extractorVersion);
        cmd.Parameters.AddWithValue("createdBy", (object?)createdBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("metadata", metadata);
        cmd.ExecuteNonQuery();
    }

    private static void InsertCoMentionRelationships(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid documentId,
        ResearchNameScanResult scan,
        IReadOnlyDictionary<string, Guid> entityIdsByName,
        string sourceKind,
        string sourceRef,
        string extractorVersion,
        Guid? createdBy,
        int maxNamesPerSentence,
        ResearchDocumentNameScanResult result)
    {
        foreach (var sentenceGroup in scan.Mentions.GroupBy(m => m.SentenceOrdinal))
        {
            var sentenceMentions = sentenceGroup
                .Where(m => entityIdsByName.ContainsKey(m.NormalizedMention))
                .GroupBy(m => m.NormalizedMention, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(m => m.Confidence).First())
                .ToList();

            if (sentenceMentions.Count < 2)
                continue;

            if (sentenceMentions.Count > maxNamesPerSentence)
            {
                result.Warnings.Add($"Skipped co-mention links for sentence {sentenceGroup.Key}: {sentenceMentions.Count} names.");
                continue;
            }

            for (var i = 0; i < sentenceMentions.Count; i++)
            {
                for (var j = i + 1; j < sentenceMentions.Count; j++)
                {
                    var left = sentenceMentions[i];
                    var right = sentenceMentions[j];
                    var leftId = entityIdsByName[left.NormalizedMention];
                    var rightId = entityIdsByName[right.NormalizedMention];
                    var relationshipId = GetOrCreateCoMentionRelationship(
                        conn, trans, leftId, rightId, sourceKind, sourceRef, extractorVersion, createdBy, out var created);

                    if (created) result.RelationshipsCreated++;
                    else result.RelationshipsMatched++;

                    InsertRelationshipEvidence(
                        conn, trans, relationshipId, documentId, left, sourceKind, sourceRef, extractorVersion, createdBy,
                        Math.Max(left.Confidence, right.Confidence));
                    result.RelationshipEvidenceInserted++;
                }
            }
        }
    }

    private static Guid GetOrCreateCoMentionRelationship(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid a,
        Guid b,
        string sourceKind,
        string sourceRef,
        string extractorVersion,
        Guid? createdBy,
        out bool created)
    {
        var from = a.CompareTo(b) <= 0 ? a : b;
        var to = a.CompareTo(b) <= 0 ? b : a;

        using (var selectCmd = new NpgsqlCommand(@"
            SELECT Id
            FROM ResearchRelationships
            WHERE RelationshipType = 'co_mentioned'
              AND Direction = 'undirected'
              AND FromEntityId = @from
              AND ToEntityId = @to
            LIMIT 1;", conn, trans))
        {
            selectCmd.Parameters.AddWithValue("from", from);
            selectCmd.Parameters.AddWithValue("to", to);
            var existing = selectCmd.ExecuteScalar();
            if (existing is Guid existingId)
            {
                created = false;
                return existingId;
            }
        }

        var metadata = JsonSerializer.Serialize(new { note = "People mentioned in the same sentence." });
        using var insertCmd = new NpgsqlCommand(@"
            INSERT INTO ResearchRelationships
                (FromEntityId, ToEntityId, RelationshipType, Direction, Confidence, Status,
                 SourceKind, SourceRef, ExtractorVersion, CreatedBy, Metadata)
            VALUES
                (@from, @to, 'co_mentioned', 'undirected', 0.5000, 'candidate',
                 @sourceKind, @sourceRef, @extractorVersion, @createdBy, @metadata::jsonb)
            RETURNING Id;", conn, trans);
        insertCmd.Parameters.AddWithValue("from", from);
        insertCmd.Parameters.AddWithValue("to", to);
        insertCmd.Parameters.AddWithValue("sourceKind", sourceKind);
        insertCmd.Parameters.AddWithValue("sourceRef", sourceRef);
        insertCmd.Parameters.AddWithValue("extractorVersion", extractorVersion);
        insertCmd.Parameters.AddWithValue("createdBy", (object?)createdBy ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("metadata", metadata);
        created = true;
        return (Guid)insertCmd.ExecuteScalar()!;
    }

    private static void InsertRelationshipEvidence(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        Guid relationshipId,
        Guid documentId,
        ResearchNameMention mention,
        string sourceKind,
        string sourceRef,
        string extractorVersion,
        Guid? createdBy,
        decimal confidence)
    {
        using var cmd = new NpgsqlCommand(@"
            INSERT INTO ResearchRelationshipEvidence
                (RelationshipId, DocumentId, SentenceId, SentenceOrdinal, PageNumber, Quote,
                 EvidenceKind, Confidence, SourceKind, SourceRef, ExtractorVersion, CreatedBy)
            VALUES
                (@relationshipId, @documentId, @sentenceId, @sentenceOrdinal, @pageNumber, @quote,
                 'co_mention_sentence', @confidence, @sourceKind, @sourceRef, @extractorVersion, @createdBy);", conn, trans);
        cmd.Parameters.AddWithValue("relationshipId", relationshipId);
        cmd.Parameters.AddWithValue("documentId", documentId);
        cmd.Parameters.AddWithValue("sentenceId", (object?)mention.SentenceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sentenceOrdinal", (object?)mention.SentenceOrdinal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pageNumber", (object?)mention.PageNumber ?? DBNull.Value);
        cmd.Parameters.AddWithValue("quote", (object?)mention.SentenceText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("confidence", confidence);
        cmd.Parameters.AddWithValue("sourceKind", sourceKind);
        cmd.Parameters.AddWithValue("sourceRef", sourceRef);
        cmd.Parameters.AddWithValue("extractorVersion", extractorVersion);
        cmd.Parameters.AddWithValue("createdBy", (object?)createdBy ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static Guid? GetOrCreateResearcherProfile(
        NpgsqlConnection conn,
        NpgsqlTransaction trans,
        string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        using var cmd = new NpgsqlCommand(@"
            INSERT INTO ResearcherProfiles (DisplayName)
            VALUES (@displayName)
            ON CONFLICT (DisplayName) DO UPDATE SET UpdatedAt = NOW()
            RETURNING Id;", conn, trans);
        cmd.Parameters.AddWithValue("displayName", displayName.Trim());
        return (Guid?)cmd.ExecuteScalar();
    }

    private static T? DeserializeJson<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }

    private static string CleanSourceKind(string sourceKind)
        => string.IsNullOrWhiteSpace(sourceKind) ? "system" : sourceKind.Trim();

    public const string ResearchGraphSchemaSql = """
        CREATE TABLE IF NOT EXISTS ResearcherProfiles (
            Id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            DisplayName     TEXT NOT NULL UNIQUE,
            Email           TEXT,
            Affiliation     TEXT,
            Metadata        JSONB NOT NULL DEFAULT '{}'::jsonb,
            CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UpdatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW()
        );

        CREATE TABLE IF NOT EXISTS ResearchEntities (
            Id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            EntityType       TEXT NOT NULL CHECK (EntityType IN ('person', 'place', 'organization', 'event', 'document', 'concept', 'unknown')),
            CanonicalName    TEXT NOT NULL,
            NormalizedName   TEXT NOT NULL,
            DisplayName      TEXT,
            Description      TEXT,
            Confidence       NUMERIC(5,4) NOT NULL DEFAULT 0.5000 CHECK (Confidence >= 0 AND Confidence <= 1),
            Veracity         NUMERIC(5,4) CHECK (Veracity IS NULL OR (Veracity >= 0 AND Veracity <= 1)),
            Status           TEXT NOT NULL DEFAULT 'candidate' CHECK (Status IN ('candidate', 'verified', 'disputed', 'rejected', 'archived')),
            SourceKind       TEXT NOT NULL DEFAULT 'system',
            SourceRef        TEXT,
            ExtractorVersion TEXT,
            CreatedBy        UUID REFERENCES ResearcherProfiles(Id),
            CreatedAt        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UpdatedAt        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata         JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchPeople (
            EntityId     UUID PRIMARY KEY REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            Honorifics   TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
            GivenNames   TEXT,
            MiddleNames  TEXT,
            FamilyName   TEXT,
            Suffixes     TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
            Nicknames    TEXT[] NOT NULL DEFAULT ARRAY[]::TEXT[],
            DateOfBirth  TEXT,
            DateOfDeath  TEXT,
            Notes        TEXT,
            Metadata     JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchPlaces (
            EntityId     UUID PRIMARY KEY REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            PlaceType    TEXT,
            Address      TEXT,
            City         TEXT,
            Region       TEXT,
            Country      TEXT,
            Latitude     NUMERIC(10,7),
            Longitude    NUMERIC(10,7),
            Notes        TEXT,
            Metadata     JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchOrganizations (
            EntityId      UUID PRIMARY KEY REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            OrganizationType TEXT,
            Jurisdiction  TEXT,
            Notes         TEXT,
            Metadata      JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchEntityAliases (
            Id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            EntityId        UUID NOT NULL REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            Alias           TEXT NOT NULL,
            NormalizedAlias TEXT NOT NULL,
            AliasType       TEXT NOT NULL DEFAULT 'name',
            IsPrimary       BOOLEAN NOT NULL DEFAULT FALSE,
            Confidence      NUMERIC(5,4) NOT NULL DEFAULT 0.5000 CHECK (Confidence >= 0 AND Confidence <= 1),
            Veracity        NUMERIC(5,4) CHECK (Veracity IS NULL OR (Veracity >= 0 AND Veracity <= 1)),
            SourceKind      TEXT NOT NULL DEFAULT 'system',
            SourceRef       TEXT,
            CreatedBy       UUID REFERENCES ResearcherProfiles(Id),
            CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata        JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchEntityMentions (
            Id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            EntityId         UUID REFERENCES ResearchEntities(Id) ON DELETE SET NULL,
            RawMention       TEXT NOT NULL,
            NormalizedMention TEXT NOT NULL,
            MentionType      TEXT NOT NULL DEFAULT 'name',
            DocumentId       UUID REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
            SentenceId       UUID,
            SentenceOrdinal  INT,
            PageNumber       INT,
            CharacterStart   INT,
            CharacterEnd     INT,
            Confidence       NUMERIC(5,4) NOT NULL DEFAULT 0.5000 CHECK (Confidence >= 0 AND Confidence <= 1),
            Veracity         NUMERIC(5,4) CHECK (Veracity IS NULL OR (Veracity >= 0 AND Veracity <= 1)),
            SourceKind       TEXT NOT NULL DEFAULT 'system',
            SourceRef        TEXT,
            ExtractorVersion TEXT,
            CreatedBy        UUID REFERENCES ResearcherProfiles(Id),
            CreatedAt        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata         JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchRelationships (
            Id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            FromEntityId     UUID NOT NULL REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            ToEntityId       UUID NOT NULL REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            RelationshipType TEXT NOT NULL,
            Direction        TEXT NOT NULL DEFAULT 'directed' CHECK (Direction IN ('directed', 'undirected')),
            Confidence       NUMERIC(5,4) NOT NULL DEFAULT 0.5000 CHECK (Confidence >= 0 AND Confidence <= 1),
            Veracity         NUMERIC(5,4) CHECK (Veracity IS NULL OR (Veracity >= 0 AND Veracity <= 1)),
            Status           TEXT NOT NULL DEFAULT 'candidate' CHECK (Status IN ('candidate', 'verified', 'disputed', 'rejected', 'archived')),
            SourceKind       TEXT NOT NULL DEFAULT 'system',
            SourceRef        TEXT,
            ExtractorVersion TEXT,
            CreatedBy        UUID REFERENCES ResearcherProfiles(Id),
            CreatedAt        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UpdatedAt        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata         JSONB NOT NULL DEFAULT '{}'::jsonb,
            CHECK (FromEntityId <> ToEntityId)
        );

        CREATE TABLE IF NOT EXISTS ResearchRelationshipEvidence (
            Id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            RelationshipId   UUID NOT NULL REFERENCES ResearchRelationships(Id) ON DELETE CASCADE,
            DocumentId       UUID REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
            SentenceId       UUID,
            SentenceOrdinal  INT,
            PageNumber       INT,
            Quote            TEXT,
            EvidenceKind     TEXT NOT NULL DEFAULT 'sentence',
            Confidence       NUMERIC(5,4) NOT NULL DEFAULT 0.5000 CHECK (Confidence >= 0 AND Confidence <= 1),
            Veracity         NUMERIC(5,4) CHECK (Veracity IS NULL OR (Veracity >= 0 AND Veracity <= 1)),
            SourceKind       TEXT NOT NULL DEFAULT 'system',
            SourceRef        TEXT,
            ExtractorVersion TEXT,
            CreatedBy        UUID REFERENCES ResearcherProfiles(Id),
            CreatedAt        TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata         JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchFindings (
            Id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            Title       TEXT NOT NULL,
            Body        TEXT NOT NULL DEFAULT '',
            FindingType TEXT NOT NULL DEFAULT 'note',
            Confidence  NUMERIC(5,4) NOT NULL DEFAULT 0.5000 CHECK (Confidence >= 0 AND Confidence <= 1),
            Veracity    NUMERIC(5,4) CHECK (Veracity IS NULL OR (Veracity >= 0 AND Veracity <= 1)),
            Status      TEXT NOT NULL DEFAULT 'draft' CHECK (Status IN ('draft', 'active', 'verified', 'disputed', 'rejected', 'archived')),
            CreatedBy   UUID REFERENCES ResearcherProfiles(Id),
            CreatedAt   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UpdatedAt   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata    JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE TABLE IF NOT EXISTS ResearchFindingLinks (
            Id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            FindingId      UUID NOT NULL REFERENCES ResearchFindings(Id) ON DELETE CASCADE,
            EntityId       UUID REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            RelationshipId UUID REFERENCES ResearchRelationships(Id) ON DELETE CASCADE,
            DocumentId     UUID REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
            SentenceId     UUID,
            LinkRole       TEXT NOT NULL DEFAULT 'related',
            CreatedAt      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            CHECK (EntityId IS NOT NULL OR RelationshipId IS NOT NULL OR DocumentId IS NOT NULL OR SentenceId IS NOT NULL)
        );

        CREATE TABLE IF NOT EXISTS ResearchAssertions (
            Id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            SubjectEntityId UUID NOT NULL REFERENCES ResearchEntities(Id) ON DELETE CASCADE,
            Predicate       TEXT NOT NULL,
            ObjectEntityId  UUID REFERENCES ResearchEntities(Id) ON DELETE SET NULL,
            ObjectValue     TEXT,
            DocumentId      UUID REFERENCES ParentDocuments(Id) ON DELETE SET NULL,
            SentenceId      UUID,
            Confidence      NUMERIC(5,4) NOT NULL DEFAULT 0.5000 CHECK (Confidence >= 0 AND Confidence <= 1),
            Veracity        NUMERIC(5,4) CHECK (Veracity IS NULL OR (Veracity >= 0 AND Veracity <= 1)),
            Status          TEXT NOT NULL DEFAULT 'candidate' CHECK (Status IN ('candidate', 'verified', 'disputed', 'rejected', 'archived')),
            CreatedBy       UUID REFERENCES ResearcherProfiles(Id),
            CreatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UpdatedAt       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata        JSONB NOT NULL DEFAULT '{}'::jsonb,
            CHECK (ObjectEntityId IS NOT NULL OR ObjectValue IS NOT NULL)
        );

        CREATE TABLE IF NOT EXISTS SentenceIdentityLedger (
            SentenceId        UUID PRIMARY KEY,
            DocumentId        UUID NOT NULL REFERENCES ParentDocuments(Id) ON DELETE CASCADE,
            SentenceOrdinal   INT NOT NULL,
            SourceCode        BIGINT,
            OrderCode         BIGINT,
            VeracityCode      SMALLINT,
            SourceFingerprint TEXT,
            TextHash          TEXT,
            GeneratorVersion  TEXT,
            DerivedVersion    TEXT,
            CreatedAt         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            Metadata          JSONB NOT NULL DEFAULT '{}'::jsonb
        );

        CREATE INDEX IF NOT EXISTS idx_research_entities_type_name ON ResearchEntities(EntityType, NormalizedName);
        CREATE INDEX IF NOT EXISTS idx_research_entities_normalized ON ResearchEntities(NormalizedName);
        CREATE INDEX IF NOT EXISTS idx_research_entities_status ON ResearchEntities(Status);
        CREATE INDEX IF NOT EXISTS idx_research_aliases_normalized ON ResearchEntityAliases(NormalizedAlias);
        CREATE INDEX IF NOT EXISTS idx_research_aliases_entity ON ResearchEntityAliases(EntityId);
        CREATE INDEX IF NOT EXISTS idx_research_mentions_entity ON ResearchEntityMentions(EntityId);
        CREATE INDEX IF NOT EXISTS idx_research_mentions_document ON ResearchEntityMentions(DocumentId);
        CREATE INDEX IF NOT EXISTS idx_research_mentions_sentence ON ResearchEntityMentions(SentenceId);
        CREATE INDEX IF NOT EXISTS idx_research_mentions_doc_source ON ResearchEntityMentions(DocumentId, SourceKind, ExtractorVersion);
        CREATE INDEX IF NOT EXISTS idx_research_relationships_from ON ResearchRelationships(FromEntityId);
        CREATE INDEX IF NOT EXISTS idx_research_relationships_to ON ResearchRelationships(ToEntityId);
        CREATE INDEX IF NOT EXISTS idx_research_relationships_type ON ResearchRelationships(RelationshipType);
        CREATE INDEX IF NOT EXISTS idx_research_relationship_evidence_rel ON ResearchRelationshipEvidence(RelationshipId);
        CREATE INDEX IF NOT EXISTS idx_research_relationship_evidence_doc ON ResearchRelationshipEvidence(DocumentId);
        CREATE INDEX IF NOT EXISTS idx_research_findings_status ON ResearchFindings(Status);
        CREATE INDEX IF NOT EXISTS idx_research_finding_links_finding ON ResearchFindingLinks(FindingId);
        CREATE INDEX IF NOT EXISTS idx_research_assertions_subject ON ResearchAssertions(SubjectEntityId);
        CREATE INDEX IF NOT EXISTS idx_sentence_identity_document ON SentenceIdentityLedger(DocumentId);
        CREATE INDEX IF NOT EXISTS idx_sentence_identity_source ON SentenceIdentityLedger(SourceCode);
        """;
}

public sealed record ResearchDocumentScanOptions
{
    public bool Persist { get; init; } = true;
    public bool IncludeCoMentionRelationships { get; init; } = true;
    public string SourceKind { get; init; } = "system";
    public string? SourceRef { get; init; }
    public string? ResearcherDisplayName { get; init; }
    public int MaxRelationshipNamesPerSentence { get; init; } = 6;
    public int MaxCharacters { get; init; }
    public decimal MinimumCandidateConfidence { get; init; } = 0.0m;
}

public sealed class ResearchDocumentNameScanResult
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string? FilePath { get; set; }
    public string ExtractorVersion { get; set; } = ResearchNameScanner.Version;
    public int SentenceCount { get; set; }
    public int CandidateNameCount { get; set; }
    public int MentionCount { get; set; }
    public bool Persisted { get; set; }
    public int EntitiesCreated { get; set; }
    public int EntitiesMatched { get; set; }
    public int EntitiesUpserted => EntitiesCreated + EntitiesMatched;
    public int MentionsInserted { get; set; }
    public int RelationshipsCreated { get; set; }
    public int RelationshipsMatched { get; set; }
    public int RelationshipsUpserted => RelationshipsCreated + RelationshipsMatched;
    public int RelationshipEvidenceInserted { get; set; }
    public List<ResearchNameCandidate> Candidates { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class ResearchNameResearchView
{
    public Guid DocumentId { get; set; }
    public string FileName { get; set; } = "";
    public string? FilePath { get; set; }
    public int SentenceCount { get; set; }
    public int PersonCount { get; set; }
    public int AliasCount { get; set; }
    public int MentionCount { get; set; }
    public List<ResearchPersonGroup> People { get; set; } = new();
}

public sealed class ResearchPersonGroup
{
    public Guid EntityId { get; set; }
    public string CanonicalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public decimal Confidence { get; set; }
    public string Status { get; set; } = "";
    public int AliasCount => Aliases.Count;
    public int MentionCount => Mentions.Count;
    public int LinkedNameCount => LinkedNames.Count;
    public string LinkedNameSummary => string.Join(", ", LinkedNames);
    public List<ResearchPersonAlias> Aliases { get; set; } = new();
    public List<ResearchPersonMention> Mentions { get; set; } = new();
    public List<string> LinkedNames { get; set; } = new();
}

public sealed class ResearchPersonAlias
{
    public Guid AliasId { get; set; }
    public string Alias { get; set; } = "";
    public string NormalizedAlias { get; set; } = "";
    public string AliasType { get; set; } = "name";
    public bool IsPrimary { get; set; }
    public decimal Confidence { get; set; }
    public string SourceKind { get; set; } = "";
}

public sealed class ResearchPersonMention
{
    public Guid MentionId { get; set; }
    public string RawMention { get; set; } = "";
    public string NormalizedMention { get; set; } = "";
    public int? SentenceOrdinal { get; set; }
    public int? PageNumber { get; set; }
    public decimal Confidence { get; set; }
    public string SourcePattern { get; set; } = "";
    public string? SentenceText { get; set; }
}

public sealed class ResearchPersonAdminRow
{
    public Guid EntityId { get; set; }
    public string CanonicalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public decimal Confidence { get; set; }
    public string Status { get; set; } = "";
    public string AliasSummary { get; set; } = "";
    public int AliasCount { get; set; }
    public int MentionCount { get; set; }
    public int DocumentCount { get; set; }
    public int LinkedNameCount { get; set; }
    public string LinkedNameSummary { get; set; } = "";
    public List<ResearchLinkedName> LinkedNames { get; set; } = new();
    public string Notes { get; set; } = "";
}

public sealed class ResearchLinkedName
{
    public Guid RelationshipId { get; set; }
    public Guid EntityId { get; set; }
    public string DisplayName { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public decimal Confidence { get; set; }
    public string Status { get; set; } = "";
}

public sealed class ResearchNameLink
{
    public Guid RelationshipId { get; set; }
    public Guid SourceEntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public string TargetDisplayName { get; set; } = "";
    public decimal Confidence { get; set; }
    public string Status { get; set; } = "";
}

public sealed class ResearchPersonMergeResult
{
    public Guid SourceEntityId { get; set; }
    public Guid TargetEntityId { get; set; }
    public string SourceDisplayName { get; set; } = "";
    public string TargetDisplayName { get; set; } = "";
    public int AliasesMoved { get; set; }
    public int AliasesDeduped { get; set; }
    public int MentionsMoved { get; set; }
    public int RelationshipsMoved { get; set; }
    public int RelationshipsRemoved { get; set; }
    public int FindingsMoved { get; set; }
    public int AssertionsMoved { get; set; }
}

internal sealed record ResearchScanDocumentSource(
    Guid DocumentId,
    string FileName,
    string? FilePath,
    List<NameScanSentence> Sentences);

internal sealed record ResearchScanDocumentSummary(
    Guid DocumentId,
    string FileName,
    string? FilePath,
    int SentenceCount);
