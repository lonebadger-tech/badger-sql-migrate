using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>
/// Canonical, sorted dump of the user schema for the fingerprint, from pg_catalog. Definitions come from
/// PostgreSQL's own deparsers (pg_get_constraintdef, pg_get_indexdef, pg_get_viewdef, pg_get_functiondef,
/// pg_get_triggerdef) with an empty search_path, so every name is schema-qualified and identical databases dump
/// identically. Objects that belong to extensions, identity sequences, the bsm state schema and system schemas
/// are left out, and so are object owners (they usually differ per environment). Columns are numbered by position,
/// not attnum (dropped columns leave gaps). Grants and partitions can be left out per environment.
/// </summary>
public sealed class PostgreSqlSchemaInspector : ISchemaInspector
{
    // @partitions = 0 leaves partitions (child tables) out; @privileges = 0 leaves grants out.
    private const string Sql = """
        WITH ns AS (
            SELECT oid, nspname FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog', 'information_schema') AND nspname NOT LIKE 'pg\_%' AND nspname <> @state),
        ext AS (SELECT objid FROM pg_depend WHERE deptype = 'e'),
        skip AS (SELECT oid FROM pg_class WHERE relispartition AND @partitions = 0),
        rel AS (
            SELECT c.* FROM pg_class c JOIN ns n ON n.oid = c.relnamespace
            WHERE c.oid NOT IN (SELECT objid FROM ext) AND c.oid NOT IN (SELECT oid FROM skip))
        SELECT 'schema|' || nspname AS line FROM ns

        UNION ALL
        SELECT 'extension|' || e.extname || '|' || n.nspname || '|' || e.extversion
        FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace
        WHERE e.extname <> 'plpgsql'

        UNION ALL
        SELECT 'table|' || n.nspname || '.' || c.relname || '|' || c.relkind::text || '|' || c.relpersistence::text
               || '|' || COALESCE(pg_get_partkeydef(c.oid), '')
               || '|' || COALESCE((SELECT string_agg(pn.nspname || '.' || p.relname, ',' ORDER BY i.inhseqno) FROM pg_inherits i JOIN pg_class p ON p.oid = i.inhparent JOIN pg_namespace pn ON pn.oid = p.relnamespace WHERE i.inhrelid = c.oid), '')
               || '|' || COALESCE(pg_get_expr(c.relpartbound, c.oid), '')
               || '|' || c.relrowsecurity::text || '|' || c.relforcerowsecurity::text || '|' || c.relreplident::text
               || '|' || COALESCE(array_to_string(c.reloptions, ','), '') || '|' || COALESCE((SELECT spcname FROM pg_tablespace WHERE oid = c.reltablespace), '')
        FROM rel c JOIN ns n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p', 'f')

        UNION ALL
        SELECT 'foreign table|' || n.nspname || '.' || c.relname || '|' || s.srvname || '|' || COALESCE(array_to_string(ft.ftoptions, ','), '')
        FROM pg_foreign_table ft
        JOIN rel c ON c.oid = ft.ftrelid
        JOIN ns n ON n.oid = c.relnamespace
        JOIN pg_foreign_server s ON s.oid = ft.ftserver

        UNION ALL
        SELECT 'column|' || n.nspname || '.' || c.relname || '|' || row_number() OVER (PARTITION BY c.oid ORDER BY a.attnum) || '|' || a.attname
               || '|' || format_type(a.atttypid, a.atttypmod) || '|' || a.attnotnull::text
               || '|' || COALESCE(pg_get_expr(d.adbin, d.adrelid), '') || '|' || a.attidentity::text || '|' || a.attgenerated::text
               || '|' || COALESCE(CASE WHEN a.attcollation <> t.typcollation THEN co.collname END, '')
               || CASE WHEN a.attoptions IS NOT NULL OR a.attstorage <> t.typstorage THEN '|' || COALESCE(array_to_string(a.attoptions, ','), '') || '|' || a.attstorage::text ELSE '' END
        FROM pg_attribute a
        JOIN rel c ON c.oid = a.attrelid
        JOIN ns n ON n.oid = c.relnamespace
        JOIN pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        LEFT JOIN pg_collation co ON co.oid = a.attcollation
        WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p', 'f')

        UNION ALL
        SELECT 'constraint|' || n.nspname || '.' || c.relname || '|' || con.conname || '|' || pg_get_constraintdef(con.oid)
        FROM pg_constraint con
        JOIN rel c ON c.oid = con.conrelid
        JOIN ns n ON n.oid = c.relnamespace
        WHERE con.contype IN ('p', 'u', 'f', 'c', 'x')

        UNION ALL
        SELECT 'index|' || n.nspname || '.' || t.relname || '|' || ic.relname || '|' || pg_get_indexdef(i.indexrelid)
               || '|' || COALESCE((SELECT spcname FROM pg_tablespace WHERE oid = ic.reltablespace), '')
        FROM pg_index i
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN rel t ON t.oid = i.indrelid
        JOIN ns n ON n.oid = t.relnamespace
        WHERE NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid AND con.conrelid = i.indrelid AND con.contype IN ('p', 'u', 'x'))

        UNION ALL
        SELECT 'view|' || n.nspname || '.' || c.relname || '|' || c.relkind::text || '|' || COALESCE(array_to_string(c.reloptions, ','), '') || '|' || pg_get_viewdef(c.oid)
        FROM rel c JOIN ns n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('v', 'm')

        UNION ALL
        SELECT 'function|' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')|' || pg_get_functiondef(p.oid)
        FROM pg_proc p JOIN ns n ON n.oid = p.pronamespace
        WHERE p.prokind IN ('f', 'p', 'w') AND p.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'aggregate|' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')|' || a.aggkind::text
               || '|' || a.aggtransfn::regproc::text || '|' || format_type(a.aggtranstype, NULL) || '|' || a.aggfinalfn::regproc::text
               || '|' || a.aggcombinefn::regproc::text || '|' || COALESCE(a.agginitval, '') || '|' || a.aggsortop::regoperator::text
        FROM pg_proc p
        JOIN pg_aggregate a ON a.aggfnoid = p.oid
        JOIN ns n ON n.oid = p.pronamespace
        WHERE p.prokind = 'a' AND p.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'operator|' || n.nspname || '.' || o.oprname || '(' || COALESCE(format_type(NULLIF(o.oprleft, 0), NULL), 'NONE') || ', ' || COALESCE(format_type(NULLIF(o.oprright, 0), NULL), 'NONE') || ')'
               || '|' || o.oprcode::regproc::text || '|' || format_type(o.oprresult, NULL) || '|' || o.oprcom::regoperator::text || '|' || o.oprnegate::regoperator::text
               || '|' || o.oprrest::regproc::text || '|' || o.oprjoin::regproc::text || '|' || o.oprcanmerge::text || '|' || o.oprcanhash::text
        FROM pg_operator o JOIN ns n ON n.oid = o.oprnamespace
        WHERE o.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'cast|' || format_type(c.castsource, NULL) || ' AS ' || format_type(c.casttarget, NULL) || '|' || c.castfunc::regprocedure::text
               || '|' || c.castcontext::text || '|' || c.castmethod::text
        FROM pg_cast c
        WHERE c.oid >= 16384 AND c.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'collation|' || n.nspname || '.' || co.collname || '|' || co.collprovider::text || '|' || co.collisdeterministic::text
               || '|' || COALESCE(co.collcollate, '') || '|' || COALESCE(co.collctype, '')
        FROM pg_collation co JOIN ns n ON n.oid = co.collnamespace
        WHERE co.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'trigger|' || n.nspname || '.' || c.relname || '|' || tg.tgname || '|' || pg_get_triggerdef(tg.oid) || '|' || tg.tgenabled::text
        FROM pg_trigger tg
        JOIN rel c ON c.oid = tg.tgrelid
        JOIN ns n ON n.oid = c.relnamespace
        WHERE NOT tg.tgisinternal

        UNION ALL
        SELECT 'rule|' || r.schemaname || '.' || r.tablename || '|' || r.rulename || '|' || r.definition
        FROM pg_rules r
        WHERE r.schemaname IN (SELECT nspname FROM ns)

        UNION ALL
        SELECT 'event trigger|' || e.evtname || '|' || e.evtevent || '|' || e.evtfoid::regprocedure::text || '|' || e.evtenabled::text
               || '|' || COALESCE(array_to_string(e.evttags, ','), '')
        FROM pg_event_trigger e
        WHERE e.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'statistics|' || n.nspname || '.' || st.stxname || '|' || pg_get_statisticsobjdef(st.oid)
        FROM pg_statistic_ext st
        JOIN ns n ON n.oid = st.stxnamespace
        WHERE st.stxrelid NOT IN (SELECT oid FROM skip)

        UNION ALL
        SELECT 'sequence|' || n.nspname || '.' || c.relname || '|' || format_type(s.seqtypid, NULL) || '|' || s.seqstart || '|' || s.seqincrement
               || '|' || s.seqmin || '|' || s.seqmax || '|' || s.seqcache || '|' || s.seqcycle::text
        FROM pg_sequence s
        JOIN rel c ON c.oid = s.seqrelid
        JOIN ns n ON n.oid = c.relnamespace
        WHERE NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'i')

        UNION ALL
        SELECT 'type|' || n.nspname || '.' || t.typname || '|' ||
               CASE t.typtype
                   WHEN 'e' THEN 'enum|' || COALESCE((SELECT string_agg(enumlabel, ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = t.oid), '')
                   WHEN 'd' THEN 'domain|' || format_type(t.typbasetype, t.typtypmod) || '|' || t.typnotnull::text || '|' || COALESCE(t.typdefault, '')
                        || '|' || COALESCE((SELECT string_agg(conname || ' ' || pg_get_constraintdef(oid), ';' ORDER BY conname) FROM pg_constraint WHERE contypid = t.oid AND contype <> 'n'), '')
                   WHEN 'r' THEN 'range|' || COALESCE((SELECT format_type(rngsubtype, NULL) FROM pg_range WHERE rngtypid = t.oid), '')
                   ELSE 'composite|' || COALESCE((SELECT string_agg(attname || ' ' || format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = t.typrelid AND attnum > 0 AND NOT attisdropped), '')
               END
        FROM pg_type t
        JOIN ns n ON n.oid = t.typnamespace
        WHERE (t.typtype IN ('e', 'd', 'r') OR (t.typtype = 'c' AND (SELECT relkind FROM pg_class WHERE oid = t.typrelid) = 'c'))
          AND t.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'text search configuration|' || n.nspname || '.' || c.cfgname || '|' || (SELECT prsname FROM pg_ts_parser WHERE oid = c.cfgparser)
               || '|' || COALESCE((SELECT string_agg(m.maptokentype || ':' || m.mapdict::regdictionary::text, ',' ORDER BY m.maptokentype, m.mapseqno) FROM pg_ts_config_map m WHERE m.mapcfg = c.oid), '')
        FROM pg_ts_config c JOIN ns n ON n.oid = c.cfgnamespace
        WHERE c.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'text search dictionary|' || n.nspname || '.' || d.dictname || '|' || (SELECT tmplname FROM pg_ts_template WHERE oid = d.dicttemplate) || '|' || COALESCE(d.dictinitoption, '')
        FROM pg_ts_dict d JOIN ns n ON n.oid = d.dictnamespace
        WHERE d.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'policy|' || n.nspname || '.' || c.relname || '|' || pol.polname || '|' || pol.polcmd::text || '|' || pol.polpermissive::text
               || '|' || COALESCE((SELECT string_agg(CASE WHEN r = 0 THEN 'public' ELSE (SELECT rolname FROM pg_roles WHERE oid = r) END, ',' ORDER BY 1) FROM unnest(pol.polroles) r), '')
               || '|' || COALESCE(pg_get_expr(pol.polqual, pol.polrelid), '') || '|' || COALESCE(pg_get_expr(pol.polwithcheck, pol.polrelid), '')
        FROM pg_policy pol
        JOIN rel c ON c.oid = pol.polrelid
        JOIN ns n ON n.oid = c.relnamespace

        UNION ALL
        SELECT 'publication|' || p.pubname || '|' || p.puballtables::text || '|' || p.pubinsert::text || ',' || p.pubupdate::text || ',' || p.pubdelete::text || ',' || p.pubtruncate::text
               || '|' || COALESCE((SELECT string_agg(pt.schemaname || '.' || pt.tablename, ',' ORDER BY 1) FROM pg_publication_tables pt WHERE pt.pubname = p.pubname), '')
        FROM pg_publication p

        UNION ALL
        SELECT 'subscription|' || s.subname || '|' || s.subenabled::text || '|' || array_to_string(s.subpublications, ',')
        FROM pg_subscription s
        WHERE s.subdbid = (SELECT oid FROM pg_database WHERE datname = current_database())

        UNION ALL
        SELECT 'foreign data wrapper|' || w.fdwname || '|' || w.fdwhandler::regproc::text || '|' || w.fdwvalidator::regproc::text || '|' || COALESCE(array_to_string(w.fdwoptions, ','), '')
        FROM pg_foreign_data_wrapper w
        WHERE w.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'server|' || s.srvname || '|' || w.fdwname || '|' || COALESCE(s.srvtype, '') || '|' || COALESCE(s.srvversion, '') || '|' || COALESCE(array_to_string(s.srvoptions, ','), '')
        FROM pg_foreign_server s JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw
        WHERE s.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'comment|' || o.type || ' ' || o.identity || '|' || d.description
        FROM pg_description d
        CROSS JOIN LATERAL pg_identify_object(d.classoid, d.objoid, d.objsubid) o
        WHERE (o.schema IN (SELECT nspname FROM ns) OR (d.classoid = 'pg_namespace'::regclass AND d.objoid IN (SELECT oid FROM ns)))
          AND d.objoid NOT IN (SELECT objid FROM ext) AND d.objoid NOT IN (SELECT oid FROM skip)

        UNION ALL
        SELECT 'grant|' || o.type || ' ' || o.identity || '|' || CASE WHEN a.grantee = 0 THEN 'public' ELSE pg_get_userbyid(a.grantee) END
               || '|' || a.privilege_type || CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END
        FROM rel c
        CROSS JOIN LATERAL aclexplode(c.relacl) a
        CROSS JOIN LATERAL pg_identify_object('pg_class'::regclass, c.oid, 0) o
        WHERE @privileges = 1 AND c.relacl IS NOT NULL AND a.grantee <> c.relowner AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')

        UNION ALL
        SELECT 'grant|' || o.type || ' ' || o.identity || '|' || CASE WHEN a.grantee = 0 THEN 'public' ELSE pg_get_userbyid(a.grantee) END
               || '|' || a.privilege_type || CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END
        FROM pg_attribute att
        JOIN rel c ON c.oid = att.attrelid
        CROSS JOIN LATERAL aclexplode(att.attacl) a
        CROSS JOIN LATERAL pg_identify_object('pg_class'::regclass, c.oid, att.attnum) o
        WHERE @privileges = 1 AND att.attacl IS NOT NULL AND att.attnum > 0 AND a.grantee <> c.relowner

        UNION ALL
        SELECT 'grant|' || o.type || ' ' || o.identity || '|' || CASE WHEN a.grantee = 0 THEN 'public' ELSE pg_get_userbyid(a.grantee) END
               || '|' || a.privilege_type || CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END
        FROM pg_proc p
        JOIN ns n ON n.oid = p.pronamespace
        CROSS JOIN LATERAL aclexplode(p.proacl) a
        CROSS JOIN LATERAL pg_identify_object('pg_proc'::regclass, p.oid, 0) o
        WHERE @privileges = 1 AND p.proacl IS NOT NULL AND a.grantee <> p.proowner AND p.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'grant|schema ' || n.nspname || '|' || CASE WHEN a.grantee = 0 THEN 'public' ELSE pg_get_userbyid(a.grantee) END
               || '|' || a.privilege_type || CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END
        FROM pg_namespace n
        CROSS JOIN LATERAL aclexplode(n.nspacl) a
        WHERE @privileges = 1 AND n.oid IN (SELECT oid FROM ns) AND n.nspacl IS NOT NULL AND a.grantee <> n.nspowner

        UNION ALL
        SELECT 'grant|' || o.type || ' ' || o.identity || '|' || CASE WHEN a.grantee = 0 THEN 'public' ELSE pg_get_userbyid(a.grantee) END
               || '|' || a.privilege_type || CASE WHEN a.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END
        FROM pg_type t
        JOIN ns n ON n.oid = t.typnamespace
        CROSS JOIN LATERAL aclexplode(t.typacl) a
        CROSS JOIN LATERAL pg_identify_object('pg_type'::regclass, t.oid, 0) o
        WHERE @privileges = 1 AND t.typacl IS NOT NULL AND a.grantee <> t.typowner AND t.oid NOT IN (SELECT objid FROM ext)

        UNION ALL
        SELECT 'default privileges|' || pg_get_userbyid(d.defaclrole) || '|' || COALESCE(n.nspname, '') || '|' || d.defaclobjtype::text || '|' || array_to_string(d.defaclacl, ',')
        FROM pg_default_acl d
        LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace
        WHERE @privileges = 1 AND (d.defaclnamespace = 0 OR d.defaclnamespace IN (SELECT oid FROM ns))

        UNION ALL
        SELECT 'user mapping|' || um.srvname || '|' || um.usename
        FROM pg_user_mappings um
        WHERE @privileges = 1;
        """;

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "schema", "extension", "table", "foreign table", "column", "constraint", "index", "view", "function", "aggregate", "operator", "cast",
        "collation", "trigger", "rule", "event trigger", "statistics", "sequence", "type", "text search configuration", "text search dictionary",
        "policy", "publication", "subscription", "foreign data wrapper", "server", "comment", "grant", "default privileges", "user mapping",
    };

    public async Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken)
    {
        var settings = session is PostgreSqlSession pg ? pg.Fingerprint : PostgreSqlFingerprintSettings.Default;
        var parameters = new Dictionary<string, object?>
        {
            ["state"] = stateSchema,
            ["partitions"] = settings.PartitionBoundaries == PartitionBoundaries.Ignore ? 0 : 1,
            ["privileges"] = settings.Privileges == FingerprintPart.Ignore ? 0 : 1,
        };

        // An empty search_path makes the deparsers qualify every name. It is set for this transaction only, so the
        // session (and, behind a pooler, other clients) never sees it.
        var own = !session.InTransaction;
        string? previous = null;
        if (own)
        {
            await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            previous = (string)(await session.QueryAsync("SELECT current_setting('search_path') AS p;", null, cancellationToken).ConfigureAwait(false))[0]["p"]!;
        }

        try
        {
            await session.QueryAsync("SELECT set_config('search_path', '', true);", null, cancellationToken).ConfigureAwait(false);
            var rows = await session.QueryAsync(Sql, parameters, cancellationToken).ConfigureAwait(false);
            var lines = rows.Select(r => ((string)r["line"]!).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd()).ToList();
            lines.Sort(StringComparer.Ordinal);
            return string.Join('\n', lines);
        }
        finally
        {
            if (own)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await session.QueryAsync("SELECT set_config('search_path', @path, true);", new Dictionary<string, object?> { ["path"] = previous }, CancellationToken.None).ConfigureAwait(false);
            }
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
                "constraint" or "index" or "trigger" or "policy" or "rule" or "user mapping" => $"{f[0]} {Field(1)}.{Field(2)}",
                "grant" => $"grant {Field(1)} to {Field(2)} {Field(3)}",
                "default privileges" => $"default privileges {Field(1)} {Field(2)} {Field(3)}",
                _ => $"{f[0]} {Field(1)}",
            };
            return new SchemaEntry(key, text);
        }
    }
}
