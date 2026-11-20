using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql.Scripting;

/// <summary>A column line of SHOW CREATE TABLE: its name and full definition ("`total` decimal(12,2) NOT NULL").</summary>
internal sealed record MySqlColumn(string Name, string Definition)
{
    /// <summary>The definition without the name, to recognize a renamed column.</summary>
    public string Rest => Definition[(Definition.IndexOf(' ', StringComparison.Ordinal) + 1)..];
}

/// <summary>An index line: "PRIMARY", or the key's name, and its definition ("UNIQUE KEY `ux_email` (`email`)").</summary>
internal sealed record MySqlIndex(string Name, string Definition)
{
    public bool Primary => Name == "PRIMARY";
}

/// <summary>A CONSTRAINT line: a foreign key or a check, with the table it refers to (foreign keys).</summary>
internal sealed record MySqlConstraint(string Name, string Definition, bool ForeignKey, string? References);

internal sealed record MySqlTable(string Name, IReadOnlyList<MySqlColumn> Columns, IReadOnlyList<MySqlIndex> Indexes, IReadOnlyList<MySqlConstraint> Constraints, string Options)
{
    /// <summary>CREATE TABLE without its foreign keys (added after every table exists).</summary>
    public string CreateWithoutForeignKeys()
    {
        var lines = Columns.Select(c => c.Definition)
            .Concat(Indexes.Select(i => i.Definition))
            .Concat(Constraints.Where(c => !c.ForeignKey).Select(c => c.Definition));
        return $"CREATE TABLE {MySqlModel.Q(Name)} (\n  {string.Join(",\n  ", lines)}\n) {Options};";
    }
}

/// <summary>A view, routine, trigger or event: its kind ("view", "procedure", ...), name and portable CREATE statement.</summary>
internal sealed record MySqlObject(string Kind, string Name, string Create, string? Table = null)
{
    public string Key => $"{Kind} {Name}";
}

/// <summary>
/// The schema of the current database as SHOW CREATE prints it: canonical, with the database name and DEFINER clauses
/// removed so the statements are portable. trek's state tables and excluded objects are left out.
/// </summary>
internal sealed partial class MySqlModel
{
    public required string Database { get; init; }

    public Dictionary<string, MySqlTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MySqlObject> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<MySqlModel> ReadAsync(IDatabaseSession session, string stateSchema, Func<string, bool> excluded, CancellationToken cancellationToken)
    {
        var database = (string)(await session.QueryAsync("SELECT DATABASE() AS db;", null, cancellationToken).ConfigureAwait(false))[0]["db"]!;
        var prefix = MySqlStateStore.Prefix(stateSchema);
        var state = StateTables.Names.Select(n => prefix + n).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var model = new MySqlModel { Database = database };
        bool Skip(string name) => state.Contains(name) || excluded($"{database}.{name}") || excluded(name);

        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(string sql) =>
            await session.QueryAsync(sql, null, cancellationToken).ConfigureAwait(false);

        foreach (var row in await Query("SELECT table_name AS t, table_type AS type FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY table_name;"))
        {
            var name = (string)row["t"]!;
            if (Skip(name))
            {
                continue;
            }

            if ((string)row["type"]! == "VIEW")
            {
                var view = await Query($"SHOW CREATE VIEW {Q(name)};");
                model.Add("view", name, Text(view[0]["Create View"]));
            }
            else
            {
                var table = await Query($"SHOW CREATE TABLE {Q(name)};");
                model.Tables[name] = ParseTable(name, Portable(Text(table[0]["Create Table"]), database));
            }
        }

        foreach (var row in await Query("SELECT routine_type AS kind, routine_name AS name FROM information_schema.routines WHERE routine_schema = DATABASE() ORDER BY routine_name;"))
        {
            var (kind, name) = (((string)row["kind"]!).ToLowerInvariant(), (string)row["name"]!);
            if (!Skip(name))
            {
                var create = await Query($"SHOW CREATE {kind.ToUpperInvariant()} {Q(name)};");
                model.Add(kind, name, Text(create[0][kind == "procedure" ? "Create Procedure" : "Create Function"]));
            }
        }

        foreach (var row in await Query("SELECT trigger_name AS name, event_object_table AS t FROM information_schema.triggers WHERE trigger_schema = DATABASE() ORDER BY trigger_name;"))
        {
            var (name, table) = ((string)row["name"]!, (string)row["t"]!);
            if (!Skip(name) && !Skip(table))
            {
                var create = await Query($"SHOW CREATE TRIGGER {Q(name)};");
                model.Add("trigger", name, Text(create[0]["SQL Original Statement"]), table);
            }
        }

        foreach (var row in await Query("SELECT event_name AS name FROM information_schema.events WHERE event_schema = DATABASE() ORDER BY event_name;"))
        {
            var name = (string)row["name"]!;
            if (!Skip(name))
            {
                var create = await Query($"SHOW CREATE EVENT {Q(name)};");
                model.Add("event", name, Text(create[0]["Create Event"]));
            }
        }

        return model;
    }

