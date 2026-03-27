# Discovery City — Project State & Findings

> Last updated: 2026-02-13 (Ubuntu NUC Mini2)

## Architecture

| Component | Project | Port | Purpose |
|-----------|---------|------|---------|
| Blazor Frontend | ABC.DiscoveryCity | 5233 | Search UI with Telerik DataGrid |
| REST API | ABC.DiscoveryCity.API | 5022 | Search endpoints, stats |
| PDF Processor | ABC.DiscoveryCity.DocumentIngestionProcessing | CLI | Ingestion, embeddings, reprocessing |
| PostgreSQL + pgvector | Docker | 5435 | Document storage, vector search |
| Ollama | systemd | 11434 | Embedding generation (mxbai-embed-large:latest, 1024 dims) |

## Infrastructure

- **Machine**: Ubuntu NUC Mini2, 18TB drive at `/media/stephen/18TB`
- **Runtime**: .NET 10.0, Telerik UI for Blazor 12.0.0
- **Database**: PostgreSQL 16 + pgvector in Docker container `discovery-city-postgres`
  - Port: 5435, DB: `discoverycity`, User: `discovery_user`
- **Ollama**: v0.15.6, model `mxbai-embed-large:latest` (670MB, 1024 dimensions)
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
   ollama list               # Should show mxbai-embed-large:latest
   ```

3. **Dependencies**: Restored automatically on build. Telerik NuGet source required.

## CLI Flags (DocumentIngestionProcessing)

```bash
cd ABC.DiscoveryCity.DocumentIngestionProcessing
dotnet run -- [DataSet] [flags]
```

| Flag | Purpose |
|------|---------|
| `--reprocess` | Re-extract metadata (People etc.) from stored text — no PDF re-parsing |
| `--embeddings-only` | Backfill NULL embeddings from DB chunks |
| `--force` | Ignore .done flags, reprocess all PDFs from scratch |
| `--no-images` | Skip thumbnail generation entirely |
| `--use-playwright` | Use Playwright browser for thumbnails (slower, ~5x) instead of Telerik direct |
| `--headless` | Run Playwright in headless mode (only relevant with `--use-playwright`) |
| `--limit N` | Process only N documents/chunks |
| `--clean` | Delete generated files (.done, .json, .jpg, .html) |
| `--stats` | Print DB counts and exit |
| `--extract-people-llm` | (stub) Future LLM-based people extraction |

**Defaults**: Telerik direct rendering for thumbnails (no browser needed). Embeddings are generated
automatically when Ollama is available. DataSet folders are processed in natural numeric order
(DataSet 1, 2, ... 9, 10, 11).

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
- `DocumentChunks`: ChunkId, ParentId, ChunkIndex, TextContent, Embedding (vector(1024))
- `DocumentImages`: Id, ParentId, ImageType, ImageSize, FilePath, Width, Height
- `Sources` / `DataSets`: Hierarchy for organizing document collections

## Running the Full Pipeline

```bash
# 1. Full ingestion (text + embeddings + thumbnails via Telerik direct, all defaults)
cd ABC.DiscoveryCity.DocumentIngestionProcessing
nohup dotnet run -- "DataSet 9" > dataset9.log 2>&1 &

# 2. Reprocess all docs for People extraction (reads from DB, fast)
nohup dotnet run -- --reprocess --no-images > reprocess.log 2>&1 &

# 3. Backfill embeddings for chunks with NULL embeddings  
nohup dotnet run -- --embeddings-only > embeddings.log 2>&1 &

# 4. Monitor progress
tail -f dataset9.log

# 5. Start API + Blazor
cd ../ABC.DiscoveryCity.API && dotnet run &
cd ../ABC.DiscoveryCity && dotnet run &
```

## Performance & Memory

- **Telerik direct rendering** (`--render-direct`, now the default) is **~5x faster** than Playwright
  browser-based rendering for thumbnail generation. No browser instances required.
- **Memory**: The process can run out of memory on large DataSets (e.g., DataSet 9 has 219K PDFs).
  If OOM occurs, use `--limit N` to process in smaller batches, or restart — the process is
  idempotent and will resume from where it left off (skips `.done` files).
- Playwright mode (`--use-playwright --headless`) spawns 10 browser instances; this consumes
  significantly more RAM and is slower. Only use when Telerik direct rendering produces
  unsatisfactory thumbnails.

## DataSet Status (2026-02-13)

| DataSet | Source PDFs | .done (text) | .done.embeddings | .done.images | Status |
|---------|------------|--------------|------------------|--------------|--------|
| DataSet 8 | 7,526 | 7,526 | 7,526 | 7,526 | **Complete** |
| DataSet 9 | 219,134 | 1,234 | — | — | **In Progress** |
| DataSet 10 | — | — | — | — | **In Progress** |

## Notes

- The application is idempotent. Re-running any mode is safe.
- To re-process a specific file from scratch, delete its `.done` file.
- DataSet folders are now processed in natural numeric order (1, 2, ... 9, 10, 11).
- Git origin: `https://github.com/bandicootisland/ABC.DiscoveryCity.git` (branch: Develop)
