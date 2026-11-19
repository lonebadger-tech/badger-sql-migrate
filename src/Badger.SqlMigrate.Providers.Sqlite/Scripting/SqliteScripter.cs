using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.Sqlite.Scripting;

/// <summary>
/// Scripts the differences between two SQLite databases: "deploy" turns the first into the second, "revert" the
/// second back into the first. SQLite's ALTER TABLE can only rename tables and columns, add a column and drop a
/// simple one, so every other table change is the rebuild SQLite documents: create the new table under a temporary
/// name, copy the rows, drop the old table, rename, and re-create its indexes and triggers. Views and triggers that
/// refer to a rebuilt table are dropped first and re-created after, because SQLite re-checks them on rename.
/// </summary>
public sealed partial class SqliteScripter : ISchemaScripter
{
    public async Task<SchemaScript> ScriptAsync(IDatabaseSession from, IDatabaseSession to, SchemaScriptOptions options, CancellationToken cancellationToken)
    {
        var patterns = options.Exclude.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        bool Excluded(string name) => patterns.Any(p => p.IsMatch(name));

        var source = await SqliteModel.ReadAsync(from, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var target = await SqliteModel.ReadAsync(to, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var owned = OwnedObjects(options.RepeatableScripts);

        var deploy = new Generator(source, target, owned).Run();
        var revert = new Generator(target, source, owned).Run();

        // Either direction rebuilding a table other tables refer to needs foreign keys off, which SQLite only allows
        // outside a transaction: then both scripts manage their own (the linter makes the change !notx).
        var foreignKeysOff = deploy.ForeignKeysOff || revert.ForeignKeysOff;
        var warnings = deploy.Warnings.ToList();
        if (foreignKeysOff)
        {
            warnings.Add("Rebuilds a table other tables refer to, so the scripts switch foreign keys off and run their own transaction (the change is !notx). Rows that no longer match a foreign key stop the script before COMMIT.");
        }

        return new SchemaScript(Render(deploy.Steps, foreignKeysOff), Render(revert.Steps, foreignKeysOff), deploy.Summary, warnings, deploy.DataLoss);
    }

    private static string Render(IReadOnlyList<string> steps, bool foreignKeysOff)
    {
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        var all = foreignKeysOff
            ?
            [
                "PRAGMA foreign_keys = OFF;",
                "BEGIN;",
                .. steps,
                "-- Fails (CHECK constraint failed: foreign_key_violation) when any row no longer matches its foreign key.\nCREATE TEMP TABLE bsm_foreign_key_check (violation TEXT CONSTRAINT foreign_key_violation CHECK (violation IS NULL));\nINSERT INTO bsm_foreign_key_check SELECT \"table\" FROM pragma_foreign_key_check;\nDROP TABLE temp.bsm_foreign_key_check;",
                "COMMIT;",
                "PRAGMA foreign_keys = ON;",
            ]
            : steps.ToList();
        return string.Join("\n\n", all.Select(s => s.Trim())) + "\n";
    }

    /// <summary>Names (lower case) of the views and triggers that repeatable scripts create.</summary>
    public static HashSet<string> OwnedObjects(IEnumerable<string> scripts)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            foreach (Match m in DefinitionRegex().Matches(script))
            {
                var name = m.Groups["name"].Value;
                var tokens = SqliteLexer.Tokenize(name).Where(t => t.Kind is TokenKind.Word or TokenKind.QuotedIdentifier).ToList();
                if (tokens.Count > 0)
                {
                    owned.Add(m.Groups["kind"].Value.ToLowerInvariant() + " " + SqliteLexer.Identifier(name, tokens[^1]));
                }
            }
        }

        return owned;
    }

