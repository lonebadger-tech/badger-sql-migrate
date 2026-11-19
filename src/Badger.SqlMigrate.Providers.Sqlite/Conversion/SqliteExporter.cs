using System.Globalization;
using Badger.SqlMigrate.Core.Conversion;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.Sqlite.Scripting;

namespace Badger.SqlMigrate.Providers.Sqlite.Conversion;

/// <summary>
/// Reads a SQLite database into a <see cref="PortableSchema"/>: columns, keys and indexes from the table pragmas, CHECK
/// constraints and generated columns from the stored CREATE TABLE text, views and triggers as written. Everything lives
/// in schema "main". An INTEGER PRIMARY KEY (the rowid) becomes an identity.
/// </summary>
public sealed class SqliteExporter : ISchemaExporter
{
    private const string Main = "main";

    public async Task<PortableSchema> ExportAsync(IDatabaseSession session, ExportOptions options, CancellationToken cancellationToken)
    {
        var model = await SqliteModel.ReadAsync(session, options.StateSchema, name => options.Excluded(new PortableName(Main, name[(Main.Length + 1)..])), cancellationToken).ConfigureAwait(false);
        var schema = new PortableSchema { SourceProvider = SqliteProvider.ProviderId, DefaultSchema = Main };
        foreach (var table in model.Tables.Values.OrderBy(t => t.Order))
        {
            if (table.Virtual)
            {
                schema.Warnings.Add($"virtual table {table.Name} ({Collapse(table.Sql)}) is not converted.");
                continue;
            }

            schema.Tables.Add(await TableAsync(session, table, cancellationToken).ConfigureAwait(false));
        }

        var names = model.Objects.Values.Where(o => o.Type == "view").Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var code = new List<PortableCode>();
        foreach (var o in model.Objects.Values.Where(o => o.Type is "view" or "trigger").OrderBy(o => o.Order))
        {
            var name = new PortableName(Main, o.Name);
            code.Add(new PortableCode(o.Type == "view" ? PortableCodeKind.View : PortableCodeKind.Trigger, name, o.Sql)
            {
                Language = "sqlite",
                Table = o.Type == "trigger" ? new PortableName(Main, o.Table) : null,
                DependsOn = o.Words.Where(w => names.Contains(w) && !w.Equals(o.Name, StringComparison.OrdinalIgnoreCase)).Select(w => new PortableName(Main, w).Key).ToList(),
            });
        }

        schema.Code.AddRange(CodeOrder.Sort(code));
        return schema;
    }

    private static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static async Task<PortableTable> TableAsync(IDatabaseSession session, SqliteTable table, CancellationToken ct)
    {
        var p = new Dictionary<string, object?> { ["t"] = table.Name };
        var info = await session.QueryAsync("SELECT name, type, \"notnull\" AS nn, dflt_value, pk, hidden FROM pragma_table_xinfo(@t) ORDER BY cid;", p, ct).ConfigureAwait(false);
        var definitions = table.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var pk = info.Where(r => Convert.ToInt32(r["pk"], CultureInfo.InvariantCulture) > 0).OrderBy(r => Convert.ToInt32(r["pk"], CultureInfo.InvariantCulture)).Select(r => (string)r["name"]!).ToList();
        var checks = new List<PortableCheck>();
        var columns = new List<PortableColumn>();
        foreach (var r in info)
        {
            var name = (string)r["name"]!;
            var type = ((string?)r["type"] ?? string.Empty).Trim();
            var hidden = Convert.ToInt32(r["hidden"], CultureInfo.InvariantCulture);
            var rest = definitions.TryGetValue(name, out var d) ? d.Rest : string.Empty;
            var rowid = pk.Count == 1 && pk[0] == name && type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) && !table.Options.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase);
            string? computed = null;
            if (hidden >= 2)
            {
                computed = After(rest, "AS");
            }

            foreach (var check in Clauses(rest, "CHECK"))
            {
                checks.Add(new PortableCheck(null, check));
            }

