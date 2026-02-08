CREATE OR REPLACE FUNCTION extract_dsl_text(structure jsonb) RETURNS text AS $$
DECLARE
    word text;
    clean_text text := '';
BEGIN
    FOR word IN SELECT * FROM jsonb_array_elements_text(structure)
    LOOP
        -- Strip suffixes (.u, .U, .n, .q, .e, .c, .x) from the end of the string
        -- Pattern matches one or more groups of (dot + char) at the end
        word := regexp_replace(word, '(\.[uUnqecx])+$', '');
        
        clean_text := clean_text || ' ' || word;
    END LOOP;
    
    RETURN trim(clean_text);
END;
$$ LANGUAGE plpgsql IMMUTABLE;
