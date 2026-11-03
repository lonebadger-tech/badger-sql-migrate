using System.Globalization;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql.Scripting;

/// <summary>When an object is created; drops run in the reverse order. Gaps leave room for related steps.</summary>
internal static class Phase
{
    public const int Schema = 10, Extension = 12, Collation = 14, Enum = 20, Range = 21, Domain = 22, Composite = 23, Wrapper = 30, Server = 31,
        UserMapping = 32, Sequence = 40, Function = 50, Aggregate = 52, Operator = 54, Cast = 56, TextSearchDictionary = 58, TextSearchConfig = 59,
        Table = 60, Partition = 61, ForeignTable = 62, SequenceOwner = 65, Key = 70, Check = 71, Index = 72, ForeignKey = 75, LateFunction = 78,
        View = 80, ViewIndex = 82, Statistics = 85, Rule = 86, Trigger = 87, Policy = 88, Publication = 90, EventTrigger = 92;
}

/// <summary>
/// One database object scripted as a whole: compared by <see cref="Definition"/>, created by <see cref="Create"/>,
/// dropped by <see cref="Drop"/>. <see cref="Parent"/> is the table it lives on (dropped and created with it).
/// </summary>
internal sealed record PgObject(string Key, string Kind, string Definition, string Create, string Drop, int Phase, string? Parent = null)
{
    /// <summary>Keys of objects this one needs (from pg_depend).</summary>
    public HashSet<string> Depends { get; } = new(StringComparer.Ordinal);

    /// <summary>For functions: a change keeps the same signature and result, so CREATE OR REPLACE is enough.</summary>
    public string? ReplaceSignature { get; init; }

    /// <summary>For views and routines: schema and name as written in catalogs (matched against repeatable scripts).</summary>
    public (string Schema, string Name)? Routine { get; init; }
}

internal sealed record PgColumn(string Name, int Position, string Type, bool NotNull, string? Default, char Identity, string? IdentityOptions,
    char Generated, string? Collation, string? Storage, string DefaultStorage, string? Options)
{
    /// <summary>Everything but position, default and storage details: equal shapes suggest a rename.</summary>
    public string Shape => string.Join('|', Type, NotNull, Identity, Generated, Generated == '\0' ? null : Default, Collation);

    public string Definition()
    {
        var sb = new System.Text.StringBuilder($"{Sql.Ident(Name)} {Type}");
        if (Collation is not null)
        {
            sb.Append(" COLLATE ").Append(Collation);
        }

        if (Generated is 's' or 'v')
        {
            sb.Append($" GENERATED ALWAYS AS ({Default}){(Generated == 's' ? " STORED" : string.Empty)}");
        }
        else if (Default is not null)
        {
            sb.Append(" DEFAULT ").Append(Default);
        }

        if (Identity is 'a' or 'd')
        {
            sb.Append($" GENERATED {(Identity == 'a' ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY{(IdentityOptions is null ? string.Empty : $" ({IdentityOptions})")}");
        }

        if (NotNull)
        {
            sb.Append(" NOT NULL");
        }

        return sb.ToString();
    }
}

internal sealed record PgTable(string Key, string Name, char Kind, char Persistence, IReadOnlyList<PgColumn> Columns, IReadOnlyList<string> Parents,
    string? PartitionOf, string? Bound, string? PartitionKey, string? Options, string? Tablespace, string ReplicaIdentity, bool Rls, bool ForceRls,
    string? ForeignServer, string? ForeignOptions)
{
    public bool IsPartition => PartitionOf is not null;
}

internal sealed record PgSequence(string Key, string Name, string Type, string Start, string Increment, string Min, string Max, string Cache, bool Cycle, string? OwnedBy);

internal sealed record PgEnum(string Key, string Name, IReadOnlyList<string> Labels);

internal sealed record PgDomain(string Key, string Name, string BaseType, string? Collation, string? Default, bool NotNull, IReadOnlyList<(string Name, string Definition)> Constraints);

internal sealed record PgComposite(string Key, string Name, IReadOnlyList<(string Name, string Type)> Attributes);

internal sealed record PgPublication(string Key, string Name, bool AllTables, string Publish, IReadOnlyList<string> Tables);

