# Discovery City PDF Processor

This tool processes PDF files from a root directory (and `DataSet*` subfolders), extracts text and metadata, deduces the date, saves the result as JSON, and inserts it into a PostgreSQL database with Vector/JSONB support.

## Setup

1.  **Database**:
    Ensure the PostgreSQL container is running:
    ```bash
    docker-compose up -d discovery-city-db
    ```
    The database runs on port **5433** to avoid conflicts with default PostgreSQL installations.

2.  **Dependencies**:
    The project uses `Npgsql` for database connectivity. It should be restored automatically on build.

## How to Run

Run the TestApp project:
```bash
dotnet run --project ABC.DiscoveryCity.TestApp
```

You will be prompted to confirm the root folder path (defaults to `S:\EpsteinFiles\DepartmentofJustice\DOJ_Disclosures\`).

## Features

-   **Folder Selection**: Automatically detects `DataSet 1`, `DataSet 2`, etc., within the root folder.
-   **PDF Processing**:
    -   Extracts metadata (Title, Author, Producer, Keywords).
    -   Extracts text content using Telerik.
    -   Deduces the document date from the text using heuristics.
-   **Output**:
    -   **JSON File**: Saved next to the original PDF as `[OriginalName]_[DeducedDate].json`.
    -   **Database**: Inserts into `Documents` table in `DiscoveryCity` database.
-   **State Management**: Creates a `.done` file next to processed PDFs to skip them on subsequent runs.
-   **Image-Only PDFs**: Text extraction is attempted. If empty, the JSON will contain empty text (future work: integrate OCR).

## Database Schema

Table `Documents`:
-   `Id`: Serial PK
-   `FilePath`: Text (Unique)
-   `Metadata`: JSONB (Indexed) - Contains all extracted info.
-   `Embedding`: Vector(1536) - For future semantic search.
-   `CreatedAt` / `ProcessedAt`: Timestamps.

## Notes

-   The application is idempotent. Re-running it will update existing database records but skip files with existing `.done` markers.
-   To re-process a file, delete its `.done` file.
