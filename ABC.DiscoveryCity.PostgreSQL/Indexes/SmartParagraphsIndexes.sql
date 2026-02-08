-- Keyword Search Index
CREATE INDEX IF NOT EXISTS idx_smart_paragraphs_structure_gin 
ON smart_paragraphs USING GIN (to_tsvector('english', extract_dsl_text(structure)));

-- Vector Search Index
-- Note: IVFFlat requires the table to have sufficient data for good clustering (lists), 
-- but we create it here for schema completeness.
-- Ideally create it after data load or use HNSW for empty tables.
CREATE INDEX IF NOT EXISTS idx_smart_paragraphs_embedding_ivfflat 
ON smart_paragraphs USING ivfflat (embedding vector_cosine_ops);
