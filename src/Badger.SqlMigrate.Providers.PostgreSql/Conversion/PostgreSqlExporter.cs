using System.Globalization;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Conversion;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.PostgreSql.Scripting;

namespace Badger.SqlMigrate.Providers.PostgreSql.Conversion;

/// <summary>
/// Reads a PostgreSQL database into a <see cref="PortableSchema"/> from pg_catalog, with an empty search_path so types,
/// defaults and definitions come back schema-qualified. Serial columns (a default from a sequence the column owns)
/// become identities. Objects owned by extensions, partitions, exclusion constraints, aggregates, operators and routines
/// in languages other than SQL and PL/pgSQL are reported instead of converted.
/// </summary>
public sealed partial class PostgreSqlExporter : ISchemaExporter
{
    public async Task<PortableSchema> ExportAsync(IDatabaseSession session, ExportOptions options, CancellationToken cancellationToken)
    {
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
            return await ReadAsync(session, options, cancellationToken).ConfigureAwait(false);
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

    private static async Task<PortableSchema> ReadAsync(IDatabaseSession session, ExportOptions options, CancellationToken ct)
    {
        var p = new Dictionary<string, object?> { ["state"] = options.StateSchema };
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) => await session.QueryAsync(Ns + sql, p, ct).ConfigureAwait(false);

        var schema = new PortableSchema { SourceProvider = PostgreSqlProvider.ProviderId, DefaultSchema = "public" };
        foreach (var r in await Q("SELECT nspname FROM ns WHERE nspname <> 'public' ORDER BY nspname;"))
        {
            schema.Schemas.Add(S(r["nspname"])!);
        }

        bool Excluded(string s, string n) => options.Excluded(new PortableName(s, n));

        // Tables.
        var tables = new Dictionary<long, (PortableName Name, string? Comment)>();
        foreach (var r in await Q(TablesSql))
        {
            var name = new PortableName(S(r["nspname"])!, S(r["relname"])!);
            if (Excluded(name.Schema, name.Name))
            {
                continue;
            }

            if (S(r["relkind"]) == "p")
            {
                schema.Warnings.Add($"{name}: partitioning is not converted; it becomes one table holding every partition's rows.");
            }

            tables[L(r["oid"])] = (name, S(r["comment"]));
        }

        foreach (var r in await Q(PartitionsSql))
        {
            schema.Warnings.Add($"{S(r["nspname"])}.{S(r["relname"])}: partition of {S(r["parent"])}; its rows are converted with the parent table.");
        }

        // Columns.
        var columns = new Dictionary<long, List<PortableColumn>>();
        var ownedSequences = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in await Q(ColumnsSql))
        {
            var oid = L(r["attrelid"]);
            if (!tables.ContainsKey(oid))
            {
                continue;
            }

            var def = S(r["def"]);
            var identity = S(r["attidentity"]) is "a" or "d" ? new PortableIdentity(L(r["seqstart"]), L(r["seqincrement"]), S(r["attidentity"]) == "a") : null;
            if (identity is null && S(r["serial_seq"]) is { } serial && def is not null && def.StartsWith("nextval(", StringComparison.Ordinal))
            {
                // serial / bigserial: the sequence belongs to the column.
                identity = new PortableIdentity(L(r["seqstart"]), L(r["seqincrement"]), false);
                ownedSequences.Add(serial);
                def = null;
            }

            var generated = S(r["attgenerated"]) == "s";
            if (!columns.TryGetValue(oid, out var list))
            {
                columns[oid] = list = [];
            }

            list.Add(new PortableColumn(S(r["attname"])!, S(r["type"])!, !B(r["attnotnull"]))
            {
                Default = generated ? null : def,
                Computed = generated ? def : null,
                ComputedStored = generated,
                Identity = identity,
                Collation = S(r["collation"]),
                Comment = S(r["comment"]),
            });
        }