/// <summary>Everything the PostgreSQL scripter compares, read from one database with an empty search_path.</summary>
internal sealed class PostgreSqlModel
{
    public Dictionary<string, PgTable> Tables { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgObject> Objects { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgSequence> Sequences { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgEnum> Enums { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgDomain> Domains { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgComposite> Composites { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PgPublication> Publications { get; } = new(StringComparer.Ordinal);

    /// <summary>"COMMENT ON target" -> the object it belongs to (a model key) and the text.</summary>
    public Dictionary<string, (string Owner, string Text)> Comments { get; } = new(StringComparer.Ordinal);

    /// <summary>Grants ("privilege ON kind name TO grantee", one privilege to one grantee) -> the object they are on (a model key).</summary>
    public SortedDictionary<string, string> Grants { get; } = new(StringComparer.Ordinal);

    /// <summary>ALTER DEFAULT PRIVILEGES ... GRANT statements.</summary>
    public SortedSet<string> DefaultPrivileges { get; } = new(StringComparer.Ordinal);

    /// <summary>Dependencies by object key (from pg_depend), including tables and types.</summary>
    public Dictionary<string, HashSet<string>> Depends { get; } = new(StringComparer.Ordinal);

    public List<string> Unsupported { get; } = [];

    public static async Task<PostgreSqlModel> ReadAsync(IDatabaseSession session, string stateSchema, Func<string, bool> excluded, CancellationToken ct)
    {
        var own = !session.InTransaction;
        if (own)
        {
            await session.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await session.QueryAsync("SELECT set_config('search_path', '', true);", null, ct).ConfigureAwait(false);
            return await ReadCoreAsync(session, stateSchema, excluded, ct).ConfigureAwait(false);
        }
        finally
        {
            if (own)
            {
                await session.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<PostgreSqlModel> ReadCoreAsync(IDatabaseSession session, string stateSchema, Func<string, bool> excluded, CancellationToken ct)
    {
        var m = new PostgreSqlModel();
        var p = new Dictionary<string, object?> { ["state"] = stateSchema };
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) => await session.QueryAsync(Common + sql, p, ct).ConfigureAwait(false);
        bool Skip(string? schema, string name) => schema is not null && excluded($"{schema}.{name}");

        foreach (var r in await Q("SELECT nspname AS name, quote_ident(nspname) AS ident FROM ns;"))
        {
            var name = S(r["name"])!;
            if (name != "public")
            {
                var ident = S(r["ident"])!;
                Add(m, new PgObject($"schema {ident}", "schema", name, $"CREATE SCHEMA {ident};", $"DROP SCHEMA IF EXISTS {ident};", Phase.Schema));
            }
        }

        foreach (var r in await Q("SELECT e.extname, n.nspname, e.extversion FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname <> 'plpgsql';"))
        {
            var name = S(r["extname"])!;
            Add(m, new PgObject($"extension {Sql.Ident(name)}", "extension", $"{name}|{r["nspname"]}|{r["extversion"]}",
                $"CREATE EXTENSION IF NOT EXISTS {Sql.Ident(name)} WITH SCHEMA {Sql.Ident(S(r["nspname"])!)} VERSION {Sql.Literal(S(r["extversion"])!)};",
                $"DROP EXTENSION IF EXISTS {Sql.Ident(name)};", Phase.Extension));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_collation'::regclass, co.oid, 0)).identity AS id, n.nspname, co.collname, co.collprovider::text AS provider, co.collisdeterministic AS det,
                   co.collcollate, co.collctype, COALESCE(to_jsonb(co) ->> 'colllocale', to_jsonb(co) ->> 'colliculocale') AS locale
            FROM pg_collation co JOIN ns n ON n.oid = co.collnamespace WHERE co.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            if (Skip(S(r["nspname"]), S(r["collname"])!))
            {
                continue;
            }

            var id = S(r["id"])!;
            var provider = S(r["provider"]) == "i" ? "icu" : "libc";
            var locale = provider == "icu" ? $"locale = {Sql.Literal(S(r["locale"]) ?? S(r["collcollate"]) ?? string.Empty)}" : $"lc_collate = {Sql.Literal(S(r["collcollate"]) ?? string.Empty)}, lc_ctype = {Sql.Literal(S(r["collctype"]) ?? string.Empty)}";
            var create = $"CREATE COLLATION {id} (provider = {provider}, {locale}{(B(r["det"]) ? string.Empty : ", deterministic = false")});";
            Add(m, new PgObject($"collation {id}", "collation", create, create, $"DROP COLLATION IF EXISTS {id};", Phase.Collation));
        }

        // Types: enums, domains, ranges, composites.
        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_type'::regclass, t.oid, 0)).identity AS id, n.nspname, t.typname, t.typtype::text AS kind,
                   (SELECT string_agg(quote_literal(enumlabel), ',' ORDER BY enumsortorder) FROM pg_enum WHERE enumtypid = t.oid) AS labels,
                   format_type(t.typbasetype, t.typtypmod) AS base, t.typnotnull, t.typdefault,
                   CASE WHEN t.typcollation <> 0 AND t.typcollation <> (SELECT typcollation FROM pg_type WHERE oid = t.typbasetype) THEN (SELECT quote_ident(cn.nspname) || '.' || quote_ident(c.collname) FROM pg_collation c JOIN pg_namespace cn ON cn.oid = c.collnamespace WHERE c.oid = t.typcollation) END AS collation,
                   (SELECT string_agg(quote_ident(conname) || ' ' || pg_get_constraintdef(oid), E'\n' ORDER BY conname) FROM pg_constraint WHERE contypid = t.oid AND contype <> 'n') AS constraints,
                   (SELECT format_type(rngsubtype, NULL) FROM pg_range WHERE rngtypid = t.oid) AS subtype,
                   (SELECT string_agg(quote_ident(attname) || E'\t' || format_type(atttypid, atttypmod), E'\n' ORDER BY attnum) FROM pg_attribute WHERE attrelid = t.typrelid AND attnum > 0 AND NOT attisdropped) AS attributes
            FROM pg_type t JOIN ns n ON n.oid = t.typnamespace
            WHERE (t.typtype IN ('e', 'd', 'r') OR (t.typtype = 'c' AND (SELECT relkind FROM pg_class WHERE oid = t.typrelid) = 'c'))
              AND t.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            if (Skip(S(r["nspname"]), S(r["typname"])!))
            {
                continue;
            }

            var id = S(r["id"])!;
            switch (S(r["kind"]))
            {
                case "e":
                    m.Enums[$"type {id}"] = new PgEnum($"type {id}", id, (S(r["labels"]) ?? string.Empty).Length == 0 ? [] : SplitLabels(S(r["labels"])!));
                    break;
                case "d":
                    var constraints = (S(r["constraints"]) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(c => { var sp = c.IndexOf(' ', StringComparison.Ordinal); return (c[..sp], c[(sp + 1)..]); }).ToList();
                    m.Domains[$"type {id}"] = new PgDomain($"type {id}", id, S(r["base"])!, S(r["collation"]), S(r["typdefault"]), B(r["typnotnull"]), constraints);
                    break;
                case "r":
                    var create = $"CREATE TYPE {id} AS RANGE (SUBTYPE = {S(r["subtype"])});";
                    Add(m, new PgObject($"type {id}", "range", create, create, $"DROP TYPE IF EXISTS {id};", Phase.Range));
                    break;
                default:
                    var attributes = (S(r["attributes"]) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(a => { var sp = a.IndexOf('\t', StringComparison.Ordinal); return (a[..sp], a[(sp + 1)..]); }).ToList();
                    m.Composites[$"composite type {id}"] = new PgComposite($"composite type {id}", id, attributes);
                    break;
            }
        }

        foreach (var r in await Q("""
            SELECT w.fdwname, w.fdwhandler::regproc::text AS handler, w.fdwvalidator::regproc::text AS validator, w.fdwoptions
            FROM pg_foreign_data_wrapper w WHERE w.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            var name = Sql.Ident(S(r["fdwname"])!);
            var create = $"CREATE FOREIGN DATA WRAPPER {name}{(S(r["handler"]) is { } h && h != "-" ? $" HANDLER {h}" : string.Empty)}{(S(r["validator"]) is { } v && v != "-" ? $" VALIDATOR {v}" : string.Empty)}{Options(r["fdwoptions"])};";
            Add(m, new PgObject($"foreign-data wrapper {name}", "foreign data wrapper", create, create, $"DROP FOREIGN DATA WRAPPER IF EXISTS {name};", Phase.Wrapper));
        }

        foreach (var r in await Q("""
            SELECT s.srvname, w.fdwname, s.srvtype, s.srvversion, s.srvoptions FROM pg_foreign_server s JOIN pg_foreign_data_wrapper w ON w.oid = s.srvfdw
            WHERE s.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            var name = Sql.Ident(S(r["srvname"])!);
            var create = $"CREATE SERVER {name}{(S(r["srvtype"]) is { } t ? $" TYPE {Sql.Literal(t)}" : string.Empty)}{(S(r["srvversion"]) is { } v ? $" VERSION {Sql.Literal(v)}" : string.Empty)} FOREIGN DATA WRAPPER {Sql.Ident(S(r["fdwname"])!)}{Options(r["srvoptions"])};";
            Add(m, new PgObject($"server {name}", "server", create, create, $"DROP SERVER IF EXISTS {name};", Phase.Server));
        }

        foreach (var r in await Q("SELECT um.srvname, um.usename FROM pg_user_mappings um;"))
        {
            var role = S(r["usename"]) == "public" ? "PUBLIC" : Sql.Ident(S(r["usename"])!);
            var create = $"CREATE USER MAPPING FOR {role} SERVER {Sql.Ident(S(r["srvname"])!)};";
            Add(m, new PgObject($"user mapping for {S(r["usename"])} on server {S(r["srvname"])}", "user mapping", create, create, $"DROP USER MAPPING IF EXISTS FOR {role} SERVER {Sql.Ident(S(r["srvname"])!)};", Phase.UserMapping));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity AS id, n.nspname, c.relname, format_type(s.seqtypid, NULL) AS type,
                   s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, s.seqcycle,
                   (SELECT (pg_identify_object('pg_class'::regclass, d.refobjid, 0)).identity || '.' || quote_ident(a.attname)
                    FROM pg_depend d JOIN pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid
                    WHERE d.objid = c.oid AND d.deptype = 'a' AND d.classid = 'pg_class'::regclass AND d.refclassid = 'pg_class'::regclass LIMIT 1) AS owned_by
            FROM pg_sequence s JOIN rel c ON c.oid = s.seqrelid JOIN ns n ON n.oid = c.relnamespace
            WHERE NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype = 'i');
            """))
        {
            if (Skip(S(r["nspname"]), S(r["relname"])!))
            {
                continue;
            }

            var id = S(r["id"])!;
            m.Sequences[$"sequence {id}"] = new PgSequence($"sequence {id}", id, S(r["type"])!, S(r["seqstart"])!, S(r["seqincrement"])!, S(r["seqmin"])!, S(r["seqmax"])!, S(r["seqcache"])!, B(r["seqcycle"]), S(r["owned_by"]));
        }

        // Functions and procedures: PostgreSQL writes their full CREATE OR REPLACE statement.
        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_proc'::regclass, p.oid, 0)).type AS kind, (pg_identify_object('pg_proc'::regclass, p.oid, 0)).identity AS id,
                   n.nspname, p.proname, p.prokind::text AS prokind, pg_get_functiondef(p.oid) AS def,
                   pg_get_function_result(p.oid) AS result, pg_get_function_arguments(p.oid) AS args
            FROM pg_proc p JOIN ns n ON n.oid = p.pronamespace
            WHERE p.prokind IN ('f', 'p', 'w') AND p.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            if (Skip(S(r["nspname"]), S(r["proname"])!))
            {
                continue;
            }

            var kind = S(r["kind"])!;
            var id = S(r["id"])!;
            var def = S(r["def"])!.TrimEnd();
            Add(m, new PgObject($"{kind} {id}", kind, def, def + ";", $"DROP {kind.ToUpperInvariant()} IF EXISTS {id};", Phase.Function) { ReplaceSignature = $"{S(r["prokind"])}|{S(r["result"])}|{S(r["args"])}", Routine = (S(r["nspname"])!, S(r["proname"])!) });
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_proc'::regclass, p.oid, 0)).identity AS id, n.nspname, p.proname, a.aggkind::text AS aggkind,
                   a.aggtransfn::regproc::text AS sfunc, format_type(a.aggtranstype, NULL) AS stype, a.aggfinalfn::regproc::text AS ffunc,
                   a.aggcombinefn::regproc::text AS cfunc, a.agginitval, a.aggsortop::regoperator::text AS sortop,
                   pg_get_function_identity_arguments(p.oid) AS args, n.nspname || '.' || quote_ident(p.proname) AS qname
            FROM pg_proc p JOIN pg_aggregate a ON a.aggfnoid = p.oid JOIN ns n ON n.oid = p.pronamespace
            WHERE p.prokind = 'a' AND p.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            if (Skip(S(r["nspname"]), S(r["proname"])!))
            {
                continue;
            }

            if (S(r["aggkind"]) != "n")
            {
                m.Unsupported.Add($"ordered-set or hypothetical aggregate {S(r["id"])}");
                continue;
            }

            var id = S(r["id"])!;
            var parts = new List<string> { $"SFUNC = {S(r["sfunc"])}", $"STYPE = {S(r["stype"])}" };
            if (S(r["ffunc"]) is { } f && f != "-")
            {
                parts.Add($"FINALFUNC = {f}");
            }

            if (S(r["cfunc"]) is { } cf && cf != "-")
            {
                parts.Add($"COMBINEFUNC = {cf}");
            }

            if (S(r["agginitval"]) is { } init)
            {
                parts.Add($"INITCOND = {Sql.Literal(init)}");
            }

            if (S(r["sortop"]) is { } so && so != "0")
            {
                parts.Add($"SORTOP = OPERATOR({so[..so.IndexOf('(', StringComparison.Ordinal)]})");
            }

            var create = $"CREATE AGGREGATE {S(r["qname"])}({S(r["args"])}) ({string.Join(", ", parts)});";
            Add(m, new PgObject($"aggregate {id}", "aggregate", create, create, $"DROP AGGREGATE IF EXISTS {id};", Phase.Aggregate));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_operator'::regclass, o.oid, 0)).identity AS id, n.nspname, o.oprname,
                   CASE WHEN o.oprleft = 0 THEN NULL ELSE format_type(o.oprleft, NULL) END AS lt, CASE WHEN o.oprright = 0 THEN NULL ELSE format_type(o.oprright, NULL) END AS rt,
                   o.oprcode::regprocedure::text AS func, o.oprcom::regoperator::text AS com, o.oprnegate::regoperator::text AS neg,
                   o.oprrest::regproc::text AS rest, o.oprjoin::regproc::text AS jn, o.oprcanhash, o.oprcanmerge
            FROM pg_operator o JOIN ns n ON n.oid = o.oprnamespace WHERE o.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            if (Skip(S(r["nspname"]), S(r["oprname"])!))
            {
                continue;
            }

            var id = S(r["id"])!;
            var name = $"{Sql.Ident(S(r["nspname"])!)}.{S(r["oprname"])}";
            static string Op(string regoperator) => regoperator[..regoperator.IndexOf('(', StringComparison.Ordinal)];
            var parts = new List<string> { $"FUNCTION = {S(r["func"])![..S(r["func"])!.IndexOf('(', StringComparison.Ordinal)]}" };
            if (S(r["lt"]) is { } lt)
            {
                parts.Add($"LEFTARG = {lt}");
            }

            if (S(r["rt"]) is { } rt)
            {
                parts.Add($"RIGHTARG = {rt}");
            }

            if (S(r["com"]) is { } com && com != "0")
            {
                parts.Add($"COMMUTATOR = OPERATOR({Op(com)})");
            }

            if (S(r["neg"]) is { } neg && neg != "0")
            {
                parts.Add($"NEGATOR = OPERATOR({Op(neg)})");
            }

            if (S(r["rest"]) is { } rest && rest != "-")
            {
                parts.Add($"RESTRICT = {rest}");
            }

            if (S(r["jn"]) is { } jn && jn != "-")
            {
                parts.Add($"JOIN = {jn}");
            }

            if (B(r["oprcanhash"]))
            {
                parts.Add("HASHES");
            }

            if (B(r["oprcanmerge"]))
            {
                parts.Add("MERGES");
            }

            var create = $"CREATE OPERATOR {name} ({string.Join(", ", parts)});";
            Add(m, new PgObject($"operator {id}", "operator", create, create, $"DROP OPERATOR IF EXISTS {id};", Phase.Operator));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_cast'::regclass, c.oid, 0)).identity AS id, format_type(c.castsource, NULL) AS src, format_type(c.casttarget, NULL) AS tgt,
                   c.castfunc::regprocedure::text AS func, c.castcontext::text AS ctx, c.castmethod::text AS method
            FROM pg_cast c WHERE c.oid >= 16384 AND c.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            var how = S(r["method"]) switch { "f" => $"WITH FUNCTION {S(r["func"])}", "i" => "WITH INOUT", _ => "WITHOUT FUNCTION" };
            var context = S(r["ctx"]) switch { "a" => " AS ASSIGNMENT", "i" => " AS IMPLICIT", _ => string.Empty };
            var create = $"CREATE CAST ({S(r["src"])} AS {S(r["tgt"])}) {how}{context};";
            Add(m, new PgObject($"cast {S(r["id"])}", "cast", create, create, $"DROP CAST IF EXISTS ({S(r["src"])} AS {S(r["tgt"])});", Phase.Cast));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_ts_dict'::regclass, d.oid, 0)).identity AS id, n.nspname, d.dictname,
                   (SELECT quote_ident(tn.nspname) || '.' || quote_ident(t.tmplname) FROM pg_ts_template t JOIN pg_namespace tn ON tn.oid = t.tmplnamespace WHERE t.oid = d.dicttemplate) AS template, d.dictinitoption
            FROM pg_ts_dict d JOIN ns n ON n.oid = d.dictnamespace WHERE d.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            if (Skip(S(r["nspname"]), S(r["dictname"])!))
            {
                continue;
            }

            var create = $"CREATE TEXT SEARCH DICTIONARY {S(r["id"])} (TEMPLATE = {S(r["template"])}{(S(r["dictinitoption"]) is { } o ? ", " + o : string.Empty)});";
            Add(m, new PgObject($"text search dictionary {S(r["id"])}", "text search dictionary", create, create, $"DROP TEXT SEARCH DICTIONARY IF EXISTS {S(r["id"])};", Phase.TextSearchDictionary));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_ts_config'::regclass, c.oid, 0)).identity AS id, n.nspname, c.cfgname,
                   (SELECT quote_ident(pn.nspname) || '.' || quote_ident(p.prsname) FROM pg_ts_parser p JOIN pg_namespace pn ON pn.oid = p.prsnamespace WHERE p.oid = c.cfgparser) AS parser,
                   (SELECT string_agg(format('ALTER TEXT SEARCH CONFIGURATION %s ADD MAPPING FOR %s WITH %s;', (pg_identify_object('pg_ts_config'::regclass, c.oid, 0)).identity, tt.alias, dicts), E'\n' ORDER BY tt.alias)
                    FROM (SELECT m.maptokentype, string_agg(m.mapdict::regdictionary::text, ', ' ORDER BY m.mapseqno) AS dicts FROM pg_ts_config_map m WHERE m.mapcfg = c.oid GROUP BY m.maptokentype) g
                    JOIN ts_token_type(c.cfgparser) tt ON tt.tokid = g.maptokentype) AS mappings
            FROM pg_ts_config c JOIN ns n ON n.oid = c.cfgnamespace WHERE c.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            if (Skip(S(r["nspname"]), S(r["cfgname"])!))
            {
                continue;
            }

            var create = $"CREATE TEXT SEARCH CONFIGURATION {S(r["id"])} (PARSER = {S(r["parser"])});\nALTER TEXT SEARCH CONFIGURATION {S(r["id"])} DROP MAPPING IF EXISTS FOR {string.Join(", ", await TokenTypes(session, S(r["parser"])!, ct).ConfigureAwait(false))};" + (S(r["mappings"]) is { } maps ? "\n" + maps : string.Empty);
            Add(m, new PgObject($"text search configuration {S(r["id"])}", "text search configuration", create, create, $"DROP TEXT SEARCH CONFIGURATION IF EXISTS {S(r["id"])};", Phase.TextSearchConfig));
        }

        // Tables, partitions, foreign tables.
        var columns = (await Q("""
            SELECT a.attrelid, a.attnum, a.attname, format_type(a.atttypid, a.atttypmod) AS type, a.attnotnull, a.attidentity::text AS identity, a.attgenerated::text AS generated,
                   pg_get_expr(d.adbin, d.adrelid) AS def,
                   CASE WHEN a.attcollation <> t.typcollation THEN (SELECT quote_ident(cn.nspname) || '.' || quote_ident(co.collname) FROM pg_collation co JOIN pg_namespace cn ON cn.oid = co.collnamespace WHERE co.oid = a.attcollation) END AS collation,
                   CASE a.attstorage WHEN 'p' THEN 'PLAIN' WHEN 'e' THEN 'EXTERNAL' WHEN 'm' THEN 'MAIN' ELSE 'EXTENDED' END AS storage,
                   CASE t.typstorage WHEN 'p' THEN 'PLAIN' WHEN 'e' THEN 'EXTERNAL' WHEN 'm' THEN 'MAIN' ELSE 'EXTENDED' END AS default_storage,
                   array_to_string(a.attoptions, ', ') AS options,
                   (SELECT format('START WITH %s INCREMENT BY %s MINVALUE %s MAXVALUE %s CACHE %s%s', s.seqstart, s.seqincrement, s.seqmin, s.seqmax, s.seqcache, CASE WHEN s.seqcycle THEN ' CYCLE' ELSE '' END)
                    FROM pg_depend dp JOIN pg_sequence s ON s.seqrelid = dp.objid WHERE dp.refobjid = a.attrelid AND dp.refobjsubid = a.attnum AND dp.deptype = 'i' AND dp.classid = 'pg_class'::regclass) AS identity_options
            FROM pg_attribute a
            JOIN rel c ON c.oid = a.attrelid
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attnum > 0 AND NOT a.attisdropped AND a.attislocal AND c.relkind IN ('r', 'p', 'f');
            """)).GroupBy(r => Convert.ToUInt32(r["attrelid"], CultureInfo.InvariantCulture)).ToDictionary(g => g.Key, g => g.OrderBy(r => Convert.ToInt32(r["attnum"], CultureInfo.InvariantCulture)).ToList());

        foreach (var r in await Q("""
            SELECT c.oid, (pg_identify_object('pg_class'::regclass, c.oid, 0)).type AS kind, (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity AS id,
                   n.nspname, c.relname, c.relkind::text AS relkind, c.relpersistence::text AS persistence, c.relispartition,
                   pg_get_partkeydef(c.oid) AS partkey, pg_get_expr(c.relpartbound, c.oid) AS bound,
                   (SELECT string_agg((pg_identify_object('pg_class'::regclass, i.inhparent, 0)).identity, ', ' ORDER BY i.inhseqno) FROM pg_inherits i WHERE i.inhrelid = c.oid) AS parents,
                   array_to_string(c.reloptions, ', ') AS options, (SELECT quote_ident(spcname) FROM pg_tablespace WHERE oid = c.reltablespace) AS tablespace,
                   c.relreplident::text AS replident, (SELECT quote_ident(ic.relname) FROM pg_index ix JOIN pg_class ic ON ic.oid = ix.indexrelid WHERE ix.indrelid = c.oid AND ix.indisreplident) AS replindex,
                   c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT quote_ident(s.srvname) FROM pg_foreign_table ft JOIN pg_foreign_server s ON s.oid = ft.ftserver WHERE ft.ftrelid = c.oid) AS server,
                   (SELECT ft.ftoptions FROM pg_foreign_table ft WHERE ft.ftrelid = c.oid) AS ftoptions
            FROM rel c JOIN ns n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'f');
            """))
        {
            if (Skip(S(r["nspname"]), S(r["relname"])!))
            {
                continue;
            }

            var oid = Convert.ToUInt32(r["oid"], CultureInfo.InvariantCulture);
            var kind = S(r["kind"])!;
            var id = S(r["id"])!;
            var cols = (columns.GetValueOrDefault(oid) ?? []).Select((c, i) => new PgColumn(
                S(c["attname"])!, i + 1, S(c["type"])!, B(c["attnotnull"]), S(c["def"]), Char(c["identity"]), S(c["identity_options"]), Char(c["generated"]),
                S(c["collation"]), S(c["storage"]) == S(c["default_storage"]) ? null : S(c["storage"]), S(c["default_storage"])!, S(c["options"]))).ToList();
            var parents = (S(r["parents"]) ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
            var isPartition = B(r["relispartition"]);
            var replica = S(r["replident"]) switch { "f" => "FULL", "n" => "NOTHING", "i" => $"USING INDEX {S(r["replindex"])}", _ => "DEFAULT" };
            m.Tables[$"{kind} {id}"] = new PgTable($"{kind} {id}", id, S(r["relkind"])![0], S(r["persistence"])![0], cols,
                isPartition ? [] : parents, isPartition ? parents.FirstOrDefault() : null, S(r["bound"]), S(r["partkey"]), S(r["options"]), S(r["tablespace"]), replica,
                B(r["relrowsecurity"]), B(r["relforcerowsecurity"]), S(r["server"]), Options(r["ftoptions"]));
        }

        var tableKeys = m.Tables.Keys.ToHashSet(StringComparer.Ordinal);
        string? TableKey(string identity) => new[] { $"table {identity}", $"foreign table {identity}" }.FirstOrDefault(tableKeys.Contains);

        // Constraints (local ones; partitions and children inherit the rest).
        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_constraint'::regclass, con.oid, 0)).identity AS id, (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity AS tbl,
                   quote_ident(con.conname) AS name, con.contype::text AS contype, pg_get_constraintdef(con.oid) AS def, con.convalidated
            FROM pg_constraint con JOIN rel c ON c.oid = con.conrelid
            WHERE con.contype IN ('p', 'u', 'f', 'c', 'x') AND con.conislocal AND con.conparentid = 0;
            """))
        {
            if (TableKey(S(r["tbl"])!) is not { } parent)
            {
                continue;
            }

            var phase = S(r["contype"]) switch { "f" => Phase.ForeignKey, "c" => Phase.Check, _ => Phase.Key };
            var def = S(r["def"])!;
            var create = $"ALTER TABLE {S(r["tbl"])} ADD CONSTRAINT {S(r["name"])} {def};";
            Add(m, new PgObject($"table constraint {S(r["id"])}", S(r["contype"]) == "f" ? "foreign key" : "constraint", def, create, $"ALTER TABLE {S(r["tbl"])} DROP CONSTRAINT IF EXISTS {S(r["name"])};", phase, parent));
        }

        // Indexes not backing a constraint, and not attached to a parent index (those come with the parent).
        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_class'::regclass, ic.oid, 0)).identity AS id, (pg_identify_object('pg_class'::regclass, t.oid, 0)).identity AS tbl, t.relkind::text AS relkind,
                   pg_get_indexdef(i.indexrelid) AS def, (SELECT quote_ident(spcname) FROM pg_tablespace WHERE oid = ic.reltablespace) AS tablespace
            FROM pg_index i JOIN pg_class ic ON ic.oid = i.indexrelid JOIN rel t ON t.oid = i.indrelid
            WHERE NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.indexrelid AND con.conrelid = i.indrelid AND con.contype IN ('p', 'u', 'x'))
              AND NOT EXISTS (SELECT 1 FROM pg_inherits inh WHERE inh.inhrelid = i.indexrelid);
            """))
        {
            var tbl = S(r["tbl"])!;
            var onView = S(r["relkind"]) == "m";
            var parent = onView ? $"materialized view {tbl}" : TableKey(tbl);
            if (parent is null)
            {
                continue;
            }

            // Indexes on partitioned tables read "ON ONLY"; created without it they cascade to the partitions.
            var def = S(r["def"])!.Replace(" ON ONLY ", " ON ", StringComparison.Ordinal);
            if (S(r["tablespace"]) is { } ts)
            {
                var where = def.LastIndexOf(" WHERE ", StringComparison.Ordinal);
                def = where < 0 ? $"{def} TABLESPACE {ts}" : $"{def[..where]} TABLESPACE {ts}{def[where..]}";
            }

            Add(m, new PgObject($"index {S(r["id"])}", "index", def, def + ";", $"DROP INDEX IF EXISTS {S(r["id"])};", onView ? Phase.ViewIndex : Phase.Index, parent));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_class'::regclass, c.oid, 0)).type AS kind, (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity AS id, n.nspname, c.relname,
                   pg_get_viewdef(c.oid) AS def, array_to_string(c.reloptions, ', ') AS options
            FROM rel c JOIN ns n ON n.oid = c.relnamespace WHERE c.relkind IN ('v', 'm');
            """))
        {
            if (Skip(S(r["nspname"]), S(r["relname"])!))
            {
                continue;
            }

            var kind = S(r["kind"])!;
            var id = S(r["id"])!;
            var body = S(r["def"])!.TrimEnd().TrimEnd(';');
            var with = S(r["options"]) is { } o ? $" WITH ({o})" : string.Empty;
            var create = kind == "view" ? $"CREATE VIEW {id}{with} AS\n{body};" : $"CREATE MATERIALIZED VIEW {id}{with} AS\n{body}\nWITH DATA;";
            Add(m, new PgObject($"{kind} {id}", kind, create, create, $"DROP {kind.ToUpperInvariant()} IF EXISTS {id};", Phase.View) { Routine = (S(r["nspname"])!, S(r["relname"])!) });
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_trigger'::regclass, tg.oid, 0)).identity AS id, (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity AS tbl,
                   quote_ident(tg.tgname) AS name, pg_get_triggerdef(tg.oid) AS def, tg.tgenabled::text AS enabled
            FROM pg_trigger tg JOIN rel c ON c.oid = tg.tgrelid
            WHERE NOT tg.tgisinternal AND COALESCE((to_jsonb(tg) ->> 'tgparentid')::oid, 0) = 0;
            """))
        {
            var tbl = S(r["tbl"])!;
            var parent = TableKey(tbl) ?? (m.Objects.ContainsKey($"view {tbl}") ? $"view {tbl}" : null);
            if (parent is null)
            {
                continue;
            }

            var enabled = S(r["enabled"]) switch
            {
                "D" => $"\nALTER TABLE {tbl} DISABLE TRIGGER {S(r["name"])};",
                "R" => $"\nALTER TABLE {tbl} ENABLE REPLICA TRIGGER {S(r["name"])};",
                "A" => $"\nALTER TABLE {tbl} ENABLE ALWAYS TRIGGER {S(r["name"])};",
                _ => string.Empty,
            };
            var create = S(r["def"])! + ";" + enabled;
            Add(m, new PgObject($"trigger {S(r["id"])}", "trigger", create, create, $"DROP TRIGGER IF EXISTS {S(r["name"])} ON {tbl};", Phase.Trigger, parent));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_rewrite'::regclass, rw.oid, 0)).identity AS id, (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity AS tbl,
                   quote_ident(rw.rulename) AS name, pg_get_ruledef(rw.oid) AS def
            FROM pg_rewrite rw JOIN rel c ON c.oid = rw.ev_class WHERE rw.rulename <> '_RETURN';
            """))
        {
            var tbl = S(r["tbl"])!;
            var parent = TableKey(tbl) ?? $"view {tbl}";
            var def = S(r["def"])!.TrimEnd();
            Add(m, new PgObject($"rule {S(r["id"])}", "rule", def, def.EndsWith(';') ? def : def + ";", $"DROP RULE IF EXISTS {S(r["name"])} ON {tbl};", Phase.Rule, parent));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_statistic_ext'::regclass, st.oid, 0)).identity AS id, (pg_identify_object('pg_class'::regclass, st.stxrelid, 0)).identity AS tbl,
                   pg_get_statisticsobjdef(st.oid) AS def
            FROM pg_statistic_ext st JOIN ns n ON n.oid = st.stxnamespace JOIN rel c ON c.oid = st.stxrelid;
            """))
        {
            if (TableKey(S(r["tbl"])!) is not { } parent)
            {
                continue;
            }

            Add(m, new PgObject($"statistics object {S(r["id"])}", "statistics", S(r["def"])!, S(r["def"]) + ";", $"DROP STATISTICS IF EXISTS {S(r["id"])};", Phase.Statistics, parent));
        }

        foreach (var r in await Q("""
            SELECT (pg_identify_object('pg_policy'::regclass, pol.oid, 0)).identity AS id, (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity AS tbl,
                   quote_ident(pol.polname) AS name, pol.polcmd::text AS cmd, pol.polpermissive,
                   (SELECT string_agg(CASE WHEN r = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(r)) END, ', ' ORDER BY 1) FROM unnest(pol.polroles) r) AS roles,
                   pg_get_expr(pol.polqual, pol.polrelid) AS qual, pg_get_expr(pol.polwithcheck, pol.polrelid) AS wcheck
            FROM pg_policy pol JOIN rel c ON c.oid = pol.polrelid;
            """))
        {
            if (TableKey(S(r["tbl"])!) is not { } parent)
            {
                continue;
            }

            var cmd = S(r["cmd"]) switch { "r" => "SELECT", "a" => "INSERT", "w" => "UPDATE", "d" => "DELETE", _ => "ALL" };
            var create = $"CREATE POLICY {S(r["name"])} ON {S(r["tbl"])} AS {(B(r["polpermissive"]) ? "PERMISSIVE" : "RESTRICTIVE")} FOR {cmd} TO {S(r["roles"]) ?? "PUBLIC"}"
                + (S(r["qual"]) is { } q ? $" USING ({q})" : string.Empty) + (S(r["wcheck"]) is { } w ? $" WITH CHECK ({w})" : string.Empty) + ";";
            Add(m, new PgObject($"policy {S(r["id"])}", "policy", create, create, $"DROP POLICY IF EXISTS {S(r["name"])} ON {S(r["tbl"])};", Phase.Policy, parent));
        }

        foreach (var r in await Q("""
            SELECT p.pubname, p.puballtables, concat_ws(', ', CASE WHEN p.pubinsert THEN 'insert' END, CASE WHEN p.pubupdate THEN 'update' END, CASE WHEN p.pubdelete THEN 'delete' END, CASE WHEN p.pubtruncate THEN 'truncate' END) AS publish,
                   (SELECT string_agg(quote_ident(pt.schemaname) || '.' || quote_ident(pt.tablename), ', ' ORDER BY 1) FROM pg_publication_tables pt WHERE pt.pubname = p.pubname) AS tables
            FROM pg_publication p;
            """))
        {
            var name = Sql.Ident(S(r["pubname"])!);
            m.Publications[$"publication {name}"] = new PgPublication($"publication {name}", name, B(r["puballtables"]), S(r["publish"])!, (S(r["tables"]) ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var r in await Q("""
            SELECT quote_ident(e.evtname) AS name, e.evtevent, e.evtfoid::regprocedure::text AS func, e.evtenabled::text AS enabled,
                   (SELECT string_agg(quote_literal(t), ', ') FROM unnest(e.evttags) t) AS tags
            FROM pg_event_trigger e WHERE e.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            var name = S(r["name"])!;
            var create = $"CREATE EVENT TRIGGER {name} ON {S(r["evtevent"])}{(S(r["tags"]) is { } t ? $" WHEN TAG IN ({t})" : string.Empty)} EXECUTE FUNCTION {S(r["func"])};"
                + S(r["enabled"]) switch { "D" => $"\nALTER EVENT TRIGGER {name} DISABLE;", "R" => $"\nALTER EVENT TRIGGER {name} ENABLE REPLICA;", "A" => $"\nALTER EVENT TRIGGER {name} ENABLE ALWAYS;", _ => string.Empty };
            Add(m, new PgObject($"event trigger {name}", "event trigger", create, create, $"DROP EVENT TRIGGER IF EXISTS {name};", Phase.EventTrigger));
        }

        foreach (var r in await Q("SELECT subname FROM pg_subscription WHERE subdbid = (SELECT oid FROM pg_database WHERE datname = current_database());"))
        {
            m.Unsupported.Add($"subscription {S(r["subname"])} (its connection holds a password)");
        }

        // Comments, as COMMENT ON targets.
        foreach (var r in await Q("""
            SELECT o.type, o.identity, d.description, d.objsubid, w.type || ' ' || w.identity AS owner
            FROM pg_description d CROSS JOIN LATERAL pg_identify_object(d.classoid, d.objoid, d.objsubid) o CROSS JOIN LATERAL pg_identify_object(d.classoid, d.objoid, 0) w
            WHERE (o.schema IN (SELECT nspname FROM ns) OR (d.classoid = 'pg_namespace'::regclass AND d.objoid IN (SELECT oid FROM ns))
                   OR d.classoid IN ('pg_event_trigger'::regclass, 'pg_publication'::regclass, 'pg_foreign_server'::regclass, 'pg_foreign_data_wrapper'::regclass, 'pg_extension'::regclass, 'pg_cast'::regclass))
              AND d.objoid NOT IN (SELECT objid FROM ext) AND d.objoid NOT IN (SELECT oid FROM skip);
            """))
        {
            if (CommentTarget(S(r["type"])!, S(r["identity"])!) is { } target)
            {
                m.Comments[target] = (S(r["owner"])!, S(r["description"])!);
            }
        }

        // Grants: one statement per privilege and grantee (owners' own rights are implicit).
        foreach (var r in await Q("""
            SELECT o.type, o.identity, a.grantee, a.privilege_type, a.is_grantable, NULL::text AS col, o.type || ' ' || o.identity AS owner
            FROM rel c CROSS JOIN LATERAL aclexplode(c.relacl) a CROSS JOIN LATERAL pg_identify_object('pg_class'::regclass, c.oid, 0) o
            WHERE c.relacl IS NOT NULL AND a.grantee <> c.relowner AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
            UNION ALL
            SELECT 'table', (pg_identify_object('pg_class'::regclass, c.oid, 0)).identity, a.grantee, a.privilege_type, a.is_grantable, quote_ident(att.attname),
                   (SELECT o.type || ' ' || o.identity FROM pg_identify_object('pg_class'::regclass, c.oid, 0) o)
            FROM pg_attribute att JOIN rel c ON c.oid = att.attrelid CROSS JOIN LATERAL aclexplode(att.attacl) a
            WHERE att.attacl IS NOT NULL AND att.attnum > 0 AND a.grantee <> c.relowner
            UNION ALL
            SELECT o.type, o.identity, a.grantee, a.privilege_type, a.is_grantable, NULL, o.type || ' ' || o.identity
            FROM pg_proc p JOIN ns n ON n.oid = p.pronamespace CROSS JOIN LATERAL aclexplode(p.proacl) a CROSS JOIN LATERAL pg_identify_object('pg_proc'::regclass, p.oid, 0) o
            WHERE p.proacl IS NOT NULL AND a.grantee <> p.proowner AND p.oid NOT IN (SELECT objid FROM ext)
            UNION ALL
            SELECT 'schema', quote_ident(n.nspname), a.grantee, a.privilege_type, a.is_grantable, NULL, 'schema ' || quote_ident(n.nspname)
            FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) a
            WHERE n.oid IN (SELECT oid FROM ns) AND n.nspacl IS NOT NULL AND a.grantee <> n.nspowner
            UNION ALL
            SELECT o.type, o.identity, a.grantee, a.privilege_type, a.is_grantable, NULL, CASE WHEN t.typrelid <> 0 THEN 'composite type ' ELSE 'type ' END || o.identity
            FROM pg_type t JOIN ns n ON n.oid = t.typnamespace CROSS JOIN LATERAL aclexplode(t.typacl) a CROSS JOIN LATERAL pg_identify_object('pg_type'::regclass, t.oid, 0) o
            WHERE t.typacl IS NOT NULL AND a.grantee <> t.typowner AND t.oid NOT IN (SELECT objid FROM ext);
            """))
        {
            var target = S(r["type"]) switch
            {
                "table" or "view" or "materialized view" or "foreign table" => "TABLE",
                "sequence" => "SEQUENCE",
                "function" or "aggregate" => "FUNCTION",
                "procedure" => "PROCEDURE",
                "schema" => "SCHEMA",
                "domain" => "DOMAIN",
                _ => "TYPE",
            };
            var grantee = Convert.ToUInt32(r["grantee"], CultureInfo.InvariantCulture) == 0 ? "PUBLIC" : await RoleAsync(session, r["grantee"], ct).ConfigureAwait(false);
            var column = S(r["col"]) is { } col ? $" ({col})" : string.Empty;
            m.Grants[$"{S(r["privilege_type"])}{column} ON {target} {S(r["identity"])} TO {grantee}{(B(r["is_grantable"]) ? " WITH GRANT OPTION" : string.Empty)}"] = S(r["owner"])!;
        }

        foreach (var r in await Q("""
            SELECT quote_ident(pg_get_userbyid(d.defaclrole)) AS role, quote_ident(n.nspname) AS schema, d.defaclobjtype::text AS objtype, a.grantee, a.privilege_type, a.is_grantable
            FROM pg_default_acl d LEFT JOIN pg_namespace n ON n.oid = d.defaclnamespace CROSS JOIN LATERAL aclexplode(d.defaclacl) a
            WHERE (d.defaclnamespace = 0 OR d.defaclnamespace IN (SELECT oid FROM ns)) AND a.grantee <> d.defaclrole;
            """))
        {
            var objects = S(r["objtype"]) switch { "r" => "TABLES", "S" => "SEQUENCES", "f" => "FUNCTIONS", "T" => "TYPES", _ => "SCHEMAS" };
            var grantee = Convert.ToUInt32(r["grantee"], CultureInfo.InvariantCulture) == 0 ? "PUBLIC" : await RoleAsync(session, r["grantee"], ct).ConfigureAwait(false);
            m.DefaultPrivileges.Add($"FOR ROLE {S(r["role"])}{(S(r["schema"]) is { } schema ? $" IN SCHEMA {schema}" : string.Empty)}|{S(r["privilege_type"])} ON {objects} TO {grantee}{(B(r["is_grantable"]) ? " WITH GRANT OPTION" : string.Empty)}");
        }

        // Dependencies between objects, by the same keys (views on tables and functions, functions on table row types ...).
        foreach (var r in await Q("""
            SELECT DISTINCT
                   CASE WHEN d.classid = 'pg_rewrite'::regclass THEN (SELECT o.type || ' ' || o.identity FROM pg_rewrite rw CROSS JOIN LATERAL pg_identify_object('pg_class'::regclass, rw.ev_class, 0) o WHERE rw.oid = d.objid)
                        WHEN d.classid = 'pg_attrdef'::regclass THEN (SELECT o.type || ' ' || o.identity FROM pg_attrdef ad CROSS JOIN LATERAL pg_identify_object('pg_class'::regclass, ad.adrelid, 0) o WHERE ad.oid = d.objid)
                        ELSE (SELECT o.type || ' ' || o.identity FROM pg_identify_object(d.classid, d.objid, 0) o) END AS dependent,
                   CASE WHEN d.refclassid = 'pg_type'::regclass AND (SELECT typrelid FROM pg_type WHERE oid = d.refobjid) <> 0
                             THEN (SELECT o.type || ' ' || o.identity FROM pg_identify_object('pg_class'::regclass, (SELECT typrelid FROM pg_type WHERE oid = d.refobjid), 0) o)
                        ELSE (SELECT o.type || ' ' || o.identity FROM pg_identify_object(d.refclassid, d.refobjid, 0) o) END AS dependency
            FROM pg_depend d
            WHERE d.deptype = 'n' AND d.objid >= 16384 AND d.refobjid >= 16384 AND d.objid NOT IN (SELECT objid FROM ext) AND d.refobjid NOT IN (SELECT objid FROM ext);
            """))
        {
            var dependent = S(r["dependent"]);
            var dependency = S(r["dependency"]);
            if (dependent is null || dependency is null || dependent == dependency)
            {
                continue;
            }

            if (!m.Depends.TryGetValue(dependent, out var set))
            {
                m.Depends[dependent] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(dependency);
        }

        foreach (var obj in m.Objects.Values)
        {
            if (m.Depends.TryGetValue(obj.Key, out var deps))
            {
                obj.Depends.UnionWith(deps);
            }
        }

        return m;
    }

    private static readonly Dictionary<string, string> RoleNames = new(StringComparer.Ordinal);

    private static async Task<string> RoleAsync(IDatabaseSession session, object? oid, CancellationToken ct)
    {
        var rows = await session.QueryAsync("SELECT quote_ident(pg_get_userbyid(@oid::oid)) AS r;", new Dictionary<string, object?> { ["oid"] = Convert.ToInt64(oid, CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
        return S(rows[0]["r"])!;
    }

    private static async Task<IReadOnlyList<string>> TokenTypes(IDatabaseSession session, string parser, CancellationToken ct) =>
        (await session.QueryAsync(
            "SELECT alias FROM ts_token_type((SELECT p.oid FROM pg_ts_parser p JOIN pg_namespace n ON n.oid = p.prsnamespace WHERE quote_ident(n.nspname) || '.' || quote_ident(p.prsname) = @parser)) ORDER BY tokid;",
            new Dictionary<string, object?> { ["parser"] = parser }, ct).ConfigureAwait(false)).Select(r => S(r["alias"])!).ToList();

    /// <summary>The COMMENT ON target for a pg_identify_object type and identity, or null for kinds not scripted.</summary>
    public static string? CommentTarget(string type, string identity)
    {
        static (string Name, string Table) On(string id)
        {
            var at = id.LastIndexOf(" on ", StringComparison.Ordinal);
            return (id[..at], id[(at + 4)..]);
        }

        return type switch
        {
            "table" or "view" or "materialized view" or "foreign table" or "sequence" or "index" or "function" or "procedure" or "aggregate" or "schema" or "type" or "domain"
                or "extension" or "collation" or "text search configuration" or "text search dictionary" or "server" or "publication" or "event trigger" or "operator" or "cast"
                => $"{type.ToUpperInvariant()} {identity}",
            "statistics object" => $"STATISTICS {identity}",
            "foreign-data wrapper" => $"FOREIGN DATA WRAPPER {identity}",
            "table column" or "view column" or "materialized view column" or "foreign table column" => $"COLUMN {identity}",
            "table constraint" => $"CONSTRAINT {On(identity).Name} ON {On(identity).Table}",
            "domain constraint" => $"CONSTRAINT {On(identity).Name} ON DOMAIN {On(identity).Table}",
            "trigger" or "policy" or "rule" => $"{type.ToUpperInvariant()} {On(identity).Name} ON {On(identity).Table}",
            _ => null,
        };
    }

    private static void Add(PostgreSqlModel m, PgObject obj) => m.Objects[obj.Key] = obj;

    private static List<string> SplitLabels(string quoted)
    {
        var labels = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuote = false;
        for (var i = 0; i < quoted.Length; i++)
        {
            var c = quoted[i];
            if (c == '\'' && inQuote && i + 1 < quoted.Length && quoted[i + 1] == '\'')
            {
                sb.Append("''");
                i++;
            }
            else if (c == '\'')
            {
                inQuote = !inQuote;
                sb.Append(c);
            }
            else if (c == ',' && !inQuote)
            {
                labels.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        labels.Add(sb.ToString());
        return labels;
    }

    /// <summary>{k=v,...} option arrays as OPTIONS (k 'v', ...).</summary>
    private static string Options(object? value)
    {
        if (value is not string[] items || items.Length == 0)
        {
            return string.Empty;
        }

        return " OPTIONS (" + string.Join(", ", items.Select(i =>
        {
            var eq = i.IndexOf('=', StringComparison.Ordinal);
            return $"{Sql.Ident(i[..eq])} {Sql.Literal(i[(eq + 1)..])}";
        })) + ")";
    }

    private static string? S(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static bool B(object? value) => value is true;

    private static char Char(object? value) => S(value) is { Length: > 0 } s ? s[0] : '\0';

    private const string Common = """
        WITH ns AS (
            SELECT oid, nspname FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog', 'information_schema') AND nspname NOT LIKE 'pg\_%' AND nspname <> @state),
        ext AS (SELECT objid FROM pg_depend WHERE deptype = 'e'),
        skip AS (SELECT oid FROM pg_class WHERE false),
        rel AS (SELECT c.* FROM pg_class c JOIN ns n ON n.oid = c.relnamespace WHERE c.oid NOT IN (SELECT objid FROM ext))

        """;
}

internal static class Sql
{
    public static string Ident(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_][a-z0-9_$]*$") && !Reserved.Contains(name) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both", "case", "cast", "check", "collate", "column", "constraint", "create",
        "current_catalog", "current_date", "current_role", "current_time", "current_timestamp", "current_user", "default", "deferrable", "desc", "distinct", "do",
        "else", "end", "except", "false", "fetch", "for", "foreign", "from", "grant", "group", "having", "in", "initially", "intersect", "into", "lateral", "leading",
        "limit", "localtime", "localtimestamp", "not", "null", "offset", "on", "only", "or", "order", "placing", "primary", "references", "returning", "select",
        "session_user", "some", "symmetric", "table", "then", "to", "trailing", "true", "union", "unique", "user", "using", "variadic", "when", "where", "window", "with",
    };
}
