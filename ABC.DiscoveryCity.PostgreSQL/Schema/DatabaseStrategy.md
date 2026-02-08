# WordCity Database Strategy: Partitioned Vector Storage

## Overview
This document outlines the storage strategy for the WordCity ingestion engine. We utilize **PostgreSQL Declarative Partitioning** to balance the needs of high-performance vector search, data isolation, and manageability.

### The Problem
* **Unified Search:** We need to search across multiple books (e.g., "All Science Papers").
* **Isolation:** We need to isolate specific books or clients (e.g., "The Encyclopedia").
* **Performance:** Giant vector indexes (IVFFlat) become slow and hard to maintain as they grow into millions of rows.

### The Solution: Stable-ID Partitioning
We use a **Parent Table** (`smart_paragraphs`) that acts as a router. The actual data lives in **Child Tables** (Partitions), one per book.

* **Naming Convention:** Partitions are named using the Book ID (`smart_paragraphs_p_{id}`), not the title. This avoids the 63-character limit and handles special characters safely.
* **Routing:** Postgres automatically routes queries to the correct physical table based on the `book_id`.

---

## 1. Schema Definitions

### A. Metadata Table (`books`)
Holds the human-readable information. This table is small and stable.

```sql
CREATE TABLE IF NOT EXISTS books (
    id SERIAL PRIMARY KEY,
    title TEXT NOT NULL,
    author TEXT,
    category TEXT, -- e.g. 'Science', 'Fiction'
    import_status TEXT DEFAULT 'pending', -- 'indexed', 'failed', 'ready'
    created_at TIMESTAMP DEFAULT NOW()
);