        // Constraints.
        var constraintColumns = (await Q(ConstraintColumnsSql)).GroupBy(r => L(r["oid"]))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => L(r["ord"])).Select(r => (Column: S(r["attname"])!, Referenced: S(r["fattname"]))).ToList());
        var primary = new Dictionary<long, PortableKey>();
        var uniques = new Dictionary<long, List<PortableKey>>();
        var checks = new Dictionary<long, List<PortableCheck>>();
        var foreign = new Dictionary<long, List<PortableForeignKey>>();
        foreach (var r in await Q(ConstraintsSql))
        {
            var table = L(r["conrelid"]);
            if (!tables.TryGetValue(table, out var t))
            {
                continue;
            }

            var cols = constraintColumns.GetValueOrDefault(L(r["oid"])) ?? [];
            var name = S(r["conname"]);
            switch (S(r["contype"]))
            {
                case "p":
                    primary[table] = new PortableKey(name, cols.Select(c => c.Column).ToList());
                    break;
                case "u":
                    Add(uniques, table, new PortableKey(name, cols.Select(c => c.Column).ToList()));
                    break;
                case "c":
                    Add(checks, table, new PortableCheck(name, S(r["check_expr"])!));
                    break;
                case "f":
                    Add(foreign, table, new PortableForeignKey(name, cols.Select(c => c.Column).ToList(), new PortableName(S(r["ref_schema"])!, S(r["ref_table"])!),
                        cols.Select(c => c.Referenced!).ToList(), Action(S(r["confdeltype"])), Action(S(r["confupdtype"]))));
                    break;
                case "x":
                    schema.Warnings.Add($"{t.Name}: exclusion constraint {name} ({S(r["def"])}) is not converted.");
                    break;
            }
        }

        // Indexes that do not back a constraint.
        var indexColumns = (await Q(IndexColumnsSql)).GroupBy(r => L(r["indexrelid"])).ToDictionary(g => g.Key, g => g.OrderBy(r => L(r["n"])).ToList());
        var indexes = new Dictionary<long, List<PortableIndex>>();
        foreach (var r in await Q(IndexesSql))
        {
            var table = L(r["indrelid"]);
            if (!tables.ContainsKey(table))
            {
                continue;
            }

            var cols = indexColumns.GetValueOrDefault(L(r["indexrelid"])) ?? [];
            var method = S(r["amname"]);
            Add(indexes, table, new PortableIndex(
                S(r["relname"])!,
                B(r["indisunique"]),
                cols.Where(c => !B(c["included"])).Select(c => B(c["is_expr"]) ? new PortableIndexColumn(S(c["coldef"])!, true, B(c["descending"])) : new PortableIndexColumn(S(c["attname"])!, false, B(c["descending"]))).ToList(),
                cols.Where(c => B(c["included"])).Select(c => S(c["attname"])!).ToList(),
                S(r["pred"]),
                method == "btree" ? null : method));
        }

        foreach (var (oid, (name, comment)) in tables.OrderBy(t => t.Value.Name.Schema, StringComparer.Ordinal).ThenBy(t => t.Value.Name.Name, StringComparer.Ordinal))
        {
            schema.Tables.Add(new PortableTable(name, columns.GetValueOrDefault(oid) ?? [], primary.GetValueOrDefault(oid), uniques.GetValueOrDefault(oid) ?? [],
                checks.GetValueOrDefault(oid) ?? [], foreign.GetValueOrDefault(oid) ?? [], indexes.GetValueOrDefault(oid) ?? [])
            {
                Comment = comment,
            });
        }

        // Types.
        var labels = (await Q(EnumLabelsSql)).GroupBy(r => L(r["enumtypid"])).ToDictionary(g => g.Key, g => g.Select(r => S(r["enumlabel"])!).ToList());
        foreach (var r in await Q(EnumsSql))
        {
            schema.Enums.Add(new PortableEnum(new PortableName(S(r["nspname"])!, S(r["typname"])!), labels.GetValueOrDefault(L(r["oid"])) ?? []));
        }

        var domainChecks = (await Q(DomainChecksSql)).GroupBy(r => L(r["contypid"]))
            .ToDictionary(g => g.Key, g => g.Select(r => new PortableCheck(S(r["conname"]), CheckRegex().Match(S(r["def"])!) is { Success: true } m ? m.Groups[1].Value : S(r["def"])!)).ToList());
        foreach (var r in await Q(DomainsSql))
        {
            schema.Domains.Add(new PortableDomain(new PortableName(S(r["nspname"])!, S(r["typname"])!), S(r["base"])!, !B(r["typnotnull"]), S(r["typdefault"]),
                domainChecks.GetValueOrDefault(L(r["oid"])) ?? []));
        }

        foreach (var r in await Q(CompositesSql))
        {
            schema.Warnings.Add($"composite type {S(r["nspname"])}.{S(r["typname"])} is not converted.");
        }

        // Sequences that are not a column's own.
        foreach (var r in await Q(SequencesSql))
        {
            var name = new PortableName(S(r["nspname"])!, S(r["relname"])!);
            if (ownedSequences.Contains($"{name.Schema}.{name.Name}") || Excluded(name.Schema, name.Name))
            {
                continue;
            }

            schema.Sequences.Add(new PortableSequence(name, S(r["type"])!, L(r["seqstart"]), L(r["seqincrement"]), L(r["seqmin"]), L(r["seqmax"]), B(r["seqcycle"])));
        }

        // Code: PL/pgSQL routines first (their bodies are not checked against other objects when created), then SQL
        // routines and views in dependency order, then triggers.
        var code = new List<PortableCode>();
        var later = new List<PortableCode>();
        foreach (var r in await Q(RoutinesSql))
        {
            var name = new PortableName(S(r["nspname"])!, S(r["proname"])!);
            if (Excluded(name.Schema, name.Name))
            {
                continue;
            }

            var language = S(r["lanname"])!;
            var kind = S(r["prokind"]);
            if (kind == "a" || kind == "w")
            {
                schema.Warnings.Add($"{(kind == "a" ? "aggregate" : "window function")} {name} is not converted.");
                continue;
            }

            if (language is not ("plpgsql" or "sql"))
            {
                schema.Warnings.Add($"function {name} is written in {language} and is not converted.");
                continue;
            }

            var item = new PortableCode(kind == "p" ? PortableCodeKind.Procedure : PortableCodeKind.Function, name, S(r["def"])!) { Language = language };
            (language == "plpgsql" ? code : later).Add(item);
        }

        var viewDependencies = (await Q(ViewDependenciesSql)).GroupBy(r => L(r["view_oid"]))
            .ToDictionary(g => g.Key, g => g.Select(r => new PortableName(S(r["ref_schema"])!, S(r["ref_name"])!).Key).Distinct().ToList());
        foreach (var r in await Q(ViewsSql))
        {
            var name = new PortableName(S(r["nspname"])!, S(r["relname"])!);
            if (Excluded(name.Schema, name.Name))
            {
                continue;
            }

            var materialized = S(r["relkind"]) == "m";
            var definition = $"CREATE {(materialized ? "MATERIALIZED " : string.Empty)}VIEW {Sql.Ident(name.Schema)}.{Sql.Ident(name.Name)} AS\n{S(r["def"])!.TrimEnd().TrimEnd(';')};";
            later.Add(new PortableCode(PortableCodeKind.View, name, definition)
            {
                Materialized = materialized,
                Language = "sql",
                DependsOn = viewDependencies.GetValueOrDefault(L(r["oid"])) ?? [],
            });
        }

        foreach (var r in await Q(TriggersSql))
        {
            var table = new PortableName(S(r["nspname"])!, S(r["relname"])!);
            if (Excluded(table.Schema, table.Name))
            {
                continue;
            }

            later.Add(new PortableCode(PortableCodeKind.Trigger, new PortableName(table.Schema, S(r["tgname"])!), S(r["def"])! + ";")
            {
                Table = table,
                TriggerFunction = new PortableName(S(r["fn_schema"])!, S(r["fn_name"])!),
                Language = "plpgsql",
            });
        }

        code.AddRange(later);
        schema.Code.AddRange(CodeOrder.Sort(code));
        return schema;
    }

    private static void Add<T>(Dictionary<long, List<T>> map, long key, T value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.Add(value);
    }

    private static string Action(string? code) => code switch
    {
        "r" => "RESTRICT",
        "c" => "CASCADE",
        "n" => "SET NULL",
        "d" => "SET DEFAULT",
        _ => "NO ACTION",
    };

    private static string? S(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static long L(object? value) => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static bool B(object? value) => value is true;

    [GeneratedRegex(@"\ACHECK\s*\((.*)\)\s*\z", RegexOptions.Singleline)]
    private static partial Regex CheckRegex();

    /// <summary>User schemas: not system, not bsm's journal, not created by an extension.</summary>
    private const string Ns = """
        WITH ns AS (
            SELECT oid, nspname FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog', 'information_schema') AND nspname NOT LIKE 'pg\_%' AND nspname <> @state
              AND oid NOT IN (SELECT objid FROM pg_depend WHERE deptype = 'e' AND classid = 'pg_namespace'::regclass)),
        ext AS (SELECT objid FROM pg_depend WHERE deptype = 'e')

        """;

    private const string TablesSql = """
        SELECT c.oid, n.nspname, c.relname, c.relkind::text AS relkind, obj_description(c.oid, 'pg_class') AS comment
        FROM pg_class c JOIN ns n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p') AND NOT c.relispartition AND c.oid NOT IN (SELECT objid FROM ext);
        """;

    private const string PartitionsSql = """
        SELECT n.nspname, c.relname, (SELECT pn.nspname || '.' || pc.relname FROM pg_inherits i JOIN pg_class pc ON pc.oid = i.inhparent JOIN pg_namespace pn ON pn.oid = pc.relnamespace WHERE i.inhrelid = c.oid LIMIT 1) AS parent
        FROM pg_class c JOIN ns n ON n.oid = c.relnamespace
        WHERE c.relispartition AND c.relkind IN ('r', 'p');
        """;

    private const string ColumnsSql = """
        SELECT a.attrelid, a.attnum, a.attname, format_type(a.atttypid, a.atttypmod) AS type, a.attnotnull,
               pg_get_expr(d.adbin, d.adrelid) AS def, a.attidentity::text AS attidentity, a.attgenerated::text AS attgenerated,
               CASE WHEN a.attcollation <> t.typcollation THEN co.collname END AS collation,
               col_description(a.attrelid, a.attnum) AS comment,
               sq.seq_name AS serial_seq, s.seqstart, s.seqincrement
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace cn ON cn.oid = c.relnamespace
        JOIN pg_type t ON t.oid = a.atttypid
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        LEFT JOIN pg_collation co ON co.oid = a.attcollation
        LEFT JOIN LATERAL (
            SELECT sn.nspname || '.' || sc.relname AS seq_name, sc.oid AS seq_oid
            FROM pg_depend dep JOIN pg_class sc ON sc.oid = dep.objid JOIN pg_namespace sn ON sn.oid = sc.relnamespace
            WHERE dep.refobjid = a.attrelid AND dep.refobjsubid = a.attnum AND dep.classid = 'pg_class'::regclass
              AND dep.refclassid = 'pg_class'::regclass AND sc.relkind = 'S' AND dep.deptype IN ('a', 'i')
            LIMIT 1) sq ON true
        LEFT JOIN pg_sequence s ON s.seqrelid = sq.seq_oid
        WHERE a.attnum > 0 AND NOT a.attisdropped AND c.relkind IN ('r', 'p') AND cn.oid IN (SELECT oid FROM ns)
        ORDER BY a.attrelid, a.attnum;
        """;

    private const string ConstraintsSql = """
        SELECT con.oid, con.conrelid, con.conname, con.contype::text AS contype, con.confdeltype::text AS confdeltype, con.confupdtype::text AS confupdtype,
               rn.nspname AS ref_schema, rc.relname AS ref_table, pg_get_constraintdef(con.oid) AS def,
               CASE WHEN con.contype = 'c' THEN pg_get_expr(con.conbin, con.conrelid) END AS check_expr
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN ns n ON n.oid = c.relnamespace
        LEFT JOIN pg_class rc ON rc.oid = con.confrelid
        LEFT JOIN pg_namespace rn ON rn.oid = rc.relnamespace
        WHERE con.contype IN ('p', 'u', 'c', 'f', 'x') AND con.conislocal
        ORDER BY con.conrelid, con.contype, con.conname;
        """;

    private const string ConstraintColumnsSql = """
        SELECT con.oid, k.ord, a.attname, fa.attname AS fattname
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN ns n ON n.oid = c.relnamespace
        CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
        JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum
        LEFT JOIN pg_attribute fa ON fa.attrelid = con.confrelid AND fa.attnum = con.confkey[k.ord]
        WHERE con.contype IN ('p', 'u', 'f');
        """;

    private const string IndexesSql = """
        SELECT i.indexrelid, i.indrelid, ic.relname, i.indisunique, am.amname, pg_get_expr(i.indpred, i.indrelid, true) AS pred
        FROM pg_index i
        JOIN pg_class ic ON ic.oid = i.indexrelid
        JOIN pg_am am ON am.oid = ic.relam
        JOIN pg_class t ON t.oid = i.indrelid
        JOIN ns n ON n.oid = t.relnamespace
        WHERE NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid AND con.contype IN ('p', 'u', 'x'))
        ORDER BY ic.relname;
        """;

    private const string IndexColumnsSql = """
        SELECT i.indexrelid, k.n, pg_get_indexdef(i.indexrelid, k.n, true) AS coldef, i.indkey[k.n - 1] = 0 AS is_expr,
               (i.indoption[k.n - 1] & 1) = 1 AS descending, k.n > i.indnkeyatts AS included, a.attname
        FROM pg_index i
        JOIN pg_class t ON t.oid = i.indrelid
        JOIN ns n ON n.oid = t.relnamespace
        CROSS JOIN LATERAL generate_series(1, i.indnatts) AS k(n)
        LEFT JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = i.indkey[k.n - 1];
        """;

    private const string EnumsSql = """
        SELECT t.oid, n.nspname, t.typname FROM pg_type t JOIN ns n ON n.oid = t.typnamespace
        WHERE t.typtype = 'e' AND t.oid NOT IN (SELECT objid FROM ext) ORDER BY n.nspname, t.typname;
        """;

    private const string EnumLabelsSql = "SELECT enumtypid, enumlabel FROM pg_enum ORDER BY enumtypid, enumsortorder;";

    private const string DomainsSql = """
        SELECT t.oid, n.nspname, t.typname, format_type(t.typbasetype, t.typtypmod) AS base, t.typnotnull, t.typdefault
        FROM pg_type t JOIN ns n ON n.oid = t.typnamespace
        WHERE t.typtype = 'd' AND t.oid NOT IN (SELECT objid FROM ext) ORDER BY n.nspname, t.typname;
        """;

    private const string DomainChecksSql = "SELECT contypid, conname, pg_get_constraintdef(oid) AS def FROM pg_constraint WHERE contypid <> 0 AND contype = 'c' ORDER BY conname;";

    private const string CompositesSql = """
        SELECT n.nspname, t.typname FROM pg_type t JOIN ns n ON n.oid = t.typnamespace JOIN pg_class c ON c.oid = t.typrelid
        WHERE t.typtype = 'c' AND c.relkind = 'c' AND t.oid NOT IN (SELECT objid FROM ext);
        """;

    private const string SequencesSql = """
        SELECT n.nspname, c.relname, format_type(s.seqtypid, NULL) AS type, s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcycle
        FROM pg_sequence s JOIN pg_class c ON c.oid = s.seqrelid JOIN ns n ON n.oid = c.relnamespace
        WHERE c.oid NOT IN (SELECT objid FROM ext)
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.classid = 'pg_class'::regclass AND d.deptype = 'i')
        ORDER BY n.nspname, c.relname;
        """;

    private const string RoutinesSql = """
        SELECT p.oid, n.nspname, p.proname, p.prokind::text AS prokind, l.lanname,
               CASE WHEN p.prokind IN ('f', 'p') THEN pg_get_functiondef(p.oid) END AS def
        FROM pg_proc p JOIN ns n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
        WHERE p.oid NOT IN (SELECT objid FROM ext)
        ORDER BY n.nspname, p.proname, p.oid;
        """;

    private const string ViewsSql = """
        SELECT c.oid, n.nspname, c.relname, c.relkind::text AS relkind, pg_get_viewdef(c.oid, true) AS def
        FROM pg_class c JOIN ns n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('v', 'm') AND c.oid NOT IN (SELECT objid FROM ext)
        ORDER BY c.oid;
        """;

    private const string ViewDependenciesSql = """
        SELECT DISTINCT r.ev_class AS view_oid, rn.nspname AS ref_schema, COALESCE(rc.relname, rp.proname) AS ref_name
        FROM pg_rewrite r
        JOIN pg_depend d ON d.objid = r.oid AND d.classid = 'pg_rewrite'::regclass
        LEFT JOIN pg_class rc ON d.refclassid = 'pg_class'::regclass AND rc.oid = d.refobjid
        LEFT JOIN pg_proc rp ON d.refclassid = 'pg_proc'::regclass AND rp.oid = d.refobjid
        JOIN pg_namespace rn ON rn.oid = COALESCE(rc.relnamespace, rp.pronamespace)
        WHERE COALESCE(rc.oid, rp.oid) IS NOT NULL AND d.refobjid <> r.ev_class;
        """;

    private const string TriggersSql = """
        SELECT tg.tgname, n.nspname, c.relname, pg_get_triggerdef(tg.oid, true) AS def, pn.nspname AS fn_schema, p.proname AS fn_name
        FROM pg_trigger tg
        JOIN pg_class c ON c.oid = tg.tgrelid
        JOIN ns n ON n.oid = c.relnamespace
        JOIN pg_proc p ON p.oid = tg.tgfoid
        JOIN pg_namespace pn ON pn.oid = p.pronamespace
        WHERE NOT tg.tgisinternal
        ORDER BY n.nspname, c.relname, tg.tgname;
        """;
}
