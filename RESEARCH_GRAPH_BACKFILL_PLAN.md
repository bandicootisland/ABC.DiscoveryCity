# Discovery City Research Graph Backfill Plan

## Goal

Turn people, places, organizations, aliases, mentions, relationships, evidence, and researcher findings into first-class data. The raw corpus remains unchanged; this plan builds versioned derived data that can be audited, regenerated, and compared before the UI relies on it.

## Current Ground Truth

- `ParentDocuments.Sentences` is the current authoritative sentence source used by search and counts.
- `document_sentences` is a normalized copy, but it is not fully backfilled.
- `SentenceSignatures` is experimental and only covers a tiny part of the corpus.
- Researcher-entered findings need to sit beside machine-derived assertions, with confidence, veracity, status, and evidence links.

## Schema Added

The research graph schema is additive and lives in `ABC.DiscoveryCity.PostgreSQL/Schema/ResearchGraph.sql`.

- `ResearcherProfiles`: researcher identity until real auth exists.
- `ResearchEntities`: canonical entity records for people, places, organizations, events, documents, and concepts.
- `ResearchPeople`, `ResearchPlaces`, `ResearchOrganizations`: typed profile details.
- `ResearchEntityAliases`: nicknames, initials, spelling variants, OCR variants, and formal names.
- `ResearchEntityMentions`: raw mentions linked to documents and sentence ids.
- `ResearchRelationships`: person-person, person-place, person-organization, and other graph edges.
- `ResearchRelationshipEvidence`: sentence/document evidence for graph edges.
- `ResearchFindings`: researcher notes, conclusions, hypotheses, and findings.
- `ResearchFindingLinks`: links from findings to entities, relationships, documents, and sentences.
- `ResearchAssertions`: atomic researcher or machine assertions.
- `SentenceIdentityLedger`: decoded sentence-id components for source, order, veracity, and generator version.

## Backfill Phases

1. Sentence identity audit

   Decode existing sentence ids where possible. Record source, order, veracity, source fingerprint, text hash, generator version, and derived version in `SentenceIdentityLedger`.

2. Sentence normalization backfill

   Rebuild `document_sentences` from the final cleaned sentence list, not from an earlier intermediate list. Generate sentence ids after final cleaning so sentence count and id count match.

3. Entity extraction

   Extract people, places, and organizations from `document_sentences`. Store raw mentions first, then canonicalize into `ResearchEntities` and `ResearchEntityAliases`.

4. Alias and nickname resolution

   Build normalized alias groups for full names, short names, initials, nicknames, OCR variants, married/maiden names, titles, and contextual references.

5. Association extraction

   Create relationship candidates from sentence and paragraph windows. Store evidence before promoting a relationship to verified status.

6. Researcher review

   Let researchers add or edit findings, aliases, assertions, and relationship evidence without overwriting machine-derived data.

7. UI pivot

   Use the research graph for the Daemask Discovery Search pivot: document to people, people to places, people to people, entity timeline, and evidence-backed findings.

## Sentence Id Direction

For this corpus, sentence ids should be treated as durable linking keys, not merely vector-search support. The preferred flow is:

1. final sentence text
2. stable source calculation
3. UUIDv8 sentence id with source, order, and veracity components
4. normalized `document_sentences`
5. `SentenceIdentityLedger`
6. entity mentions and relationships linked by sentence id

Vector and semantic indexes can be rebuilt later from this layer, but the durable graph should not depend on vectors.

## Open Item

The latest WordCity sentence-id implementation was requested at `Developer.WordCity/ABC.WordCity`, but that checkout is not present under `/Users/stephen/Developer` on this machine. Review it before finalizing the sentence id decoder/backfill code.
