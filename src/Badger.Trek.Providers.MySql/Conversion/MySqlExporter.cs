using System.Globalization;
using System.Text.RegularExpressions;
using Badger.Trek.Core;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.MySql.Scripting;

namespace Badger.Trek.Providers.MySql.Conversion;

/// <summary>
/// Reads the current MySQL database into a <see cref="PortableSchema"/> from information_schema, and views, routines and
/// triggers from SHOW CREATE (without DEFINER clauses and qualifiers for this database). The database is the one schema:
/// every name carries it. Column types are COLUMN_TYPE as MySQL prints it; defaults are MySQL expressions (literal
/// defaults quoted). Events and prefix lengths of indexes are reported instead of converted.
/// </summary>
public sealed partial class MySqlExporter : ISchemaExporter
{
    /// <summary>Types whose literal defaults information_schema shows unquoted and a CREATE statement needs quoted.</summary>
    private static readonly HashSet<string> QuotedDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "tinytext", "text", "mediumtext", "longtext", "enum", "set", "date", "time", "datetime", "timestamp", "json",
    };

    public async Task<PortableSchema> ExportAsync(IDatabaseSession session, ExportOptions options, CancellationToken cancellationToken)
    {
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) =>
            await session.QueryAsync(sql, null, cancellationToken).ConfigureAwait(false);

        var info = (await Q("SELECT DATABASE() AS db, (SELECT default_collation_name FROM information_schema.schemata WHERE schema_name = DATABASE()) AS coll;"))[0];
        var database = S(info["db"]) ?? throw new MigrationException("No database is selected; set 'database' for the environment.");
        var collation = S(info["coll"]);
        var schema = new PortableSchema
        {
            SourceProvider = MySqlProvider.ProviderId,
            DefaultSchema = database,
            CaseInsensitive = collation?.EndsWith("_ci", StringComparison.OrdinalIgnoreCase) == true,
            Collation = collation,
        };
        schema.Schemas.Add(database);

        var prefix = MySqlStateStore.Prefix(options.StateSchema);
        var state = StateTables.Names.Select(n => prefix + n).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PortableName N(string name) => new(database, name);
        bool Skip(string name) => state.Contains(name) || options.Excluded(N(name));

        // Tables.
        var tables = new Dictionary<string, TableParts>(StringComparer.Ordinal);
        foreach (var r in await Q("""
            SELECT table_name AS t, table_collation AS coll, table_comment AS cmt
              FROM information_schema.tables
             WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
             ORDER BY table_name;
            """))
        {
            var name = S(r["t"])!;
            if (!Skip(name))
            {
                tables[name] = new TableParts(N(name), S(r["coll"]), S(r["cmt"]) is { Length: > 0 } comment ? comment : null);
            }
        }

        // Columns.
        foreach (var r in await Q("""
            SELECT table_name AS t, column_name AS name, data_type AS dt, column_type AS type, is_nullable AS nullable, column_default AS def,
                   extra, collation_name AS coll, generation_expression AS gen, column_comment AS cmt
              FROM information_schema.columns
             WHERE table_schema = DATABASE()
             ORDER BY table_name, ordinal_position;
            """))
        {
            if (!tables.TryGetValue(S(r["t"])!, out var table))
            {
                continue;
            }

            var name = S(r["name"])!;
            var extra = S(r["extra"]) ?? string.Empty;
            var generated = S(r["gen"]) is { Length: > 0 } gen ? MySqlSchemaInspector.Unqualify(gen, database) : null;
            var columnCollation = S(r["coll"]);
            if (extra.Contains("on update", StringComparison.OrdinalIgnoreCase))
            {
                schema.Warnings.Add($"{table.Name}.{name}: ON UPDATE CURRENT_TIMESTAMP is not converted.");
            }

            table.Columns.Add(new PortableColumn(name, S(r["type"])!, S(r["nullable"]) == "YES")
            {
                Default = generated is null ? Default(S(r["def"]), S(r["dt"]) ?? string.Empty, extra, database) : null,
                Identity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase) ? new PortableIdentity(1, 1, false) : null,
                Computed = generated,
                ComputedStored = generated is not null && extra.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase),
                Collation = columnCollation is not null && !string.Equals(columnCollation, table.Collation, StringComparison.OrdinalIgnoreCase) ? columnCollation : null,
                Comment = S(r["cmt"]) is { Length: > 0 } comment ? comment : null,
            });
        }

        // Keys and indexes: one row per part.
        var indexRows = (await Q("""
            SELECT table_name AS t, index_name AS name, non_unique AS nu, seq_in_index AS seq, column_name AS col, sub_part AS sub,
                   collation AS dir, index_type AS type, expression AS expr
              FROM information_schema.statistics
             WHERE table_schema = DATABASE()
             ORDER BY table_name, index_name, seq_in_index;
            """)).Where(r => tables.ContainsKey(S(r["t"])!));
        foreach (var group in indexRows.GroupBy(r => (Table: S(r["t"])!, Name: S(r["name"])!)))
        {
            var table = tables[group.Key.Table];
            var parts = group.ToList();
            var unique = Convert.ToInt64(parts[0]["nu"], CultureInfo.InvariantCulture) == 0;
            var plain = parts.All(p => S(p["col"]) is not null && p["sub"] is null);
            foreach (var p in parts.Where(p => p["sub"] is not null))
            {
                schema.Warnings.Add($"{table.Name}: index {group.Key.Name} indexes the first {p["sub"]} characters of {S(p["col"])}; the prefix length is not converted.");
            }

            if (group.Key.Name == "PRIMARY")
            {
                table.PrimaryKey = new PortableKey(null, parts.Select(p => S(p["col"])!).ToList());
            }
            else if (unique && plain && S(parts[0]["type"]) == "BTREE")
            {
                table.Uniques.Add(new PortableKey(group.Key.Name, parts.Select(p => S(p["col"])!).ToList()));
            }
            else
            {
                var columns = parts.Select(p => S(p["col"]) is { } column
                    ? new PortableIndexColumn(column, false, S(p["dir"]) == "D")
                    : new PortableIndexColumn(MySqlSchemaInspector.Unqualify(S(p["expr"]) ?? string.Empty, database), true, S(p["dir"]) == "D")).ToList();
                var method = S(parts[0]["type"]) is { } type && type != "BTREE" ? type : null;
                table.Indexes.Add(new PortableIndex(group.Key.Name, unique, columns, [], null, method));
            }
        }

        // Foreign keys.
        var fkRows = (await Q("""
            SELECT rc.table_name AS t, rc.constraint_name AS name, rc.update_rule AS upd, rc.delete_rule AS del,
                   k.column_name AS col, k.referenced_table_schema AS rs, k.referenced_table_name AS rt, k.referenced_column_name AS rcol
              FROM information_schema.referential_constraints rc
              JOIN information_schema.key_column_usage k
                ON k.constraint_schema = rc.constraint_schema AND k.constraint_name = rc.constraint_name AND k.table_name = rc.table_name
             WHERE rc.constraint_schema = DATABASE() AND k.referenced_table_name IS NOT NULL
             ORDER BY rc.table_name, rc.constraint_name, k.ordinal_position;
            """)).Where(r => tables.ContainsKey(S(r["t"])!));
        foreach (var group in fkRows.GroupBy(r => (Table: S(r["t"])!, Name: S(r["name"])!)))
        {
            var parts = group.ToList();
            var first = parts[0];
            tables[group.Key.Table].ForeignKeys.Add(new PortableForeignKey(
                group.Key.Name,
                parts.Select(p => S(p["col"])!).ToList(),
                new PortableName(S(first["rs"]) ?? database, S(first["rt"])!),
                parts.Select(p => S(p["rcol"])!).ToList(),
                S(first["del"]) ?? "NO ACTION",
                S(first["upd"]) ?? "NO ACTION"));
        }

        // Check constraints (MySQL prints the clause with backquotes and extra parentheses).
        foreach (var r in await Q("""
            SELECT tc.table_name AS t, tc.constraint_name AS name, cc.check_clause AS clause
              FROM information_schema.table_constraints tc
              JOIN information_schema.check_constraints cc ON cc.constraint_schema = tc.constraint_schema AND cc.constraint_name = tc.constraint_name
             WHERE tc.table_schema = DATABASE() AND tc.constraint_type = 'CHECK'
             ORDER BY tc.table_name, tc.constraint_name;
            """))
        {
            if (tables.TryGetValue(S(r["t"])!, out var table))
            {
                table.Checks.Add(new PortableCheck(S(r["name"]), MySqlSchemaInspector.Unqualify(S(r["clause"]) ?? string.Empty, database)));
            }
        }

        foreach (var t in tables.Values)
        {
            schema.Tables.Add(new PortableTable(t.Name, t.Columns, t.PrimaryKey, t.Uniques, t.Checks, t.ForeignKeys, t.Indexes) { Comment = t.Comment });
        }

        await ReadCodeAsync(session, schema, database, Skip, cancellationToken).ConfigureAwait(false);

        foreach (var r in await Q("SELECT event_name AS name FROM information_schema.events WHERE event_schema = DATABASE() ORDER BY event_name;"))
        {
            if (!Skip(S(r["name"])!))
            {
                schema.Warnings.Add($"event {N(S(r["name"])!)} is not converted.");
            }
        }

        return schema;
    }

    /// <summary>Views, functions, procedures and triggers, in that order; views after the views and functions they use.</summary>
    private static async Task ReadCodeAsync(IDatabaseSession session, PortableSchema schema, string database, Func<string, bool> skip, CancellationToken cancellationToken)
    {
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) =>
            await session.QueryAsync(sql, null, cancellationToken).ConfigureAwait(false);

        async Task<string?> Create(string sql, string column) =>
            S((await Q(sql)).FirstOrDefault()?.GetValueOrDefault(column)) is { Length: > 0 } create ? MySqlModel.Portable(create, database) : null;

        PortableCode Code(PortableCodeKind kind, string name, string definition) => new(kind, new PortableName(database, name), definition) { Language = "sql" };

        var views = new List<PortableCode>();
        foreach (var r in await Q("SELECT table_name AS name FROM information_schema.views WHERE table_schema = DATABASE() ORDER BY table_name;"))
        {
            var name = S(r["name"])!;
            if (!skip(name))
            {
                views.Add(Code(PortableCodeKind.View, name, await Create($"SHOW CREATE VIEW {MySqlModel.Q(name)};", "Create View").ConfigureAwait(false) ?? string.Empty));
            }
        }

        var functions = new List<PortableCode>();
        var procedures = new List<PortableCode>();
        foreach (var r in await Q("SELECT routine_type AS kind, routine_name AS name FROM information_schema.routines WHERE routine_schema = DATABASE() ORDER BY routine_name;"))
        {
            var (name, function) = (S(r["name"])!, S(r["kind"]) == "FUNCTION");
            if (skip(name))
            {
                continue;
            }

            var keyword = function ? "FUNCTION" : "PROCEDURE";
            var create = await Create($"SHOW CREATE {keyword} {MySqlModel.Q(name)};", function ? "Create Function" : "Create Procedure").ConfigureAwait(false);
            if (create is null)
            {
                schema.Warnings.Add($"{keyword.ToLowerInvariant()} {database}.{name}: its definition is not visible to this login (it needs SHOW_ROUTINE or to be the definer); not converted.");
                continue;
            }

            (function ? functions : procedures).Add(Code(function ? PortableCodeKind.Function : PortableCodeKind.Procedure, name, create));
        }

        var triggers = new List<PortableCode>();
        foreach (var r in await Q("SELECT trigger_name AS name, event_object_table AS t FROM information_schema.triggers WHERE trigger_schema = DATABASE() ORDER BY trigger_name;"))
        {
            var (name, table) = (S(r["name"])!, S(r["t"])!);
            if (!skip(name) && !skip(table))
            {
                var create = await Create($"SHOW CREATE TRIGGER {MySqlModel.Q(name)};", "SQL Original Statement").ConfigureAwait(false) ?? string.Empty;
                triggers.Add(Code(PortableCodeKind.Trigger, name, create) with { Table = new PortableName(database, table) });
            }
        }

        // A view depends on the other views and the functions whose backquoted names its definition mentions.
        var candidates = views.Concat(functions).ToList();
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            var uses = candidates.Where(c => c.Name.Key != view.Name.Key && view.Definition.Contains(MySqlModel.Q(c.Name.Name), StringComparison.Ordinal)).Select(c => c.Name.Key).ToList();
            views[i] = view with { DependsOn = uses };
        }

        schema.Code.AddRange(functions);
        var byKey = views.ToDictionary(v => v.Name.Key);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(PortableCode view)
        {
            if (!visited.Add(view.Name.Key))
            {
                return;
            }

            foreach (var dependency in view.DependsOn)
            {
                if (byKey.TryGetValue(dependency, out var used))
                {
                    Visit(used);
                }
            }

            schema.Code.Add(view);
        }

        foreach (var view in views)
        {
            Visit(view);
        }

        schema.Code.AddRange(procedures);
        schema.Code.AddRange(triggers);
    }

    /// <summary>
    /// The column default as a MySQL expression. information_schema shows literal defaults unquoted ('abc' as abc) and
    /// expression defaults (DEFAULT_GENERATED) as they are written, e.g. CURRENT_TIMESTAMP(6) or uuid().
    /// </summary>
    private static string? Default(string? value, string dataType, string extra, string database)
    {
        if (value is null)
        {
            return null;
        }

        if (extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase) || CurrentTimestampRegex().IsMatch(value))
        {
            return MySqlSchemaInspector.Unqualify(value, database);
        }

        return QuotedDefaults.Contains(dataType)
            ? "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'"
            : value;
    }

    private static string? S(object? value) => value switch
    {
        null or DBNull => null,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    [GeneratedRegex(@"\A(?:CURRENT_TIMESTAMP|NOW|LOCALTIME|LOCALTIMESTAMP)(?:\(\d*\))?\z", RegexOptions.IgnoreCase)]
    private static partial Regex CurrentTimestampRegex();

    private sealed class TableParts(PortableName name, string? collation, string? comment)
    {
        public PortableName Name { get; } = name;

        public string? Collation { get; } = collation;

        public string? Comment { get; } = comment;

        public List<PortableColumn> Columns { get; } = [];

        public PortableKey? PrimaryKey { get; set; }

        public List<PortableKey> Uniques { get; } = [];

        public List<PortableCheck> Checks { get; } = [];

        public List<PortableForeignKey> ForeignKeys { get; } = [];

        public List<PortableIndex> Indexes { get; } = [];
    }
}
