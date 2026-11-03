using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>
/// Canonical, sorted dump of the user schema for the fingerprint, from pg_catalog. Definitions come from
/// PostgreSQL's own deparsers (pg_get_constraintdef, pg_get_indexdef, pg_get_viewdef, pg_get_functiondef,
/// pg_get_triggerdef) with an empty search_path, so every name is schema-qualified and identical databases dump
/// identically. Objects that belong to extensions, identity sequences, the bsm state schema and system schemas
/// are left out. Columns are numbered by position, not attnum (dropped columns leave gaps).
/// </summary>
public sealed class PostgreSqlSchemaInspector : ISchemaInspector
{
    private const string Sql = """
        WITH ns AS (
            SELECT oid, nspname FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog', 'information_schema') AND nspname NOT LIKE 'pg\_%' AND nspname <> @state),
        ext AS (SELECT objid FROM pg_depend WHERE deptype = 'e')
        SELECT 'schema|' || nspname AS line FROM ns

        UNION ALL
        SELECT 'extension|' || e.extname || '|' || n.nspname
        FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
        WHERE e.extname <> 'plpgsql'

        UNION ALL
        SELECT 'table|' || n.nspname || '.' || c.relname || '|' || c.relkind::text || '|' || c.relpersistence::text
               || '|' || COALESCE(pg_get_partkeydef(c.oid), '')
               || '|' || COALESCE((SELECT pn.nspname || '.' || p.relname FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent JOIN pg_namespace pn ON pn.oid = p.relnamespace WHERE i.inhrelid = c.oid ORDER BY 1 LIMIT 1), '')
               || '|' || COALESCE(pg_get_expr(c.relpartbound, c.oid), '')
               || '|' || c.relrowsecurity::text || '|' || c.relforcerowsecurity::text
        FROM pg_class c JOIN ns n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'f') AND c.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'column|' || n.nspname || '.' || c.relname || '|' || row_number() OVER (PARTITION BY c.oid ORDER BY a.attnum) || '|' || a.attname
               || '|' || format_type(a.atttypid, a.atttypmod) || '|' || a.attnotnull::text
               || '|' || COALESCE(pg_get_expr(d.adbin, d.adrelid), '') || '|' || a.attidentity::text || '|' || a.attgenerated::text
               || '|' || COALESCE(CASE WHEN a.attcollation <> t.typcollation THEN co.collname END, '')
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN ns n ON n.oid = c.relnamespace
        JOIN pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        LEFT JOIN pg_collation co ON co.oid = a.attcollation
        WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'f') AND c.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'constraint|' || n.nspname || '.' || c.relname || '|' || con.conname || '|' || pg_get_constraintdef(con.oid)
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN ns n ON n.oid = c.relnamespace
        WHERE con.contype IN ('p', 'u', 'f', 'c', 'x') AND c.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'index|' || n.nspname || '.' || t.relname || '|' || ic.relname || '|' || pg_get_indexdef(i.indexrelid)
        FROM pg_index i
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_class t ON t.oid = i.indrelid
        JOIN ns n ON n.oid = t.relnamespace
        WHERE t.oid NOT IN (SELECT objid FROM ext)
          AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid AND con.conrelid = i.indrelid AND con.contype IN ('p', 'u', 'x'))

        UNION ALL
        SELECT 'view|' || n.nspname || '.' || c.relname || '|' || c.relkind::text || '|' || pg_get_viewdef(c.oid)
        FROM pg_class c JOIN ns n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('v', 'm') AND c.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'function|' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')|' || pg_get_functiondef(p.oid)
        FROM pg_proc p JOIN ns n ON n.oid = p.pronamespace
        WHERE p.prokind IN ('f', 'p', 'w') AND p.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'trigger|' || n.nspname || '.' || c.relname || '|' || tg.tgname || '|' || pg_get_triggerdef(tg.oid)
        FROM pg_trigger tg
        JOIN pg_class c ON c.oid = tg.tgrelid
        JOIN ns n ON n.oid = c.relnamespace
        WHERE NOT tg.tgisinternal AND c.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'sequence|' || n.nspname || '.' || c.relname || '|' || format_type(s.seqtypid, NULL) || '|' || s.seqstart || '|' || s.seqincrement
               || '|' || s.seqmin || '|' || s.seqmax || '|' || s.seqcache || '|' || s.seqcycle::text
        FROM pg_sequence s
        JOIN pg_class c ON c.oid = s.seqrelid
        JOIN ns n ON n.oid = c.relnamespace
        WHERE c.oid NOT IN (SELECT objid FROM ext)
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'i')

        UNION ALL
        SELECT 'type|' || n.nspname || '.' || t.typname || '|' ||
               CASE t.typtype
                   WHEN 'e' THEN 'enum|' || COALESCE((SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = t.oid), '')
                   WHEN 'd' THEN 'domain|' || format_type(t.typbasetype, t.typtypmod) || '|' || t.typnotnull::text || '|' || COALESCE(t.typdefault, '')
                        || '|' || COALESCE((SELECT string_agg(conname || ' ' || pg_get_constraintdef(oid), ';' ORDER BY conname) FROM pg_constraint WHERE contypid = t.oid), '')
                   WHEN 'r' THEN 'range|' || COALESCE((SELECT format_type(rngsubtype, NULL) FROM pg_range WHERE rngtypid = t.oid), '')
                   ELSE 'composite|' || COALESCE((SELECT string_agg(attname || ' ' || format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = t.typrelid AND attnum > 0 AND NOT attisdropped), '')
               END
        FROM pg_type t
        JOIN ns n ON n.oid = t.typnamespace
        WHERE (t.typtype IN ('e', 'd', 'r') OR (t.typtype = 'c' AND (SELECT relkind FROM pg_class WHERE oid = t.typrelid) = 'c'))
          AND t.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'policy|' || n.nspname || '.' || c.relname || '|' || pol.polname || '|' || pol.polcmd::text || '|' || pol.polpermissive::text
               || '|' || COALESCE((SELECT string_agg(CASE WHEN r = 0 THEN 'public' ELSE (SELECT rolname FROM pg_roles WHERE oid = r) END, ',' ORDER BY 1) FROM unnest(pol.polroles) r), '')
               || '|' || COALESCE(pg_get_expr(pol.polqual, pol.polrelid), '') || '|' || COALESCE(pg_get_expr(pol.polwithcheck, pol.polrelid), '')
        FROM pg_policy pol
        JOIN pg_class c ON c.oid = pol.polrelid
        JOIN ns n ON n.oid = c.relnamespace;
        """;

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "schema", "extension", "table", "column", "constraint", "index", "view", "function", "trigger", "sequence", "type", "policy" };

    public async Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken)
    {
        // An empty search_path makes the deparsers qualify every name.
        var path = (string)(await session.QueryAsync("SELECT current_setting('search_path') AS p;", null, cancellationToken).ConfigureAwait(false))[0]["p"]!;
        await session.QueryAsync("SELECT set_config('search_path', '', false);", null, cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await session.QueryAsync(Sql, new Dictionary<string, object?> { ["state"] = stateSchema }, cancellationToken).ConfigureAwait(false);
            var lines = rows.Select(r => ((string)r["line"]!).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()).ToList();
            lines.Sort(StringComparer.Ordinal);
            return string.Join('\n', lines);
        }
        finally
        {
            await session.QueryAsync("SELECT set_config('search_path', @path, false);", new Dictionary<string, object?> { ["path"] = path }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Entries keyed by kind and object, e.g. "column public.customer.email", "function public.total(integer)".</summary>
    public IReadOnlyList<SchemaEntry> Parse(string dump)
    {
        var entries = new List<SchemaEntry>();
        var current = new List<string>();
        foreach (var line in dump.Split('\n'))
        {
            var bar = line.IndexOf('|', StringComparison.Ordinal);
            if (bar > 0 && Kinds.Contains(line[..bar]) && current.Count > 0)
            {
                entries.Add(Entry(current));
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Count > 0 && !(current.Count == 1 && current[0].Length == 0))
        {
            entries.Add(Entry(current));
        }

        return entries;

        static SchemaEntry Entry(List<string> lines)
        {
            var text = string.Join('\n', lines);
            var f = lines[0].Split('|');
            string Field(int i) => i < f.Length ? f[i] : string.Empty;
            var key = f[0] switch
            {
                "column" => $"column {Field(1)}.{Field(3)}",
                "constraint" or "index" or "trigger" or "policy" => $"{f[0]} {Field(1)}.{Field(2)}",
                _ => $"{f[0]} {Field(1)}",
            };
            return new SchemaEntry(key, text);
        }
    }
}
