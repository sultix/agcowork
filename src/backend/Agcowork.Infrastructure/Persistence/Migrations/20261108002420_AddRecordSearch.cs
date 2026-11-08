using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agcowork.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Full-text search (AP-026): <c>records.search_vector</c>, maintained by triggers, outside the EF model (a mapped column
    /// would be read with every record). Weighted title A, text-like properties and option labels/values B (the type of a
    /// property comes from the collection schema, hence a trigger instead of a generated column), body C. Only prefixes are
    /// indexed: 75000 characters of the body, 25000 of the property text (<c>RecordSearch.BodyPrefixLength</c>,
    /// <c>PropertiesPrefixLength</c>), so even the measured worst case (about 8 bytes per character) stays below PostgreSQL's
    /// 1 MB tsvector limit; should a vector still exceed it, shorter prefixes and finally the title alone are indexed, so no
    /// write and no run of this migration fails.
    /// Configuration <c>simple</c>: no stemming, no stop words. Relations, actors, numbers, dates, checkboxes and JSON
    /// payloads are not indexed. GIN index behind <c>workspace_id</c> (btree_gin, AP-007).
    /// </summary>
    public partial class AddRecordSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Text of the indexed properties in schema order: text, markdown, url and email values; for select, status and
            // multi_select the label and value of each chosen option. Everything else (relation and actor ids, numbers, dates,
            // json) stays out. Immutable: depends on its arguments only.
            migrationBuilder.Sql("""
                CREATE FUNCTION agcowork_search_properties_text(p_properties jsonb, p_schema jsonb)
                RETURNS text LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                  SELECT string_agg(part, E'\n' ORDER BY ord)
                  FROM (
                    SELECT d.ord,
                      CASE
                        WHEN d.def ->> 'type' IN ('text', 'markdown', 'url', 'email') THEN
                          CASE WHEN jsonb_typeof(p_properties -> (d.def ->> 'key')) = 'string' THEN p_properties ->> (d.def ->> 'key') END
                        WHEN d.def ->> 'type' IN ('select', 'status', 'multi_select') THEN (
                          SELECT string_agg(concat_ws(' ', o.opt ->> 'label', o.opt ->> 'value'), ' ' ORDER BY o.n)
                          FROM jsonb_array_elements(CASE WHEN jsonb_typeof(d.def -> 'options') = 'array' THEN d.def -> 'options' ELSE '[]'::jsonb END)
                            WITH ORDINALITY AS o(opt, n)
                          WHERE (jsonb_typeof(p_properties -> (d.def ->> 'key')) = 'string' AND o.opt ->> 'value' = p_properties ->> (d.def ->> 'key'))
                             OR (jsonb_typeof(p_properties -> (d.def ->> 'key')) = 'array'
                                 AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(p_properties -> (d.def ->> 'key')) AS chosen(item) WHERE chosen.item = o.opt ->> 'value')))
                      END AS part
                    FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_schema -> 'properties') = 'array' THEN p_schema -> 'properties' ELSE '[]'::jsonb END)
                      WITH ORDINALITY AS d(def, ord)
                  ) AS parts
                  WHERE part IS NOT NULL AND part <> ''
                $$;
                """);

            // The vector with the given prefix lengths. Worst case measured on PostgreSQL 17: 4-byte letters (CJK Ext. B) in
            // hyphenated compounds (the compound and each part become lexemes) take about 8 bytes per character; with the default
            // prefixes (500 + 25000 + 75000) × 8 ≈ 804 KB stays below the 1 MB limit (1048575 bytes) of a tsvector.
            migrationBuilder.Sql("""
                CREATE FUNCTION agcowork_record_search_vector_parts(
                  p_title text, p_properties jsonb, p_body text, p_schema jsonb, p_properties_length integer, p_body_length integer)
                RETURNS tsvector LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                  SELECT setweight(to_tsvector('simple'::regconfig, coalesce(p_title, '')), 'A')
                      || setweight(to_tsvector('simple'::regconfig, left(coalesce(agcowork_search_properties_text(p_properties, p_schema), ''), p_properties_length)), 'B')
                      || setweight(to_tsvector('simple'::regconfig, left(coalesce(p_body, ''), p_body_length)), 'C')
                $$;
                """);

            // Never fails a write or this migration: should the vector still exceed the limit (program_limit_exceeded), a tenth of
            // the prefixes is indexed, and in the end the title only. Not PARALLEL SAFE: exception blocks are subtransactions.
            migrationBuilder.Sql("""
                CREATE FUNCTION agcowork_record_search_vector(
                  p_title text, p_properties jsonb, p_body text, p_schema jsonb,
                  p_properties_length integer DEFAULT 25000, p_body_length integer DEFAULT 75000)
                RETURNS tsvector LANGUAGE plpgsql IMMUTABLE AS $$
                BEGIN
                  BEGIN
                    RETURN agcowork_record_search_vector_parts(p_title, p_properties, p_body, p_schema, p_properties_length, p_body_length);
                  EXCEPTION WHEN program_limit_exceeded THEN
                    NULL;
                  END;
                  BEGIN
                    RETURN agcowork_record_search_vector_parts(p_title, p_properties, p_body, p_schema, p_properties_length / 10, p_body_length / 10);
                  EXCEPTION WHEN program_limit_exceeded THEN
                    NULL;
                  END;
                  RETURN setweight(to_tsvector('simple'::regconfig, left(coalesce(p_title, ''), 500)), 'A');
                END
                $$;
                """);

            // The parts of a schema that change the vector: key, type and options of the indexed properties, in order.
            migrationBuilder.Sql("""
                CREATE FUNCTION agcowork_search_schema(p_schema jsonb)
                RETURNS jsonb LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                  SELECT coalesce(jsonb_agg(jsonb_build_array(d.def -> 'key', d.def -> 'type', d.def -> 'options') ORDER BY d.ord), '[]'::jsonb)
                  FROM jsonb_array_elements(CASE WHEN jsonb_typeof(p_schema -> 'properties') = 'array' THEN p_schema -> 'properties' ELSE '[]'::jsonb END)
                    WITH ORDINALITY AS d(def, ord)
                  WHERE d.def ->> 'type' IN ('text', 'markdown', 'url', 'email', 'select', 'status', 'multi_select')
                $$;
                """);

            // The text a search snippet is cut from: the same prefixes as the index (title excluded: it is shown anyway).
            migrationBuilder.Sql("""
                CREATE FUNCTION agcowork_record_search_document(p_workspace_id uuid, p_collection_id uuid, p_properties jsonb, p_body text)
                RETURNS text LANGUAGE sql STABLE PARALLEL SAFE AS $$
                  SELECT concat_ws(E'\n\n',
                    nullif(left(agcowork_search_properties_text(
                      p_properties,
                      (SELECT c.schema FROM collections c WHERE c.workspace_id = p_workspace_id AND c.id = p_collection_id)), 25000), ''),
                    nullif(left(p_body, 75000), ''))
                $$;
                """);

            migrationBuilder.Sql("ALTER TABLE records ADD COLUMN search_vector tsvector;");
            migrationBuilder.Sql("""
                UPDATE records r
                SET search_vector = agcowork_record_search_vector(
                  r.title, r.properties, r.body,
                  (SELECT c.schema FROM collections c WHERE c.workspace_id = r.workspace_id AND c.id = r.collection_id));
                """);
            migrationBuilder.Sql("ALTER TABLE records ALTER COLUMN search_vector SET NOT NULL;");

            // Every write of title, properties or body (EF updates, ExecuteUpdate clean-ups, inserts) recomputes the vector with
            // the collection's current schema. Updates of other columns (archive, revision) leave it alone.
            migrationBuilder.Sql("""
                CREATE FUNCTION agcowork_records_search_vector() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  NEW.search_vector := agcowork_record_search_vector(
                    NEW.title, NEW.properties, NEW.body,
                    (SELECT c.schema FROM collections c WHERE c.workspace_id = NEW.workspace_id AND c.id = NEW.collection_id));
                  RETURN NEW;
                END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_records_search_vector
                BEFORE INSERT OR UPDATE OF title, properties, body, collection_id ON records
                FOR EACH ROW EXECUTE FUNCTION agcowork_records_search_vector();
                """);

            // A schema change that affects the indexed text (property added, removed or retyped, options or labels changed)
            // re-indexes the records of the collection that carry a value under one of the changed keys, in the same transaction.
            // Records without such a value have the same property text as before. UpdateSchema holds the collection FOR UPDATE,
            // record writers take it FOR SHARE first, so the lock order collection → record holds. Limit: synchronous, in one
            // statement; a label change on a property that thousands of large records carry can run into the command timeout.
            migrationBuilder.Sql("""
                CREATE FUNCTION agcowork_collections_search_reindex() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE
                  changed text[];
                BEGIN
                  SELECT array_agg(coalesce(o.key, n.key)) INTO changed
                  FROM (SELECT e ->> 0 AS key, e FROM jsonb_array_elements(agcowork_search_schema(OLD.schema)) AS e) AS o
                  FULL JOIN (SELECT e ->> 0 AS key, e FROM jsonb_array_elements(agcowork_search_schema(NEW.schema)) AS e) AS n ON o.key = n.key
                  WHERE o.e IS DISTINCT FROM n.e;
                  IF changed IS NOT NULL THEN
                    UPDATE records r
                    SET search_vector = agcowork_record_search_vector(r.title, r.properties, r.body, NEW.schema)
                    WHERE r.workspace_id = NEW.workspace_id AND r.collection_id = NEW.id AND jsonb_exists_any(r.properties, changed);
                  END IF;
                  RETURN NULL;
                END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_collections_search_reindex
                AFTER UPDATE OF schema ON collections
                FOR EACH ROW EXECUTE FUNCTION agcowork_collections_search_reindex();
                """);

            migrationBuilder.Sql("CREATE INDEX ix_records_workspace_id_search_vector ON records USING gin (workspace_id uuid_ops, search_vector tsvector_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_collections_search_reindex ON collections;");
            migrationBuilder.Sql("DROP TRIGGER trg_records_search_vector ON records;");
            migrationBuilder.Sql("DROP INDEX ix_records_workspace_id_search_vector;");
            migrationBuilder.Sql("ALTER TABLE records DROP COLUMN search_vector;");
            migrationBuilder.Sql("DROP FUNCTION agcowork_collections_search_reindex();");
            migrationBuilder.Sql("DROP FUNCTION agcowork_records_search_vector();");
            migrationBuilder.Sql("DROP FUNCTION agcowork_record_search_document(uuid, uuid, jsonb, text);");
            migrationBuilder.Sql("DROP FUNCTION agcowork_search_schema(jsonb);");
            migrationBuilder.Sql("DROP FUNCTION agcowork_record_search_vector(text, jsonb, text, jsonb, integer, integer);");
            migrationBuilder.Sql("DROP FUNCTION agcowork_record_search_vector_parts(text, jsonb, text, jsonb, integer, integer);");
            migrationBuilder.Sql("DROP FUNCTION agcowork_search_properties_text(jsonb, jsonb);");
        }
    }
}
