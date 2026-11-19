using System.Globalization;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.Sqlite.Scripting;

/// <param name="Name">Column name.</param>
/// <param name="Definition">The whole column definition, normalized (name included).</param>
/// <param name="Rest">The definition after the name, normalized.</param>
/// <param name="Hidden">pragma_table_xinfo.hidden: 2 and 3 are generated columns.</param>
internal sealed record SqliteColumn(string Name, string Definition, string Rest, int Hidden)
{
    public bool Generated => Hidden >= 2;
}

/// <param name="Sql">The CREATE statement as SQLite stores it.</param>
/// <param name="Body">The CREATE TABLE text from the column list on (columns, constraints, options).</param>
/// <param name="Options">What follows the column list (WITHOUT ROWID, STRICT), normalized.</param>
/// <param name="Virtual">A virtual table (FTS, R*Tree ...): compared and scripted as a whole.</param>
/// <param name="References">Foreign keys: (referenced table, ON DELETE action).</param>
internal sealed record SqliteTable(
    string Name,
    string Sql,
    string Body,
    IReadOnlyList<SqliteColumn> Columns,
    IReadOnlyList<string> Constraints,
    string Options,
    bool Virtual,
    long Order,
    IReadOnlyList<(string Table, string OnDelete)> References)
{
    public string Normalized { get; } = SqliteLexer.Normalize(Sql);

    public bool SameShape(SqliteTable other) =>
        Virtual == other.Virtual
        && (Virtual
            ? Normalized == other.Normalized
            : Options == other.Options && Constraints.SequenceEqual(other.Constraints) && Columns.Select(c => c.Definition).SequenceEqual(other.Columns.Select(c => c.Definition)));
}

/// <param name="Type">index, view or trigger.</param>
/// <param name="Table">The table an index or trigger belongs to (the view itself for a view).</param>
/// <param name="Words">Every identifier in the definition, lower case: what it may refer to.</param>
internal sealed record SqliteObject(string Type, string Name, string Table, string Sql, string Normalized, long Order, IReadOnlySet<string> Words);

/// <summary>
/// The user schema of a SQLite database, read from sqlite_schema and the table pragmas. SQLite keeps each object's
/// CREATE statement as written, so definitions are compared normalized (layout and identifier quoting ignored), and
/// tables element by element so a column added with ALTER TABLE equals the same column written in CREATE TABLE.
/// </summary>
internal sealed class SqliteModel
{
    public Dictionary<string, SqliteTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Indexes, views and triggers keyed by "type name" (names compare case-insensitively).</summary>
    public Dictionary<string, SqliteObject> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(string type, string name) => type + " " + name;