    [GeneratedRegex("""\bCREATE\s+(?:(?:TEMP|TEMPORARY)\s+)?(?<kind>VIEW|TRIGGER)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>(?:"(?:[^"]|"")+"|\[[^\]]+\]|`(?:[^`]|``)+`|[A-Za-z_][\w$]*)(?:\s*\.\s*(?:"(?:[^"]|"")+"|\[[^\]]+\]|`(?:[^`]|``)+`|[A-Za-z_][\w$]*))?)""", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionRegex();

    private sealed record Result(IReadOnlyList<string> Steps, IReadOnlyList<string> Summary, IReadOnlyList<string> Warnings, bool DataLoss, bool ForeignKeysOff);

    /// <summary>One direction: scripts what turns <c>a</c> into <c>b</c>.</summary>
    private sealed class Generator(SqliteModel a, SqliteModel b, HashSet<string> owned)
    {
        private const string NewPrefix = "bsm_new_";

        private readonly List<string> summary = [];
        private readonly List<string> warnings = [];
        private readonly List<string> tableSteps = [];
        private readonly List<string> dropTables = [];

        /// <summary>Tables dropped and created again: their indexes and triggers go with them.</summary>
        private readonly HashSet<string> rebuilt = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tables removed, rebuilt, or with columns dropped or renamed: what refers to them is re-created.</summary>
        private readonly HashSet<string> reshaped = new(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> droppedTables = new(StringComparer.OrdinalIgnoreCase);

        private bool dataLoss;

        public Result Run()
        {
            Tables();
            var (drops, creates) = Objects();

            var script = new List<string>();
            script.AddRange(drops);
            script.AddRange(dropTables);
            script.AddRange(tableSteps);
            script.AddRange(creates);

            // With foreign keys on, dropping a table that other rows refer to counts violations SQLite does not
            // forget when the rebuilt table takes the name back, so the commit would fail.
            var referenced = a.Tables.Values.Where(t => !droppedTables.Contains(t.Name))
                .Any(t => t.References.Any(r => rebuilt.Contains(r.Table)));
            return new Result(script, summary, warnings, dataLoss, referenced);
        }

        // ------------------------------------------------------------------ tables

        private void Tables()
        {
            foreach (var old in a.Tables.Values.Where(t => !b.Tables.ContainsKey(t.Name)).OrderByDescending(t => t.Order))
            {
                droppedTables.Add(old.Name);
                reshaped.Add(old.Name);
                dropTables.Add($"DROP TABLE {Q(old.Name)};");
                summary.Add($"- table main.{old.Name}");
                Loss($"table main.{old.Name} is dropped");
            }

            foreach (var now in b.Tables.Values.OrderBy(t => t.Order))
            {
                if (!a.Tables.TryGetValue(now.Name, out var old))
                {
                    tableSteps.Add(Terminated(now.Sql));
                    summary.Add($"+ table main.{now.Name}");
                    continue;
                }

                if (old.SameShape(now))
                {
                    continue;
                }

                if (old.Virtual || now.Virtual)
                {
                    rebuilt.Add(now.Name);
                    reshaped.Add(now.Name);
                    tableSteps.Add($"DROP TABLE {Q(old.Name)};");
                    tableSteps.Add(Terminated(now.Sql));
                    summary.Add($"~ table main.{now.Name} (re-created)");
                    Loss($"virtual table main.{now.Name} is re-created empty");
                    continue;
                }

                if (!TryRename(old, now) && !TryAdd(old, now) && !TryDrop(old, now))
                {
                    Rebuild(old, now);
                }
            }
        }

        private bool TryRename(SqliteTable old, SqliteTable now)
        {
            if (old.Options != now.Options || !old.Constraints.SequenceEqual(now.Constraints) || old.Columns.Count != now.Columns.Count)
            {
                return false;
            }

            var renames = new List<(string From, string To)>();
            for (var i = 0; i < old.Columns.Count; i++)
            {
                var (o, n) = (old.Columns[i], now.Columns[i]);
                if (o.Rest != n.Rest)
                {
                    return false;
                }

                if (!string.Equals(o.Name, n.Name, StringComparison.Ordinal))
                {
                    renames.Add((o.Name, n.Name));
                }
            }

            if (renames.Count == 0)
            {
                return false;
            }

            reshaped.Add(now.Name);
            foreach (var (from, to) in renames)
            {
                tableSteps.Add($"ALTER TABLE {Q(now.Name)} RENAME COLUMN {Q(from)} TO {Q(to)};");
                summary.Add($"~ column main.{now.Name}.{from} renamed to {to}");
            }

            return true;
        }

        private bool TryAdd(SqliteTable old, SqliteTable now)
        {
            if (old.Options != now.Options || !old.Constraints.SequenceEqual(now.Constraints) || now.Columns.Count <= old.Columns.Count)
            {
                return false;
            }

            for (var i = 0; i < old.Columns.Count; i++)
            {
                if (old.Columns[i].Definition != now.Columns[i].Definition)
                {
                    return false;
                }
            }

            var added = now.Columns.Skip(old.Columns.Count).ToList();
            if (!added.All(Addable))
            {
                return false;
            }

            foreach (var column in added)
            {
                tableSteps.Add($"ALTER TABLE {Q(now.Name)} ADD COLUMN {column.Definition};");
                summary.Add($"+ column main.{now.Name}.{column.Name}");
            }

            return true;
        }

        private bool TryDrop(SqliteTable old, SqliteTable now)
        {
            if (old.Options != now.Options || !old.Constraints.SequenceEqual(now.Constraints) || now.Columns.Count >= old.Columns.Count)
            {
                return false;
            }

            var dropped = new List<SqliteColumn>();
            var j = 0;
            foreach (var column in old.Columns)
            {
                if (j < now.Columns.Count && now.Columns[j].Definition == column.Definition)
                {
                    j++;
                }
                else
                {
                    dropped.Add(column);
                }
            }

            if (j != now.Columns.Count || !dropped.All(Droppable))
            {
                return false;
            }

            reshaped.Add(now.Name);
            foreach (var column in dropped)
            {
                tableSteps.Add($"ALTER TABLE {Q(now.Name)} DROP COLUMN {Q(column.Name)};");
                summary.Add($"- column main.{now.Name}.{column.Name}");
                Loss($"column main.{now.Name}.{column.Name} is dropped");
            }

            return true;
        }

        private void Rebuild(SqliteTable old, SqliteTable now)
        {
            rebuilt.Add(now.Name);
            reshaped.Add(now.Name);
            var temp = NewPrefix + now.Name;

            // Copy columns by name; a column at the same position with a new name and the same definition is a rename.
            var oldByName = old.Columns.Where(c => !c.Generated).ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var newNames = now.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var copies = new List<(string Target, string Source)>();
            for (var i = 0; i < now.Columns.Count; i++)
            {
                var column = now.Columns[i];
                if (column.Generated)
                {
                    continue;
                }

                if (oldByName.ContainsKey(column.Name))
                {
                    copies.Add((column.Name, oldByName[column.Name].Name));
                    if (old.Columns.First(c => c.Name.Equals(column.Name, StringComparison.OrdinalIgnoreCase)).Rest != column.Rest)
                    {
                        summary.Add($"~ column main.{now.Name}.{column.Name}");
                    }
                }
                else if (i < old.Columns.Count && old.Columns[i] is { Generated: false } candidate && !newNames.Contains(candidate.Name) && candidate.Rest == column.Rest)
                {
                    copies.Add((column.Name, candidate.Name));
                    summary.Add($"~ column main.{now.Name}.{candidate.Name} renamed to {column.Name}");
                }
                else
                {
                    summary.Add($"+ column main.{now.Name}.{column.Name}");
                    if (Words(column.Rest).Contains("NOT") && Words(column.Rest).Contains("NULL") && !Words(column.Rest).Contains("DEFAULT"))
                    {
                        warnings.Add($"Column main.{now.Name}.{column.Name} is NOT NULL without a default: copying the rows fails if main.{now.Name} has any. Add a default or fill it in the script.");
                    }
                }
            }

            foreach (var column in old.Columns.Where(c => !c.Generated && !copies.Any(x => x.Source.Equals(c.Name, StringComparison.OrdinalIgnoreCase))))
            {
                summary.Add($"- column main.{now.Name}.{column.Name}");
                Loss($"column main.{now.Name}.{column.Name} is dropped");
            }

            if (!old.Options.Equals(now.Options, StringComparison.Ordinal) || !old.Constraints.SequenceEqual(now.Constraints))
            {
                summary.Add($"~ table main.{now.Name} constraints");
            }

            summary.Add($"~ table main.{now.Name} (rebuilt)");
            tableSteps.Add($"CREATE TABLE {Q(temp)} {now.Body.Trim()};");
            if (copies.Count > 0)
            {
                tableSteps.Add($"INSERT INTO {Q(temp)} ({string.Join(", ", copies.Select(c => Q(c.Target)))})\nSELECT {string.Join(", ", copies.Select(c => Q(c.Source)))} FROM {Q(old.Name)};");
            }

            tableSteps.Add($"DROP TABLE {Q(old.Name)};");
            tableSteps.Add($"ALTER TABLE {Q(temp)} RENAME TO {Q(now.Name)};");
        }

        private static bool Addable(SqliteColumn column)
        {
            if (column.Generated)
            {
                return false;
            }

            var tokens = SqliteLexer.Tokenize(column.Rest).Where(t => !t.IsTrivia).ToList();
            var words = Words(column.Rest);
            if (words.Contains("PRIMARY") || words.Contains("UNIQUE") || words.Contains("GENERATED") || words.Contains("AS"))
            {
                return false;
            }

            var d = tokens.FindIndex(t => SqliteLexer.IsWord(column.Rest, t, "DEFAULT"));
            string? defaultValue = null;
            if (d >= 0)
            {
                if (d + 1 >= tokens.Count)
                {
                    return false;
                }

                var value = tokens[d + 1];
                if (value.Kind == TokenKind.Punctuation && column.Rest[value.Start] is '(')
                {
                    return false;
                }

                if (value.Kind == TokenKind.Punctuation && column.Rest[value.Start] is '-' or '+' && d + 2 < tokens.Count)
                {
                    value = tokens[d + 2];
                }

                defaultValue = column.Rest.Substring(value.Start, value.Length).ToUpperInvariant();
                if (defaultValue is "CURRENT_TIME" or "CURRENT_DATE" or "CURRENT_TIMESTAMP")
                {
                    return false;
                }
            }

            var notNull = Enumerable.Range(0, Math.Max(0, tokens.Count - 1)).Any(i => SqliteLexer.IsWord(column.Rest, tokens[i], "NOT") && SqliteLexer.IsWord(column.Rest, tokens[i + 1], "NULL"));
            if (notNull && defaultValue is null or "NULL")
            {
                return false;
            }

            return !words.Contains("REFERENCES") || defaultValue is null or "NULL";
        }

        private static bool Droppable(SqliteColumn column)
        {
            var words = Words(column.Rest);
            return !column.Generated && !words.Contains("PRIMARY") && !words.Contains("UNIQUE") && !words.Contains("REFERENCES") && !words.Contains("GENERATED");
        }

        private static HashSet<string> Words(string text) =>
            SqliteLexer.Tokenize(text).Where(t => t.Kind == TokenKind.Word).Select(t => text.Substring(t.Start, t.Length).ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

        // ------------------------------------------------------------------ indexes, views, triggers

        private (List<string> Drops, List<string> Creates) Objects()
        {
            bool Changed(SqliteObject o) => !b.Objects.TryGetValue(SqliteModel.Key(o.Type, o.Name), out var now) || now.Normalized != o.Normalized;
            bool IsOwned(SqliteObject o) => o.Type is "view" or "trigger" && owned.Contains(o.Type + " " + o.Name);

            // Views and triggers that refer, directly or through other views, to a table or view that goes away for a while.
            var gone = new HashSet<string>(reshaped.Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);
            foreach (var o in a.Objects.Values.Where(o => o.Type == "view" && Changed(o) && !IsOwned(o)))
            {
                gone.Add(o.Name.ToLowerInvariant());
            }

            var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool grew;
            do
            {
                grew = false;
                foreach (var o in a.Objects.Values.Where(o => o.Type is "view" or "trigger" && !forced.Contains(SqliteModel.Key(o.Type, o.Name))))
                {
                    if (o.Words.Overlaps(gone.Where(g => g != o.Name.ToLowerInvariant() || o.Type == "trigger")))
                    {
                        forced.Add(SqliteModel.Key(o.Type, o.Name));
                        grew |= o.Type == "view" && gone.Add(o.Name.ToLowerInvariant());
                    }
                }
            }
            while (grew);

            var drops = new List<(int Rank, long Order, string Sql)>();
            foreach (var o in a.Objects.Values)
            {
                var key = SqliteModel.Key(o.Type, o.Name);
                var withTable = o.Type is "index" or "trigger" && droppedTables.Contains(o.Table);
                var onRebuilt = o.Type is "index" or "trigger" && rebuilt.Contains(o.Table);
                if (withTable || !(forced.Contains(key) || onRebuilt || (Changed(o) && !IsOwned(o))))
                {
                    if (withTable && !b.Objects.ContainsKey(key))
                    {
                        summary.Add($"- {o.Type} main.{o.Name}");
                    }

                    continue;
                }

                drops.Add((o.Type switch { "trigger" => 0, "view" => 1, _ => 2 }, o.Order, $"DROP {o.Type.ToUpperInvariant()} {Q(o.Name)};"));
                if (!b.Objects.ContainsKey(key))
                {
                    summary.Add($"- {o.Type} main.{o.Name}");
                }
            }

            var creates = new List<(int Rank, long Order, string Sql)>();
            foreach (var o in b.Objects.Values)
            {
                var key = SqliteModel.Key(o.Type, o.Name);
                a.Objects.TryGetValue(key, out var old);
                var changed = old is null || old.Normalized != o.Normalized;
                var onRebuilt = o.Type is "index" or "trigger" && rebuilt.Contains(o.Table);
                if (!(forced.Contains(key) || onRebuilt || (changed && !IsOwned(o))))
                {
                    continue;
                }

                creates.Add((o.Type switch { "index" => 0, "view" => 1, _ => 2 }, o.Order, Terminated(o.Sql)));
                if (changed && !IsOwned(o))
                {
                    summary.Add($"{(old is null ? "+" : "~")} {o.Type} main.{o.Name}");
                }
            }

            return (
                drops.OrderBy(d => d.Rank).ThenByDescending(d => d.Order).Select(d => d.Sql).ToList(),
                creates.OrderBy(c => c.Rank).ThenBy(c => c.Order).Select(c => c.Sql).ToList());
        }

        private void Loss(string what)
        {
            dataLoss = true;
            warnings.Add($"DATA LOSS: {what}.");
        }

        private static string Terminated(string sql) => sql.TrimEnd().EndsWith(';') ? sql.Trim() : sql.Trim() + ";";
    }

    /// <summary>An identifier as written in generated SQL: bare when that is unambiguous, otherwise double-quoted.</summary>
    internal static string Q(string name) =>
        SqliteLexer.IsSimple(name) && !Keywords.Contains(name) ? name : SqliteLexer.Quote(name);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "AS", "ASC", "ATTACH", "AUTOINCREMENT", "BEFORE", "BEGIN",
        "BETWEEN", "BY", "CASCADE", "CASE", "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONFLICT", "CONSTRAINT", "CREATE", "CROSS", "CURRENT",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE", "DEFAULT", "DEFERRABLE", "DEFERRED", "DELETE", "DESC", "DETACH", "DISTINCT",
        "DO", "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT", "EXCLUDE", "EXCLUSIVE", "EXISTS", "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING",
        "FOR", "FOREIGN", "FROM", "FULL", "GENERATED", "GLOB", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE", "IMMEDIATE", "IN", "INDEX", "INDEXED",
        "INITIALLY", "INNER", "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LAST", "LEFT", "LIKE", "LIMIT", "MATCH",
        "MATERIALIZED", "NATURAL", "NO", "NOT", "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON", "OR", "ORDER", "OTHERS", "OUTER", "OVER",
        "PARTITION", "PLAN", "PRAGMA", "PRECEDING", "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE", "REFERENCES", "REGEXP", "REINDEX", "RELEASE",
        "RENAME", "REPLACE", "RESTRICT", "RETURNING", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT", "SELECT", "SET", "TABLE", "TEMP", "TEMPORARY",
        "THEN", "TIES", "TO", "TRANSACTION", "TRIGGER", "UNBOUNDED", "UNION", "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES", "VIEW", "VIRTUAL",
        "WHEN", "WHERE", "WINDOW", "WITH", "WITHOUT",
    };
}
