using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Oracle;

/// <summary>
/// Canonical, sorted dump of the deploy schema for the fingerprint, from the ALL_* dictionary views: tables, columns
/// (in order), identity options, constraints, indexes, views, materialized views, sequences, synonyms, PL/SQL source
/// (packages, procedures, functions, types, triggers), triggers and comments. Names Oracle generates (SYS_C...
/// constraints and their indexes, ISEQ$$ identity sequences, SYS_NC expression columns) are left out or written by
/// content, so the same schema has the same fingerprint everywhere; so is the schema's own name inside definitions.
/// trek's state tables, the recycle bin and current sequence values are not schema.
/// </summary>
public sealed partial class OracleSchemaInspector : ISchemaInspector
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "table", "column", "identity", "constraint", "index", "view", "mview", "sequence", "synonym", "source", "trigger", "comment",
    };

    public async Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken)
    {
        var owner = OracleStateStore.Owner(session);
        var prefix = OracleStateStore.Prefix(stateSchema);
        var state = StateTables.Names.Append(OracleStateStore.LockTable).Select(n => prefix + n.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        var p = new Dictionary<string, object?> { ["o"] = owner };
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) =>
            await session.QueryAsync(sql, p, cancellationToken).ConfigureAwait(false);
        string Local(object? definition) => Unqualify(S(definition), owner);

        var lines = new List<string>();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in await Q("""
            SELECT table_name AS t, temporary AS tmp, duration AS dur, iot_type AS iot, partitioned AS part
              FROM all_tables
             WHERE owner = :o AND nested = 'NO' AND dropped = 'NO' AND (iot_type IS NULL OR iot_type = 'IOT')
            """))
        {
            var t = S(r["t"]);
            if (state.Contains(t) || System(t))
            {
                continue;
            }

            tables.Add(t);
            lines.Add($"table|{t}|temporary={S(r["tmp"])} {S(r["dur"])}|iot={S(r["iot"])}|partitioned={S(r["part"])}");
        }

        // Expression columns of function-based indexes are hidden; identity defaults name a generated sequence.
        foreach (var r in (await Q("""
            SELECT table_name AS t, column_id AS pos, column_name AS c, data_type AS type, data_length AS len, char_length AS clen,
                   char_used AS cused, data_precision AS prec, data_scale AS scale, nullable AS nul, data_default AS def,
                   virtual_column AS virt, identity_column AS ident, default_on_null AS donull
              FROM all_tab_cols
             WHERE owner = :o AND hidden_column = 'NO'
            """)).Where(r => tables.Contains(S(r["t"]))))
        {
            var identity = S(r["ident"]) == "YES";
            var def = identity ? string.Empty : Local(r["def"]);
            lines.Add($"column|{S(r["t"])}|{Convert.ToInt64(r["pos"], CultureInfo.InvariantCulture):D4}|{S(r["c"])}|{Type(r)}|null={S(r["nul"])}|default={def}|virtual={S(r["virt"])}|identity={S(r["ident"])}|on null={S(r["donull"])}");
        }

        foreach (var r in (await Q("SELECT table_name AS t, column_name AS c, generation_type AS gen, identity_options AS opts FROM all_tab_identity_cols WHERE owner = :o"))
            .Where(r => tables.Contains(S(r["t"]))))
        {
            lines.Add($"identity|{S(r["t"])}|{S(r["c"])}|{S(r["gen"])}|{S(r["opts"])}");
        }

        // Constraints. A NOT NULL check Oracle made for a NOT NULL column is the column's null=N already.
        var consColumns = (await Q("SELECT constraint_name AS name, table_name AS t, column_name AS c, position AS pos FROM all_cons_columns WHERE owner = :o"))
            .ToLookup(r => S(r["name"]));
        string Columns(string constraint) => string.Join(", ", consColumns[constraint].OrderBy(c => Convert.ToInt64(c["pos"] ?? 0L, CultureInfo.InvariantCulture)).Select(c => S(c["c"])));
        var constraints = (await Q("""
            SELECT constraint_name AS name, constraint_type AS type, table_name AS t, search_condition_vc AS cond, r_owner AS rowner,
                   r_constraint_name AS rname, delete_rule AS del, deferrable AS defer, deferred AS deferred, status, validated, generated AS gen
              FROM all_constraints
             WHERE owner = :o AND constraint_type IN ('P', 'U', 'R', 'C')
            """)).Where(r => tables.Contains(S(r["t"]))).ToList();
        var referenced = constraints.ToDictionary(r => S(r["name"]), r => S(r["t"]), StringComparer.Ordinal);
        foreach (var r in constraints)
        {
            var type = S(r["type"]);
            var generated = S(r["gen"]) == "GENERATED NAME";
            var condition = Local(r["cond"]);
            if (type == "C" && generated && NotNullRegex().IsMatch(condition))
            {
                continue;
            }

            var name = generated ? "(generated)" : S(r["name"]);
            var detail = type switch
            {
                "P" => $"primary key ({Columns(S(r["name"]))})",
                "U" => $"unique ({Columns(S(r["name"]))})",
                "R" => $"foreign key ({Columns(S(r["name"]))}) -> {(S(r["rowner"]) == owner ? string.Empty : S(r["rowner"]) + ".")}{referenced.GetValueOrDefault(S(r["rname"]), S(r["rname"]))} ({Columns(S(r["rname"]))}) on delete {S(r["del"])}",
                _ => $"check ({condition})",
            };
            lines.Add($"constraint|{S(r["t"])}|{name}|{detail}|{S(r["defer"])} {S(r["deferred"])}|{S(r["status"])} {S(r["validated"])}");
        }

        // Indexes: expression parts replace the generated column names.
        var expressions = (await Q("SELECT index_name AS i, column_position AS pos, column_expression AS expr FROM all_ind_expressions WHERE index_owner = :o"))
            .ToDictionary(r => (S(r["i"]), Convert.ToInt64(r["pos"], CultureInfo.InvariantCulture)), r => Local(r["expr"]));
        var indexColumns = (await Q("SELECT index_name AS i, column_name AS c, column_position AS pos, descend FROM all_ind_columns WHERE index_owner = :o"))
            .ToLookup(r => S(r["i"]));
        foreach (var r in (await Q("""
            SELECT index_name AS i, table_name AS t, index_type AS type, uniqueness AS uniq, generated AS gen
              FROM all_indexes
             WHERE owner = :o AND table_owner = :o AND index_type NOT IN ('LOB', 'IOT - TOP')
            """)).Where(r => tables.Contains(S(r["t"]))))
        {
            var index = S(r["i"]);
            var parts = indexColumns[index].OrderBy(c => Convert.ToInt64(c["pos"], CultureInfo.InvariantCulture)).Select(c =>
                (expressions.TryGetValue((index, Convert.ToInt64(c["pos"], CultureInfo.InvariantCulture)), out var e) ? $"({e})" : S(c["c"]))
                + (S(c["descend"]) == "DESC" ? " DESC" : string.Empty));
            var name = S(r["gen"]) == "Y" ? "(generated)" : index;
            lines.Add($"index|{S(r["t"])}|{name}|{S(r["uniq"])}|{S(r["type"]).Replace("FUNCTION-BASED ", string.Empty, StringComparison.Ordinal)}|{string.Join(", ", parts)}");
        }

        foreach (var r in await Q("SELECT view_name AS name, text, read_only AS ro FROM all_views WHERE owner = :o"))
        {
            lines.Add($"view|{S(r["name"])}|read only={S(r["ro"])}\n{Local(r["text"])}");
        }

        foreach (var r in await Q("SELECT mview_name AS name, query, refresh_method AS rmethod, refresh_mode AS rmode FROM all_mviews WHERE owner = :o"))
        {
            tables.Remove(S(r["name"]));
            lines.Add($"mview|{S(r["name"])}|refresh {S(r["rmethod"])} on {S(r["rmode"])}\n{Local(r["query"])}");
        }

        foreach (var r in await Q("""
            SELECT sequence_name AS name, min_value AS lo, max_value AS hi, increment_by AS inc, cycle_flag AS cyc, order_flag AS ord, cache_size AS cache
              FROM all_sequences
             WHERE sequence_owner = :o AND sequence_name NOT LIKE 'ISEQ$$%'
            """))
        {
            lines.Add($"sequence|{S(r["name"])}|min={S(r["lo"])}|max={S(r["hi"])}|increment={S(r["inc"])}|cycle={S(r["cyc"])}|order={S(r["ord"])}|cache={S(r["cache"])}");
        }

        foreach (var r in await Q("SELECT synonym_name AS name, table_owner AS towner, table_name AS t, db_link AS link FROM all_synonyms WHERE owner = :o"))
        {
            var target = (S(r["towner"]) == owner ? string.Empty : S(r["towner"]) + ".") + S(r["t"]) + (S(r["link"]) is { Length: > 0 } link ? "@" + link : string.Empty);
            lines.Add($"synonym|{S(r["name"])}|{target}");
        }

        var source = new Dictionary<(string Type, string Name), StringBuilder>();
        foreach (var r in await Q("""
            SELECT type, name, text
              FROM all_source
             WHERE owner = :o AND type IN ('PROCEDURE', 'FUNCTION', 'PACKAGE', 'PACKAGE BODY', 'TRIGGER', 'TYPE', 'TYPE BODY')
             ORDER BY type, name, line
            """))
        {
            var key = (S(r["type"]), S(r["name"]));
            if (!source.TryGetValue(key, out var text))
            {
                source[key] = text = new StringBuilder();
            }

            text.Append(r["text"] as string);
        }

        foreach (var ((type, name), text) in source)
        {
            if (!System(name))
            {
                lines.Add($"source|{type.ToLowerInvariant()} {name}\n{Unqualify(text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(), owner)}");
            }
        }

        foreach (var r in await Q("SELECT trigger_name AS name, table_name AS t, trigger_type AS type, triggering_event AS event, status FROM all_triggers WHERE owner = :o"))
        {
            lines.Add($"trigger|{S(r["t"])}|{S(r["name"])}|{S(r["type"])}|{S(r["event"])}|{S(r["status"])}");
        }

        foreach (var r in (await Q("SELECT table_name AS t, comments AS c FROM all_tab_comments WHERE owner = :o AND comments IS NOT NULL")).Where(r => tables.Contains(S(r["t"]))))
        {
            lines.Add($"comment|{S(r["t"])}||{S(r["c"])}");
        }

        foreach (var r in (await Q("SELECT table_name AS t, column_name AS col, comments AS c FROM all_col_comments WHERE owner = :o AND comments IS NOT NULL")).Where(r => tables.Contains(S(r["t"]))))
        {
            lines.Add($"comment|{S(r["t"])}|{S(r["col"])}|{S(r["c"])}");
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>Entries keyed by kind and object, e.g. "column ORDERS.TOTAL", "index ORDERS.IX_CUSTOMER".</summary>
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

        // Keys must be unique; two generated constraints with the same content cannot be told apart, so number them.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var unique = new List<SchemaEntry>(entries.Count);
        foreach (var e in entries)
        {
            var n = seen[e.Key] = seen.GetValueOrDefault(e.Key) + 1;
            unique.Add(n == 1 ? e : e with { Key = $"{e.Key} #{n}" });
        }

        return unique;

        static SchemaEntry Entry(List<string> lines)
        {
            var f = lines[0].Split('|');
            string Field(int i) => i < f.Length ? f[i] : string.Empty;
            var key = f[0] switch
            {
                "column" => $"column {Field(1)}.{Field(3)}",
                "constraint" when Field(2) == "(generated)" => $"constraint {Field(1)}.{Field(3)}",
                "index" when Field(2) == "(generated)" => $"index {Field(1)}.{Field(3)} {Field(5)}",
                "constraint" or "index" or "trigger" => $"{f[0]} {Field(1)}.{Field(2)}",
                "identity" => $"identity {Field(1)}.{Field(2)}",
                "comment" => $"comment {Field(1)}{(Field(2).Length > 0 ? "." + Field(2) : string.Empty)}",
                _ => $"{f[0]} {Field(1)}",
            };
            return new SchemaEntry(key, string.Join('\n', lines));
        }
    }

    /// <summary>The column type as DDL writes it: VARCHAR2(20 CHAR), NUMBER(12,2), RAW(16), TIMESTAMP(6) ...</summary>
    private static string Type(IReadOnlyDictionary<string, object?> r)
    {
        var type = S(r["type"]);
        string N(string key) => S(r[key]);
        return type switch
        {
            "VARCHAR2" or "CHAR" => $"{type}({N("clen")} {(N("cused") == "C" ? "CHAR" : "BYTE")})",
            "NVARCHAR2" or "NCHAR" => $"{type}({N("clen")})",
            "RAW" => $"RAW({N("len")})",
            "NUMBER" when N("prec").Length > 0 => N("scale") is { Length: > 0 } s && s != "0" ? $"NUMBER({N("prec")},{s})" : $"NUMBER({N("prec")})",
            "NUMBER" when N("scale") == "0" => "INTEGER",
            "FLOAT" => $"FLOAT({N("prec")})",
            _ => type,
        };
    }

    /// <summary>Objects Oracle makes itself, and the recycle bin.</summary>
    private static bool System(string name) =>
        name.StartsWith("BIN$", StringComparison.Ordinal) || name.StartsWith("SYS_", StringComparison.Ordinal) || name.StartsWith("ISEQ$$", StringComparison.Ordinal)
        || name.StartsWith("MLOG$_", StringComparison.Ordinal) || name.StartsWith("RUPD$_", StringComparison.Ordinal) || name.StartsWith("DR$", StringComparison.Ordinal);

    private static string S(object? value) => value switch
    {
        null => string.Empty,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
    };

    /// <summary>Removes the schema's own qualifier ("SHOP". or SHOP.) from a stored definition.</summary>
    internal static string Unqualify(string definition, string owner)
    {
        var quoted = definition.Replace("\"" + owner + "\".", string.Empty, StringComparison.Ordinal);
        return Regex.Replace(quoted, $@"(?<![\w$#""]){Regex.Escape(owner)}\.(?=[A-Za-z""])", string.Empty, RegexOptions.IgnoreCase);
    }

    [GeneratedRegex(@"\A""[^""]+""\s+IS\s+NOT\s+NULL\z", RegexOptions.IgnoreCase)]
    private static partial Regex NotNullRegex();
}
