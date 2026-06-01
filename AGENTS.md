# Discovery City

Legal document discovery and analysis platform for investigating large collections of court documents (Epstein/DOJ disclosure files). 729K+ documents with full-text search, AI semantic search, and automated metadata extraction.

## Architecture

```
Blazor WASM (5233) --> ASP.NET Core API (5022) --> PostgreSQL 16 + pgvector (5435)
                                                --> Ollama (11434, mxbai-embed-large:latest, 1024-dim)
                                                --> Elasticsearch (9200, optional)
```

| Project | Role |
|---------|------|
| `ABC.DiscoveryCity` | Blazor WebAssembly frontend. Telerik UI for Blazor 12.0.0. Search page with DataGrid, DocumentDetails detail component, AdminStats dashboard. |
| `ABC.DiscoveryCity.API` | ASP.NET Core REST API. Controllers: Search, Images, Imports, Metadata. Serves PDF bytes, thumbnails, spreadsheet conversion. |
| `ABC.DiscoveryCity.PostgreSQL` | Database layer via Dapper/Npgsql. `DbService.cs` is the main service (~104KB). pgvector for semantic search. |
| `ABC.DiscoveryCity.DocumentIngestionProcessing` | CLI tool for batch PDF ingestion. `Program.cs` (~58KB) orchestrates the pipeline. |
| `ABC.DiscoveryCity.TelerikProcessing` | PDF processing, thumbnail generation, image extraction, spreadsheet handling. |
| `ABC.DiscoveryCity.Embeddings` | Ollama embedding service (1024-dim vectors via mxbai-embed-large). |
| `ABC.DiscoveryCity.Words` / `.Words.Common` | Linguistic analysis, people extraction, grammar rules, ontology. |
| `ABC.DiscoveryCity.DocReader` | PDF text/image extraction console app with web scraping. |
| `ABC.DiscoveryCity.MariaDB` | Legacy MariaDB/MySQL data loaders (HathiTrust, OpenLibrary, CSV). |
| `ABC.DiscoveryCity.Downloader` | Torrent-based document downloading (MonoTorrent). |
| `ABC.DiscoveryCity.Importer` | Data import tool for initial DB loading. |
| `ABC.DiscoveryCity.Linker` | Document linking and deduplication. |
| `ABC.PdfProcessing.Syncfusion` | Syncfusion PDF utilities (alternative to Telerik). |
| `HathiIndexer` | HathiTrust library indexing (in Indexer folder). |

## Tech Stack

- .NET 10.0, C# 13 (nullable enable, implicit usings)
- Blazor WebAssembly with Telerik UI for Blazor 12.0.0
- PostgreSQL 16 + pgvector extension (Docker, port 5435)
- Ollama for embeddings (mxbai-embed-large:latest, 1024 dimensions)
- Dapper + Npgsql (no EF Core)
- Telerik Document Processing + iText7 + Syncfusion for PDFs
- Tesseract 5.2.0 for OCR
- SixLabors.ImageSharp / SkiaSharp for image processing

## Database Schema

- **ParentDocuments**: Id, FilePath (unique), Metadata (JSONB with People, Terms, etc.), DataSetId, ProcessedAt
- **DocumentChunks**: ChunkId, ParentId, ChunkIndex, TextContent, Embedding (vector(1024))
- **DocumentImages**: Id, ParentId, ImageType, ImageSize, FilePath, Width, Height
- **Sources / DataSets**: Hierarchical document collection organization

Connection: `Host=192.168.1.114;Port=5435;Database=discoverycity;User=discovery_user`

## Key Frontend Components

### SearchPage.razor (`ABC.DiscoveryCity/Pages/`)
Main search UI with Telerik DataGrid, paged results, dataset/name filters, exact match toggle. Uses `SearchService` with a client-side sliding-window page cache (5 pages, prefetches adjacent).

### DocumentDetails.razor (`ABC.DiscoveryCity/Shared/`)
Detail panel for a selected document. Three tabs: Overview (thumbnail + metadata + text preview), Viewer (PDF viewer / media player / spreadsheet viewer), Metadata (JSONB table).

Key design patterns in this component:
- **FileCategory enum** classifies documents (Pdf, Spreadsheet, Video, Audio, Unknown) based on file extension.
- **Category-driven switch expressions** control labels, icons, viewer tab title, content type labels throughout.
- **ViewerData** (byte[]) is lazy-loaded when the viewer tab is selected. `LoadedViewerPath` prevents re-fetching the same document.
- **Media file support**: Video (.avi, .mp4, .vob) and audio (.m4a) files use native HTML5 `<video>` / `<audio>` elements. The `MediaFilePath` property derives the .mp4 path from the document's file path.
- **Search term highlighting**: `HighlightSearchTerms(SearchQuery)` is applied to all text content and metadata values.
- **StripDrivePrefix**: Cleans machine-specific path prefixes from metadata display values.

### SearchService.cs (`ABC.DiscoveryCity/Services/`)
HTTP client wrapper for all API calls. Includes DTOs: `SearchResultDto`, `PagedSearchResult`, `ImageDto`, `SystemStatsDto`, `DataSetStatsDto`.

## Key Principles

- **Evidence preservation**: Raw OCR text is never modified. MIME artifacts (`=` replacing chars) are handled only in derived metadata fields (People, Title).
- **Idempotent processing**: All CLI modes safe to re-run. Flag files: `.done`, `.done.embeddings`, `.done.images`.
- **Telerik direct rendering** is the default for thumbnails (~5x faster than Playwright browser mode).

## Running

```bash
# API (port 5022)
cd ABC.DiscoveryCity.API && dotnet run

# Frontend (port 5233, connects to localhost:5022)
cd ABC.DiscoveryCity && dotnet run

# Full ingestion
cd ABC.DiscoveryCity.DocumentIngestionProcessing
dotnet run -- "DataSet 9" --no-images

# Reprocess metadata only (fast, reads from DB)
dotnet run -- --reprocess --no-images

# Backfill embeddings
dotnet run -- --embeddings-only
```

## Git

- Origin: `https://github.com/bandicootisland/ABC.DiscoveryCity.git`
- Main branch: `master`, development on `Develop`
- Primary development machine: Ubuntu NUC Mini2 (18TB drive)
- This repo may also be worked on from Windows machines
