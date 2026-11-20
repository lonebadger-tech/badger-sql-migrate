using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql;

/// <summary>
/// Canonical, sorted dump of the current database's schema for the fingerprint, from information_schema: tables,
/// columns (in order), indexes, foreign keys, check constraints, views, routines, triggers and events. MySQL writes
/// the database's own name into stored definitions (`shop_prod`.`orders`): it is removed, so the same schema has the
/// same fingerprint in every environment. trek's state tables are left out; AUTO_INCREMENT counters are not schema.
/// </summary>
public sealed partial class MySqlSchemaInspector : ISchemaInspector
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "table", "column", "index", "foreign key", "check", "view", "routine", "trigger", "event", "sequence" };

    public async Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken)
    {
        var database = (string)(await session.QueryAsync("SELECT DATABASE() AS db;", null, cancellationToken).ConfigureAwait(false))[0]["db"]!;
        var mariaDb = session is MySqlSession { IsMariaDb: true };
        var prefix = MySqlStateStore.Prefix(stateSchema);
        var stateTables = StateTables.Names.Select(n => prefix + n).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Mine(object? table) => table is string t && !stateTables.Contains(t);
        string Local(object? definition) => Unqualify(S(definition), database);

        var lines = new List<string>();
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) =>
            await session.QueryAsync(sql, null, cancellationToken).ConfigureAwait(false);

        foreach (var r in (await Q("""
            SELECT table_name AS t, engine, table_collation AS coll, table_comment AS cmt
              FROM information_schema.tables
             WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
            """)).Where(r => Mine(r["t"])))
        {
            lines.Add($"table|{S(r["t"])}|engine={S(r["engine"])}|collation={S(r["coll"])}|comment={S(r["cmt"])}");
        }

        foreach (var r in (await Q("""
            SELECT c.table_name AS t, c.ordinal_position AS pos, c.column_name AS col, c.column_type AS type, c.is_nullable AS nullable,
                   c.column_default AS def, c.extra, c.collation_name AS coll, c.generation_expression AS gen, c.column_comment AS cmt
              FROM information_schema.columns c
              JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE'
             WHERE c.table_schema = DATABASE()
            """)).Where(r => Mine(r["t"])))
        {
            // DEFAULT_GENERATED only says the default is an expression, which the default itself shows.
            var extra = Regex.Replace(S(r["extra"]), @"\bDEFAULT_GENERATED\b", string.Empty).Trim();
            lines.Add($"column|{S(r["t"])}|{Convert.ToInt64(r["pos"], CultureInfo.InvariantCulture):D4}|{S(r["col"])}|{S(r["type"])}|null={S(r["nullable"])}|default={(r["def"] is null ? "(none)" : S(r["def"]))}|extra={extra}|collation={S(r["coll"])}|generated={Local(r["gen"])}|comment={S(r["cmt"])}");
        }

        // MariaDB has no functional indexes, and calls invisible indexes ignored.
        var indexes = (await Q($"""
            SELECT table_name AS t, index_name AS name, non_unique, seq_in_index AS seq, column_name AS col, sub_part, collation AS dir,
                   index_type AS type, {(mariaDb ? "NULL AS expression, IF(ignored = 'YES', 'NO', 'YES') AS visible" : "expression, is_visible AS visible")}
              FROM information_schema.statistics
             WHERE table_schema = DATABASE()
            """)).Where(r => Mine(r["t"]));
        foreach (var index in indexes.GroupBy(r => (T: S(r["t"]), Name: S(r["name"]))))
        {
            var first = index.First();
            var parts = index.OrderBy(r => Convert.ToInt64(r["seq"], CultureInfo.InvariantCulture)).Select(r =>
                (r["col"] is null ? $"({Local(r["expression"])})" : S(r["col"]))
                + (r["sub_part"] is null ? string.Empty : $"({S(r["sub_part"])})")
                + (S(r["dir"]) == "D" ? " DESC" : string.Empty));
            var unique = Convert.ToInt64(first["non_unique"], CultureInfo.InvariantCulture) == 0;
            lines.Add($"index|{index.Key.T}|{index.Key.Name}|{(unique ? "unique" : "non-unique")}|{S(first["type"])}|visible={S(first["visible"])}|{string.Join(", ", parts)}");
        }

        var fkColumns = (await Q("""
            SELECT table_name AS t, constraint_name AS name, column_name AS col, referenced_column_name AS ref_col, ordinal_position AS pos
              FROM information_schema.key_column_usage
             WHERE table_schema = DATABASE() AND referenced_table_name IS NOT NULL
            """)).ToLookup(r => (S(r["t"]), S(r["name"])));
        foreach (var r in (await Q("""
            SELECT table_name AS t, constraint_name AS name, referenced_table_name AS ref, unique_constraint_schema AS ref_db,
                   update_rule, delete_rule
              FROM information_schema.referential_constraints
             WHERE constraint_schema = DATABASE()
            """)).Where(r => Mine(r["t"])))
        {
            var cols = fkColumns[(S(r["t"]), S(r["name"]))].OrderBy(c => Convert.ToInt64(c["pos"], CultureInfo.InvariantCulture)).ToList();
            var target = S(r["ref_db"]) == database ? S(r["ref"]) : $"{S(r["ref_db"])}.{S(r["ref"])}";
            lines.Add($"foreign key|{S(r["t"])}|{S(r["name"])}|({string.Join(", ", cols.Select(c => S(c["col"])))}) -> {target} ({string.Join(", ", cols.Select(c => S(c["ref_col"])))})|on update {S(r["update_rule"])}|on delete {S(r["delete_rule"])}");
        }

        // MariaDB has no NOT ENFORCED checks, and names checks per table.
        foreach (var r in (await Q($"""
            SELECT tc.table_name AS t, tc.constraint_name AS name, cc.check_clause AS clause, {(mariaDb ? "'YES' AS enforced" : "tc.enforced")}
              FROM information_schema.table_constraints tc
              JOIN information_schema.check_constraints cc ON cc.constraint_schema = tc.constraint_schema AND cc.constraint_name = tc.constraint_name{(mariaDb ? " AND cc.table_name = tc.table_name" : string.Empty)}
             WHERE tc.table_schema = DATABASE() AND tc.constraint_type = 'CHECK'
            """)).Where(r => Mine(r["t"])))
        {
            lines.Add($"check|{S(r["t"])}|{S(r["name"])}|enforced={S(r["enforced"])}|{Local(r["clause"])}");
        }

        foreach (var r in await Q("""
            SELECT table_name AS name, view_definition AS def, check_option, security_type
              FROM information_schema.views
             WHERE table_schema = DATABASE()
            """))
        {
            lines.Add($"view|{S(r["name"])}|check={S(r["check_option"])}|security={S(r["security_type"])}\n{Local(r["def"])}");
        }

        var parameters = (await Q("""
            SELECT specific_name AS name, routine_type AS kind, ordinal_position AS pos, parameter_mode AS mode,
                   parameter_name AS param, dtd_identifier AS type
              FROM information_schema.parameters
             WHERE specific_schema = DATABASE()
            """)).ToLookup(r => (S(r["kind"]), S(r["name"])));
        foreach (var r in await Q("""
            SELECT routine_name AS name, routine_type AS kind, dtd_identifier AS returns, routine_definition AS def,
                   is_deterministic, sql_data_access, security_type
              FROM information_schema.routines
             WHERE routine_schema = DATABASE()
            """))
        {
            var args = parameters[(S(r["kind"]), S(r["name"]))]
                .Where(p => Convert.ToInt64(p["pos"], CultureInfo.InvariantCulture) > 0)
                .OrderBy(p => Convert.ToInt64(p["pos"], CultureInfo.InvariantCulture))
                .Select(p => $"{S(p["mode"])} {S(p["param"])} {S(p["type"])}".Trim());
            lines.Add($"routine|{S(r["kind"]).ToLowerInvariant()} {S(r["name"])}({string.Join(", ", args)})|returns={S(r["returns"])}|deterministic={S(r["is_deterministic"])}|{S(r["sql_data_access"])}|security={S(r["security_type"])}\n{Local(r["def"])}");
        }

        foreach (var r in (await Q("""
            SELECT trigger_name AS name, event_object_table AS t, action_timing AS timing, event_manipulation AS event,
                   action_order AS ord, action_statement AS body
              FROM information_schema.triggers
             WHERE trigger_schema = DATABASE()
            """)).Where(r => Mine(r["t"])))
        {
            lines.Add($"trigger|{S(r["t"])}|{S(r["name"])}|{S(r["timing"])} {S(r["event"])}|order={S(r["ord"])}\n{Local(r["body"])}");
        }

        foreach (var r in await Q("""
            SELECT event_name AS name, event_type AS type, interval_value AS every, interval_field AS unit, status, event_definition AS body
              FROM information_schema.events
             WHERE event_schema = DATABASE()
            """))
        {
            lines.Add($"event|{S(r["name"])}|{S(r["type"])} {S(r["every"])} {S(r["unit"])}|{S(r["status"])}\n{Local(r["body"])}");
        }

        if (mariaDb)
        {
            // MariaDB sequences are tables of type SEQUENCE; SHOW CREATE SEQUENCE gives their options.
            foreach (var r in await Q("SELECT table_name AS name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'SEQUENCE'"))
            {
                var create = await Q($"SHOW CREATE SEQUENCE `{S(r["name"]).Replace("`", "``", StringComparison.Ordinal)}`");
                lines.Add($"sequence|{S(r["name"])}|{S(create[0]["Create Table"])}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>Entries keyed by kind and object, e.g. "column orders.total", "index orders.ix_customer".</summary>
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
            var f = lines[0].Split('|');
            string Field(int i) => i < f.Length ? f[i] : string.Empty;
            var key = f[0] switch
            {
                "column" => $"column {Field(1)}.{Field(3)}",
                "index" or "foreign key" or "check" or "trigger" => $"{f[0]} {Field(1)}.{Field(2)}",
                _ => $"{f[0]} {Field(1)}",
            };
            return new SchemaEntry(key, string.Join('\n', lines));
        }
    }

    private static string S(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
    };

    /// <summary>Removes `database`. qualifiers for the current database from a stored definition.</summary>
    internal static string Unqualify(string definition, string database) =>
        definition.Replace($"`{database.Replace("`", "``", StringComparison.Ordinal)}`.", string.Empty, StringComparison.Ordinal);
}
