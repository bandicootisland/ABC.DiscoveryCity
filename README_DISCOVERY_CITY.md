# Discovery City — Project State & Findings

> Last updated: 2026-02-10 (Ubuntu NUC Mini2)

## Architecture

| Component | Project | Port | Purpose |
|-----------|---------|------|---------|
| Blazor Frontend | ABC.DiscoveryCity | 5233 | Search UI with Telerik DataGrid |
| REST API | ABC.DiscoveryCity.API | 5022 | Search endpoints, stats |
| PDF Processor | ABC.DiscoveryCity.TestApp | CLI | Ingestion, embeddings, reprocessing |
| PostgreSQL + pgvector | Docker | 5435 | Document storage, vector search |
| Ollama | systemd | 11434 | Embedding generation (all-minilm:latest, 384 dims) |

## Infrastructure

- **Machine**: Ubuntu NUC Mini2, 18TB drive at `/media/stephen/18TB`
- **Runtime**: .NET 10.0, Telerik UI for Blazor 12.0.0
- **Database**: PostgreSQL 16 + pgvector in Docker container `discovery-city-postgres`
  - Port: 5435, DB: `discoverycity`, User: `discovery_user`
- **Ollama**: v0.15.6, model `all-minilm:latest` (45MB, 384 dimensions)
- **Data root**: `/media/stephen/18TB/EpsteinFiles/DepartmentofJustice/DOJ_Disclosures/`

## Database Stats (2026-02-10)

| Metric | Count |
|--------|-------|
| Total Documents | 729,937 |
| Documents with People extracted | 60 |
| Total Chunks | 1,475,291 |
| Chunks with Embeddings | 1,060,997 |
| Chunks without Embeddings | 414,302 |
| DataSets | 12 |

## Setup

1. **Database**: 
   ```bash
   docker-compose up -d discovery-city-db
   ```
   Runs on port **5435** (mapped from container 5432).

2. **Ollama**:
   ```bash
   systemctl status ollama   # Should be running
   ollama list               # Should show all-minilm:latest
   ```

3. **Dependencies**: Restored automatically on build. Telerik NuGet source required.

## CLI Flags (TestApp)

```bash
cd ABC.DiscoveryCity.TestApp
dotnet run -- [DataSet] [flags]
```

| Flag | Purpose |
|------|---------|
| `--reprocess` | Re-extract metadata (People etc.) from stored text — no PDF re-parsing |
| `--embeddings-only` | Backfill NULL embeddings from DB chunks |
| `--force` | Ignore .done flags, reprocess all PDFs from scratch |
| `--no-images` | Skip Playwright thumbnail generation |
| `--limit N` | Process only N documents/chunks |
| `--clean` | Delete generated files (.done, .json, .jpg, .html) |
| `--stats` | Print DB counts and exit |
| `--headless` | Run Playwright in headless mode |
| `--render-direct` | Use Telerik direct rendering (no browser) |
| `--extract-people-llm` | (stub) Future LLM-based people extraction |

## Key Findings & Decisions

### People Extraction (Regex)
- Extracts names from email headers (From/To/Cc/Bcc/Sent by), salutations (Dear/Hi/Hello),
  context patterns (w/, meeting with), and capitalized "Firstname Lastname" sequences.
- **MIME artifacts**: Many scanned emails contain `=` replacing characters (e.g., `Ep=tein`, `bo=tom`).
  Regex uses `[a-z=]` character classes to match through these. The `=` is stripped only in the
  People metadata field — **raw text is never modified** (evidence preservation).
- False positive filtering: reject-first-words list, common phrase blacklist, min-length checks.
- People stored as JSONB array in `ParentDocuments.Metadata->>'People'`.

### Evidence Preservation Principle
- Raw OCR text is **sacred evidence** — must not be cleaned or modified.
- MIME `=` artifacts are a **matching problem**, not a cleanup problem.
- Only derived/metadata fields (People, Title, Date) may contain cleaned values.
- `CleanMimeArtifacts()` function exists but is NOT applied to stored text.

### Idempotency
- All processing modes are safe to re-run:
  - Normal: skips `.done` files
  - `--force`: full re-parse, UPDATE existing + DELETE/recreate chunks
  - `--reprocess`: reads from DB, updates metadata only (no chunk changes)
  - `--embeddings-only`: only fills NULL embeddings
- Flag files: `.done` (text), `.done.embeddings`, `.done.images`

### Reprocess Mode (`--reprocess`)
- Reads existing documents from DB (no PDF parsing needed — fast)
- Reconstructs full text from stored chunks
- Re-runs all extraction logic (currently: People regex)
- Updates only the metadata JSONB, leaves chunks and embeddings untouched
- Extensible: add future extractions (LLM entities, classification, etc.) in marked section

## Database Schema

- `ParentDocuments`: Id, FilePath (unique), Metadata (JSONB), DataSetId, ProcessedAt
- `DocumentChunks`: ChunkId, ParentId, ChunkIndex, TextContent, Embedding (vector(384))
- `DocumentImages`: Id, ParentId, ImageType, ImageSize, FilePath, Width, Height
- `Sources` / `DataSets`: Hierarchy for organizing document collections

## Running the Full Pipeline

```bash
# 1. Reprocess all docs for People extraction (reads from DB, fast)
cd ABC.DiscoveryCity.TestApp
nohup dotnet run -- --reprocess --no-images > reprocess.log 2>&1 &

# 2. Backfill embeddings for chunks with NULL embeddings  
nohup dotnet run -- --embeddings-only > embeddings.log 2>&1 &

# 3. Monitor progress
tail -f reprocess.log
tail -f embeddings.log

# 4. Start API + Blazor
cd ../ABC.DiscoveryCity.API && dotnet run &
cd ../ABC.DiscoveryCity && dotnet run &
```

## Notes

- The application is idempotent. Re-running any mode is safe.
- To re-process a specific file from scratch, delete its `.done` file.
- Git origin: `https://github.com/bandicootisland/ABC.DiscoveryCity.git` (branch: Develop)