    public static async Task<SqliteModel> ReadAsync(IDatabaseSession session, string stateSchema, Func<string, bool> excluded, CancellationToken cancellationToken)
    {
        var prefix = SqliteStateStore.Prefix(stateSchema);
        var stateTables = SqliteStateStore.Tables.Select(t => prefix + t).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var model = new SqliteModel();

        var kinds = (await session.QueryAsync("SELECT name, type FROM pragma_table_list WHERE schema = 'main';", null, cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => (string)r["name"]!, r => (string)r["type"]!, StringComparer.OrdinalIgnoreCase);
        var rows = await session.QueryAsync("SELECT type, name, tbl_name, sql, rowid AS ord FROM main.sqlite_schema WHERE sql IS NOT NULL ORDER BY rowid;", null, cancellationToken).ConfigureAwait(false);

        bool Skip(string name) =>
            name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) || stateTables.Contains(name) || excluded("main." + name)
            || (kinds.TryGetValue(name, out var kind) && kind == "shadow");

        foreach (var r in rows)
        {
            var type = (string)r["type"]!;
            var name = (string)r["name"]!;
            var table = (string)r["tbl_name"]!;
            var sql = ((string)r["sql"]!).Replace("\r\n", "\n", StringComparison.Ordinal);
            var order = Convert.ToInt64(r["ord"], CultureInfo.InvariantCulture);
            if (Skip(name) || Skip(table))
            {
                continue;
            }

            if (type == "table")
            {
                model.Tables[name] = await ReadTableAsync(session, name, sql, order, cancellationToken).ConfigureAwait(false);
            }
            else if (type is "index" or "view" or "trigger")
            {
                var tokens = SqliteLexer.Tokenize(sql);
                var words = tokens.Where(t => t.Kind is TokenKind.Word or TokenKind.QuotedIdentifier)
                    .Select(t => SqliteLexer.Identifier(sql, t).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                model.Objects[Key(type, name)] = new SqliteObject(type, name, table, sql, SqliteLexer.Normalize(sql, tokens), order, words);
            }
        }

        return model;
    }

    private static async Task<SqliteTable> ReadTableAsync(IDatabaseSession session, string name, string sql, long order, CancellationToken cancellationToken)
    {
        var p = new Dictionary<string, object?> { ["t"] = name };
        var references = (await session.QueryAsync("SELECT DISTINCT \"table\" AS tbl, on_delete FROM pragma_foreign_key_list(@t);", p, cancellationToken).ConfigureAwait(false))
            .Select(r => ((string)r["tbl"]!, ((string?)r["on_delete"] ?? "NO ACTION").ToUpperInvariant())).ToList();

        var tokens = SqliteLexer.Tokenize(sql);
        var isVirtual = tokens.Where(t => !t.IsTrivia).Take(2).LastOrDefault() is var second && SqliteLexer.IsWord(sql, second, "VIRTUAL");
        if (isVirtual)
        {
            return new SqliteTable(name, sql, string.Empty, [], [], string.Empty, true, order, references);
        }

        var hidden = (await session.QueryAsync("SELECT name, hidden FROM pragma_table_xinfo(@t);", p, cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => (string)r["name"]!, r => Convert.ToInt32(r["hidden"], CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);

        var (columns, constraints, options, bodyStart) = ParseCreateTable(sql, tokens);
        return new SqliteTable(
            name,
            sql,
            bodyStart < 0 ? string.Empty : sql[bodyStart..],
            columns.Select(c => c with { Hidden = hidden.GetValueOrDefault(c.Name) }).ToList(),
            constraints,
            options,
            false,
            order,
            references);
    }

    private static readonly HashSet<string> ConstraintStarts = new(StringComparer.OrdinalIgnoreCase) { "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN" };

    /// <summary>Splits CREATE TABLE into column definitions, table constraints and the options after the list.</summary>
    internal static (List<SqliteColumn> Columns, List<string> Constraints, string Options, int BodyStart) ParseCreateTable(string sql, List<Token> tokens)
    {
        var columns = new List<SqliteColumn>();
        var constraints = new List<string>();
        var open = tokens.FindIndex(t => t.Kind == TokenKind.Punctuation && sql[t.Start] == '(');
        if (open < 0)
        {
            return (columns, constraints, SqliteLexer.Normalize(sql, tokens), -1);
        }

        var depth = 0;
        var element = new List<Token>();
        var close = tokens.Count;
        for (var i = open + 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var c = t.Kind == TokenKind.Punctuation ? sql[t.Start] : '\0';
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if ((c == ',' || c == ')') && depth == 0)
            {
                Add(element);
                element = [];
                if (c == ')')
                {
                    close = i;
                    break;
                }

                continue;
            }

            element.Add(t);
        }

        var options = SqliteLexer.Normalize(sql, tokens.Skip(close + 1));
        return (columns, constraints, options, tokens[open].Start);

        void Add(List<Token> e)
        {
            var significant = e.Where(t => !t.IsTrivia).ToList();
            if (significant.Count == 0)
            {
                return;
            }

            var first = significant[0];
            if (first.Kind == TokenKind.Word && ConstraintStarts.Contains(sql.Substring(first.Start, first.Length)))
            {
                constraints.Add(SqliteLexer.Normalize(sql, significant));
                return;
            }

            var name = SqliteLexer.Identifier(sql, first);
            columns.Add(new SqliteColumn(name, SqliteLexer.Normalize(sql, significant), SqliteLexer.Normalize(sql, significant.Skip(1)), 0));
        }
    }

    /// <summary>The canonical dump the schema fingerprint is the hash of: one entry per object, sorted.</summary>
    public string Dump()
    {
        var lines = new List<string>();
        foreach (var t in Tables.Values)
        {
            if (t.Virtual)
            {
                lines.Add($"virtual table|main.{t.Name}|{t.Normalized}");
                continue;
            }

            lines.Add($"table|main.{t.Name}|{t.Options}");
            for (var i = 0; i < t.Columns.Count; i++)
            {
                lines.Add($"column|main.{t.Name}|{(i + 1).ToString(CultureInfo.InvariantCulture)}|{t.Columns[i].Name}|{t.Columns[i].Rest}");
            }

            lines.AddRange(t.Constraints.Select(c => $"constraint|main.{t.Name}|{c}"));
        }

        foreach (var o in Objects.Values)
        {
            lines.Add(o.Type == "view" ? $"view|main.{o.Name}|{o.Normalized}" : $"{o.Type}|main.{o.Table}|{o.Name}|{o.Normalized}");
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }
}
