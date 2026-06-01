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
CREATE INDEX IF NOT EXISTS idx_research_entities_status ON ResearchEntities(Status);
CREATE INDEX IF NOT EXISTS idx_research_aliases_normalized ON ResearchEntityAliases(NormalizedAlias);
CREATE INDEX IF NOT EXISTS idx_research_aliases_entity ON ResearchEntityAliases(EntityId);
CREATE INDEX IF NOT EXISTS idx_research_mentions_entity ON ResearchEntityMentions(EntityId);
CREATE INDEX IF NOT EXISTS idx_research_mentions_document ON ResearchEntityMentions(DocumentId);
CREATE INDEX IF NOT EXISTS idx_research_mentions_sentence ON ResearchEntityMentions(SentenceId);
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