            columns.Add(new PortableColumn(name, type, Convert.ToInt32(r["nn"], CultureInfo.InvariantCulture) == 0 && !rowid)
            {
                Default = r["dflt_value"] as string,
                Identity = rowid ? new PortableIdentity(1, 1, false) : null,
                Computed = computed,
                ComputedStored = hidden == 3,
                Collation = Word(rest, "COLLATE"),
            });
        }

        foreach (var constraint in table.Constraints)
        {
            var tokens = SqliteLexer.Tokenize(constraint).Where(t => !t.IsTrivia).ToList();
            var named = tokens.Count > 2 && SqliteLexer.IsWord(constraint, tokens[0], "CONSTRAINT") ? SqliteLexer.Identifier(constraint, tokens[1]) : null;
            foreach (var check in Clauses(constraint, "CHECK"))
            {
                checks.Add(new PortableCheck(named, check));
            }
        }

        var foreignKeys = (await session.QueryAsync("SELECT id, seq, \"table\" AS tbl, \"from\" AS fc, \"to\" AS tc, on_update, on_delete FROM pragma_foreign_key_list(@t) ORDER BY id, seq;", p, ct).ConfigureAwait(false))
            .GroupBy(r => Convert.ToInt32(r["id"], CultureInfo.InvariantCulture))
            .Select(g => new PortableForeignKey(null, g.Select(r => (string)r["fc"]!).ToList(), new PortableName(Main, (string)g.First()["tbl"]!),
                g.Select(r => r["tc"] as string ?? string.Empty).ToList(), Action(g.First()["on_delete"]), Action(g.First()["on_update"])))
            .ToList();
        foreach (var fk in foreignKeys.Where(f => f.ReferencedColumns.Any(c => c.Length == 0)).ToList())
        {
            // REFERENCES parent without columns: the parent's primary key.
            var parentPk = (await session.QueryAsync("SELECT name FROM pragma_table_info(@t) WHERE pk > 0 ORDER BY pk;", new Dictionary<string, object?> { ["t"] = fk.Referenced.Name }, ct).ConfigureAwait(false))
                .Select(r => (string)r["name"]!).ToList();
            foreignKeys[foreignKeys.IndexOf(fk)] = fk with { ReferencedColumns = parentPk };
        }

        var uniques = new List<PortableKey>();
        var indexes = new List<PortableIndex>();
        var sqlByIndex = (await session.QueryAsync("SELECT name, sql FROM main.sqlite_master WHERE type = 'index' AND tbl_name = @t AND sql IS NOT NULL;", p, ct).ConfigureAwait(false))
            .ToDictionary(r => (string)r["name"]!, r => (string)r["sql"]!, StringComparer.OrdinalIgnoreCase);
        foreach (var ix in await session.QueryAsync("SELECT name, \"unique\" AS uq, origin, partial FROM pragma_index_list(@t);", p, ct).ConfigureAwait(false))
        {
            var name = (string)ix["name"]!;
            var origin = (string)ix["origin"]!;
            var keys = (await session.QueryAsync("SELECT cid, name, \"desc\" AS d FROM pragma_index_xinfo(@i) WHERE key = 1 ORDER BY seqno;", new Dictionary<string, object?> { ["i"] = name }, ct).ConfigureAwait(false)).ToList();
            if (origin == "pk")
            {
                continue;
            }

            if (origin == "u")
            {
                uniques.Add(new PortableKey(null, keys.Select(k => (string)k["name"]!).ToList()));
                continue;
            }

            var sql = sqlByIndex.GetValueOrDefault(name) ?? string.Empty;
            var expressions = ColumnList(sql);
            var columns2 = keys.Select((k, i) => Convert.ToInt32(k["cid"], CultureInfo.InvariantCulture) == -2
                ? new PortableIndexColumn(i < expressions.Count ? StripDirection(expressions[i]) : "NULL", true, Convert.ToInt32(k["d"], CultureInfo.InvariantCulture) == 1)
                : new PortableIndexColumn((string)k["name"]!, false, Convert.ToInt32(k["d"], CultureInfo.InvariantCulture) == 1)).ToList();
            indexes.Add(new PortableIndex(name, Convert.ToInt32(ix["uq"], CultureInfo.InvariantCulture) == 1, columns2, [], Convert.ToInt32(ix["partial"], CultureInfo.InvariantCulture) == 1 ? After(sql, "WHERE") : null, null));
        }

        return new PortableTable(new PortableName(Main, table.Name), columns, pk.Count == 0 ? null : new PortableKey(null, pk), uniques, checks, foreignKeys, indexes);
    }

    private static string Action(object? value) => ((value as string) ?? "NO ACTION").ToUpperInvariant() switch
    {
        "CASCADE" => "CASCADE",
        "SET NULL" => "SET NULL",
        "SET DEFAULT" => "SET DEFAULT",
        "RESTRICT" => "RESTRICT",
        _ => "NO ACTION",
    };

    /// <summary>The parenthesized text after each occurrence of a keyword (CHECK(...)), at the top level.</summary>
    internal static List<string> Clauses(string text, string keyword)
    {
        var result = new List<string>();
        var tokens = SqliteLexer.Tokenize(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!SqliteLexer.IsWord(text, tokens[i], keyword))
            {
                continue;
            }

            var open = tokens.FindIndex(i + 1, t => !t.IsTrivia);
            if (open >= 0 && tokens[open].Kind == TokenKind.Punctuation && text[tokens[open].Start] == '(' && Close(text, tokens, open) is var close and >= 0)
            {
                result.Add(text[tokens[open].End..tokens[close].Start].Trim());
                i = close;
            }
        }

        return result;
    }

    /// <summary>The parenthesized expression after the keyword (AS (...)), or the text after it to the end (WHERE ...).</summary>
    private static string? After(string text, string keyword)
    {
        var tokens = SqliteLexer.Tokenize(text);
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokenKind.Punctuation)
            {
                depth += text[t.Start] == '(' ? 1 : text[t.Start] == ')' ? -1 : 0;
            }

            if (depth == 0 && SqliteLexer.IsWord(text, t, keyword))
            {
                var next = tokens.FindIndex(i + 1, x => !x.IsTrivia);
                if (keyword == "AS" && next >= 0 && text[tokens[next].Start] == '(' && Close(text, tokens, next) is var close and >= 0)
                {
                    return text[tokens[next].End..tokens[close].Start].Trim();
                }

                return next < 0 ? null : text[tokens[next].Start..].Trim().TrimEnd(';').Trim();
            }
        }

        return null;
    }

    private static string? Word(string text, string keyword)
    {
        var tokens = SqliteLexer.Tokenize(text).Where(t => !t.IsTrivia).ToList();
        var i = tokens.FindIndex(t => SqliteLexer.IsWord(text, t, keyword));
        return i >= 0 && i + 1 < tokens.Count ? SqliteLexer.Identifier(text, tokens[i + 1]) : null;
    }

    private static int Close(string text, List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Punctuation)
            {
                continue;
            }

            var c = text[tokens[i].Start];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The items of CREATE INDEX ... ON t (a, lower(b) DESC).</summary>
    private static List<string> ColumnList(string sql)
    {
        var tokens = SqliteLexer.Tokenize(sql);
        var on = tokens.FindIndex(t => SqliteLexer.IsWord(sql, t, "ON"));
        var open = on < 0 ? -1 : tokens.FindIndex(on, t => t.Kind == TokenKind.Punctuation && sql[t.Start] == '(');
        if (open < 0 || Close(sql, tokens, open) is not (var close and >= 0))
        {
            return [];
        }

        var items = new List<string>();
        var depth = 0;
        var start = tokens[open].End;
        for (var i = open + 1; i <= close; i++)
        {
            var t = tokens[i];
            if (t.Kind != TokenKind.Punctuation)
            {
                continue;
            }

            var c = sql[t.Start];
            depth += c == '(' ? 1 : c == ')' && i != close ? -1 : 0;
            if ((c == ',' && depth == 0) || i == close)
            {
                items.Add(sql[start..t.Start].Trim());
                start = t.End;
            }
        }

        return items;
    }

    private static string StripDirection(string item)
    {
        var trimmed = item.Trim();
        foreach (var suffix in new[] { " DESC", " ASC" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^suffix.Length].Trim();
            }
        }

        return trimmed;
    }
}