    private void Add(string kind, string name, string create, string? table = null)
    {
        var o = new MySqlObject(kind, name, Portable(create, Database), table);
        Objects[o.Key] = o;
    }

    /// <summary>Parses SHOW CREATE TABLE: one column, key or constraint per line; AUTO_INCREMENT counters are not schema.</summary>
    public static MySqlTable ParseTable(string name, string create)
    {
        var lines = create.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var columns = new List<MySqlColumn>();
        var indexes = new List<MySqlIndex>();
        var constraints = new List<MySqlConstraint>();
        var options = string.Empty;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim().TrimEnd(',');
            if (line.StartsWith(')'))
            {
                options = AutoIncrementRegex().Replace(line[1..].Trim(), string.Empty).Trim();
                continue;
            }

            if (line.StartsWith('`'))
            {
                columns.Add(new MySqlColumn(Identifier(line), line));
            }
            else if (line.StartsWith("PRIMARY KEY", StringComparison.Ordinal))
            {
                indexes.Add(new MySqlIndex("PRIMARY", line));
            }
            else if (KeyRegex().Match(line) is { Success: true } key)
            {
                indexes.Add(new MySqlIndex(Identifier(line[key.Length..].TrimStart()), line));
            }
            else if (line.StartsWith("CONSTRAINT", StringComparison.Ordinal))
            {
                var constraintName = Identifier(line["CONSTRAINT".Length..].TrimStart());
                var references = ReferencesRegex().Match(line);
                constraints.Add(new MySqlConstraint(constraintName, line, line.Contains(" FOREIGN KEY ", StringComparison.Ordinal), references.Success ? Unquote(references.Groups["t"].Value) : null));
            }
        }

        return new MySqlTable(name, columns, indexes, constraints, options);
    }

    /// <summary>Removes DEFINER clauses and qualifiers for this database, so the statement runs in any environment.</summary>
    public static string Portable(string create, string database) =>
        DefinerRegex().Replace(MySqlSchemaInspector.Unqualify(create.Replace("\r\n", "\n", StringComparison.Ordinal), database), string.Empty).Trim();

    public static string Q(string name) => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`";

    /// <summary>The backquoted identifier a line starts with.</summary>
    private static string Identifier(string text)
    {
        if (!text.StartsWith('`'))
        {
            return text.Split(' ', 2)[0];
        }

        var i = 1;
        var name = new System.Text.StringBuilder();
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                if (i + 1 < text.Length && text[i + 1] == '`')
                {
                    name.Append('`');
                    i += 2;
                    continue;
                }

                break;
            }

            name.Append(text[i]);
            i++;
        }

        return name.ToString();
    }

    private static string Unquote(string name) => name.StartsWith('`') ? Identifier(name) : name;

    private static string Text(object? value) => value switch
    {
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };

    [GeneratedRegex(@"\s*\bAUTO_INCREMENT=\d+")]
    private static partial Regex AutoIncrementRegex();

    [GeneratedRegex(@"\A(?:UNIQUE|FULLTEXT|SPATIAL)?\s*KEY\b")]
    private static partial Regex KeyRegex();

    [GeneratedRegex(@"\bREFERENCES\s+(?<t>`(?:[^`]|``)+`|\S+)")]
    private static partial Regex ReferencesRegex();

    [GeneratedRegex(@"\s*\bDEFINER\s*=\s*(?:`(?:[^`]|``)*`|'[^']*'|\S+?)@(?:`(?:[^`]|``)*`|'[^']*'|\S+)")]
    private static partial Regex DefinerRegex();
}
