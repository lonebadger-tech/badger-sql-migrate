using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql.Scripting;

/// <summary>
/// Scripts the differences between two MySQL databases: "deploy" turns the first into the second, "revert" the
/// second back into the first. Tables are compared line by line as SHOW CREATE TABLE prints them, and changed with
/// ALTER TABLE: columns added (in position), dropped, modified, renamed (same definition under a new name at the same
/// position) and moved; indexes, checks and table options. Foreign keys are dropped first and added after every table
/// exists, so new tables and cycles work. Views, routines, triggers and events are dropped and created again, with a
/// DELIMITER around bodies of several statements. Objects that repeatable scripts create are left to them.
/// </summary>
public sealed partial class MySqlScripter : ISchemaScripter
{
    public async Task<SchemaScript> ScriptAsync(IDatabaseSession from, IDatabaseSession to, SchemaScriptOptions options, CancellationToken cancellationToken)
    {
        var patterns = options.Exclude.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        bool Excluded(string name) => patterns.Any(p => p.IsMatch(name));

        var source = await MySqlModel.ReadAsync(from, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var target = await MySqlModel.ReadAsync(to, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var owned = OwnedObjects(options.RepeatableScripts);

        var deploy = new Generator(source, target, owned).Run();
        var revert = new Generator(target, source, owned).Run();
        var warnings = deploy.Warnings.ToList();
        if (deploy.Steps.Count > 0)
        {
            warnings.Add("MySQL commits each DDL statement as it runs: if a statement fails, the ones before it stay applied. Review the script, and keep it to what this change needs.");
        }

        return new SchemaScript(Render(deploy.Steps), Render(revert.Steps), deploy.Summary, warnings, deploy.DataLoss);
    }

    private static string Render(IReadOnlyList<string> steps) =>
        steps.Count == 0 ? string.Empty : string.Join("\n\n", steps.Select(s => s.Trim())) + "\n";

    /// <summary>Keys ("view name", "procedure name" ...) of the objects that repeatable scripts create.</summary>
    public static HashSet<string> OwnedObjects(IEnumerable<string> scripts)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            foreach (Match m in DefinitionRegex().Matches(script))
            {
                var name = m.Groups["name"].Value;
                name = name.Contains('.', StringComparison.Ordinal) ? name[(name.LastIndexOf('.') + 1)..] : name;
                owned.Add($"{m.Groups["kind"].Value.ToLowerInvariant()} {name.Trim('`').Replace("``", "`", StringComparison.Ordinal)}");
            }
        }

        return owned;
    }

    [GeneratedRegex(@"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:ALGORITHM\s*=\s*\w+\s+)?(?:DEFINER\s*=\s*\S+\s+)?(?:SQL\s+SECURITY\s+\w+\s+)?(?<kind>VIEW|PROCEDURE|FUNCTION|TRIGGER|EVENT)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>(?:`(?:[^`]|``)+`|[A-Za-z_][\w$]*)(?:\s*\.\s*(?:`(?:[^`]|``)+`|[A-Za-z_][\w$]*))?)", RegexOptions.IgnoreCase)]
    private static partial Regex DefinitionRegex();

    private sealed record Result(IReadOnlyList<string> Steps, IReadOnlyList<string> Summary, IReadOnlyList<string> Warnings, bool DataLoss);

    /// <summary>One direction: scripts what turns <c>a</c> into <c>b</c>.</summary>
    private sealed partial class Generator(MySqlModel a, MySqlModel b, HashSet<string> owned)
    {
        private readonly List<string> summary = [];
        private readonly List<string> warnings = [];
        private readonly List<string> dropForeignKeys = [];
        private readonly List<string> dropObjects = [];
        private readonly List<string> dropTables = [];
        private readonly List<string> alterTables = [];
        private readonly List<string> createTables = [];
        private readonly List<string> addForeignKeys = [];
        private readonly List<string> createObjects = [];
        private bool dataLoss;

        public Result Run()
        {
            Tables();
            Objects();
            return new Result([.. dropForeignKeys, .. dropObjects, .. dropTables, .. alterTables, .. createTables, .. addForeignKeys, .. createObjects], summary, warnings, dataLoss);
        }

        private static string Q(string name) => MySqlModel.Q(name);

        private void Loss(string what)
        {
            dataLoss = true;
            warnings.Add($"Data loss: {what}.");
        }

        // ------------------------------------------------------------------ tables

        private void Tables()
        {
            foreach (var old in a.Tables.Values.Where(t => !b.Tables.ContainsKey(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                // Foreign keys from this table go with it; foreign keys to it are dropped with their own tables' changes.
                dropTables.Add($"DROP TABLE {Q(old.Name)};");
                summary.Add($"- table {old.Name}");
                Loss($"table {old.Name} is dropped");
            }

            foreach (var now in b.Tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                if (!a.Tables.TryGetValue(now.Name, out var old))
                {
                    createTables.Add(now.CreateWithoutForeignKeys());
                    summary.Add($"+ table {now.Name}");
                    foreach (var fk in now.Constraints.Where(c => c.ForeignKey))
                    {
                        addForeignKeys.Add($"ALTER TABLE {Q(now.Name)} ADD {fk.Definition};");
                    }

                    continue;
                }

                AlterTable(old, now);
            }

            // Foreign keys to a dropped table must go before it: drop them wherever they are, unless their table goes too.
            foreach (var table in a.Tables.Values.Where(t => b.Tables.ContainsKey(t.Name)))
            {
                foreach (var fk in table.Constraints.Where(c => c.ForeignKey && c.References is { } r && !b.Tables.ContainsKey(r)))
                {
                    if (!b.Tables[table.Name].Constraints.Any(c => c.Name == fk.Name && c.Definition == fk.Definition))
                    {
                        continue;
                    }

                    warnings.Add($"Table {table.Name} keeps foreign key {fk.Name} to {fk.References}, which is dropped: the script drops the foreign key first.");
                    dropForeignKeys.Add($"ALTER TABLE {Q(table.Name)} DROP FOREIGN KEY {Q(fk.Name)};");
                }
            }
        }

        private void AlterTable(MySqlTable old, MySqlTable now)
        {
            var t = Q(now.Name);

            // Foreign keys: changed or removed ones go first, new or changed ones are added last.
            foreach (var fk in old.Constraints.Where(c => c.ForeignKey && !now.Constraints.Any(n => n.Name == c.Name && n.Definition == c.Definition)))
            {
                dropForeignKeys.Add($"ALTER TABLE {t} DROP FOREIGN KEY {Q(fk.Name)};");
                if (!now.Constraints.Any(n => n.Name == fk.Name))
                {
                    summary.Add($"- foreign key {now.Name}.{fk.Name}");
                }
            }

            foreach (var fk in now.Constraints.Where(c => c.ForeignKey && !old.Constraints.Any(o => o.Name == c.Name && o.Definition == c.Definition)))
            {
                addForeignKeys.Add($"ALTER TABLE {t} ADD {fk.Definition};");
                summary.Add(old.Constraints.Any(o => o.Name == fk.Name) ? $"~ foreign key {now.Name}.{fk.Name}" : $"+ foreign key {now.Name}.{fk.Name}");
            }

            // Checks and indexes that change or go away are dropped before the columns change.
            foreach (var check in old.Constraints.Where(c => !c.ForeignKey && !now.Constraints.Any(n => n.Name == c.Name && n.Definition == c.Definition)))
            {
                alterTables.Add($"ALTER TABLE {t} DROP CHECK {Q(check.Name)};");
                if (!now.Constraints.Any(n => n.Name == check.Name))
                {
                    summary.Add($"- check {now.Name}.{check.Name}");
                }
            }

            foreach (var index in old.Indexes.Where(i => !now.Indexes.Any(n => n.Name == i.Name && n.Definition == i.Definition)))
            {
                alterTables.Add(index.Primary ? $"ALTER TABLE {t} DROP PRIMARY KEY;" : $"ALTER TABLE {t} DROP INDEX {Q(index.Name)};");
                if (!now.Indexes.Any(n => n.Name == index.Name))
                {
                    summary.Add($"- index {now.Name}.{index.Name}");
                }
            }

            TableOptions(old, now);
            Columns(old, now);

            foreach (var index in now.Indexes.Where(i => !old.Indexes.Any(o => o.Name == i.Name && o.Definition == i.Definition)))
            {
                alterTables.Add($"ALTER TABLE {t} ADD {index.Definition};");
                summary.Add(old.Indexes.Any(o => o.Name == index.Name) ? $"~ index {now.Name}.{index.Name}" : $"+ index {now.Name}.{index.Name}");
            }

            foreach (var check in now.Constraints.Where(c => !c.ForeignKey && !old.Constraints.Any(o => o.Name == c.Name && o.Definition == c.Definition)))
            {
                alterTables.Add($"ALTER TABLE {t} ADD {check.Definition};");
                summary.Add(old.Constraints.Any(o => o.Name == check.Name) ? $"~ check {now.Name}.{check.Name}" : $"+ check {now.Name}.{check.Name}");
            }
        }

        /// <summary>
        /// Engine, comment, row format ... with ALTER TABLE; a new character set or collation with CONVERT TO, which also
        /// converts the columns that use the table's default (a plain ALTER TABLE ... COLLATE= would leave them as they were).
        /// Runs before the column changes.
        /// </summary>
        private void TableOptions(MySqlTable old, MySqlTable now)
        {
            if (old.Options == now.Options)
            {
                return;
            }

            var t = Q(now.Name);
            if (PartitionRegex().IsMatch(old.Options) || PartitionRegex().IsMatch(now.Options))
            {
                warnings.Add($"Table {now.Name}: partitioning differs; it is not scripted. Write ALTER TABLE ... PARTITION BY (or REMOVE PARTITIONING) by hand.");
            }

            var (oldCharset, oldCollation, oldRest) = SplitOptions(old.Options);
            var (charset, collation, rest) = SplitOptions(now.Options);
            if (charset != oldCharset || collation != oldCollation)
            {
                alterTables.Add($"ALTER TABLE {t} CONVERT TO CHARACTER SET {charset}{(collation.Length > 0 ? " COLLATE " + collation : string.Empty)};");
                summary.Add($"~ table {now.Name} character set {oldCharset} {oldCollation} -> {charset} {collation}".TrimEnd());
                warnings.Add($"Table {now.Name} is converted to {charset} {collation}: every text column is rewritten, and characters the new character set cannot hold are lost.".Replace("  ", " ", StringComparison.Ordinal));
            }

            if (rest != oldRest && rest.Length > 0)
            {
                alterTables.Add($"ALTER TABLE {t} {rest};");
                summary.Add($"~ table {now.Name} options");
            }
        }

        /// <summary>A table's options split into character set, collation and the rest (partitioning left out).</summary>
        private static (string Charset, string Collation, string Others) SplitOptions(string options)
        {
            var text = PartitionRegex().Replace(options, string.Empty);
            var charset = CharsetRegex().Match(text) is { Success: true } c ? c.Groups[1].Value : string.Empty;
            var collation = CollationRegex().Match(text) is { Success: true } k ? k.Groups[1].Value : string.Empty;
            var rest = CollationRegex().Replace(CharsetRegex().Replace(text, string.Empty), string.Empty);
            return (charset, collation, System.Text.RegularExpressions.Regex.Replace(rest, @"\s+", " ").Trim());
        }

        /// <summary>Adds, drops, renames, modifies and moves columns so the table ends up with <paramref name="now"/>'s columns, in order.</summary>
        private void Columns(MySqlTable old, MySqlTable now)
        {
            var t = Q(now.Name);
            var oldByName = old.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var newByName = now.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

            // A column that disappears while a new one with exactly the same definition appears is a rename, when each is
            // the other's only match (two new columns with the same definition stay an add, so no data goes to the wrong one).
            var removed = old.Columns.Where(c => !newByName.ContainsKey(c.Name)).ToList();
            var added = now.Columns.Where(c => !oldByName.ContainsKey(c.Name)).ToList();
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in removed)
            {
                var matches = added.Where(n => n.Rest == o.Rest).ToList();
                if (matches.Count == 1 && removed.Count(r => r.Rest == o.Rest) == 1)
                {
                    renamed[o.Name] = matches[0].Name;
                }
            }

            // The current order, kept up to date as the script changes it, to know where each column is.
            var order = old.Columns.Select(c => renamed.TryGetValue(c.Name, out var r) ? r : c.Name).ToList();
            foreach (var (from, to) in renamed)
            {
                alterTables.Add($"ALTER TABLE {t} RENAME COLUMN {Q(from)} TO {Q(to)};");
                summary.Add($"~ column {now.Name}.{from} renamed to {to}");
            }

            foreach (var column in old.Columns.Where(c => !newByName.ContainsKey(c.Name) && !renamed.ContainsKey(c.Name)))
            {
                alterTables.Add($"ALTER TABLE {t} DROP COLUMN {Q(column.Name)};");
                order.Remove(column.Name);
                summary.Add($"- column {now.Name}.{column.Name}");
                Loss($"column {now.Name}.{column.Name} is dropped");
            }

            for (var i = 0; i < now.Columns.Count; i++)
            {
                var column = now.Columns[i];
                var position = i == 0 ? "FIRST" : $"AFTER {Q(now.Columns[i - 1].Name)}";
                var oldName = renamed.FirstOrDefault(r => r.Value.Equals(column.Name, StringComparison.OrdinalIgnoreCase)).Key ?? column.Name;
                if (!oldByName.TryGetValue(oldName, out var before))
                {
                    alterTables.Add($"ALTER TABLE {t} ADD COLUMN {column.Definition} {position};");
                    order.Insert(i, column.Name);
                    summary.Add($"+ column {now.Name}.{column.Name}");
                    if (NotNullWithoutDefault(column.Definition))
                    {
                        warnings.Add($"Column {now.Name}.{column.Name} is NOT NULL without a default: existing rows get the type's implicit default (0, '' ...), or the statement fails in strict mode for some types. Add a default or fill it in the script.");
                    }

                    continue;
                }

                var moved = order.IndexOf(column.Name) != i;
                if (before.Rest != column.Rest || moved)
                {
                    alterTables.Add($"ALTER TABLE {t} MODIFY COLUMN {column.Definition}{(moved ? " " + position : string.Empty)};");
                    order.Remove(column.Name);
                    order.Insert(i, column.Name);
                    if (before.Rest != column.Rest)
                    {
                        summary.Add($"~ column {now.Name}.{column.Name}: {before.Rest} -> {column.Rest}");
                        if (TypeOf(before.Rest) != TypeOf(column.Rest))
                        {
                            Loss($"column {now.Name}.{column.Name} changes type from {TypeOf(before.Rest)} to {TypeOf(column.Rest)}; values that do not fit are truncated or rejected");
                        }
                    }
                    else
                    {
                        summary.Add($"~ column {now.Name}.{column.Name} moved");
                    }
                }
            }
        }

        private static bool NotNullWithoutDefault(string definition) =>
            definition.Contains(" NOT NULL", StringComparison.Ordinal) && !definition.Contains(" DEFAULT ", StringComparison.Ordinal)
            && !definition.Contains("AUTO_INCREMENT", StringComparison.Ordinal) && !definition.Contains(" GENERATED ALWAYS ", StringComparison.Ordinal);

        private static string TypeOf(string rest) => rest.Split(' ', 2)[0];

        // ------------------------------------------------------------------ views, routines, triggers, events

        private void Objects()
        {
            bool Owned(MySqlObject o) => owned.Contains(o.Key);

            foreach (var old in a.Objects.Values.Where(o => !Owned(o) && (!b.Objects.TryGetValue(o.Key, out var n) || n.Create != o.Create)).OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                // A trigger goes with its table when the table is dropped.
                if (old.Kind == "trigger" && old.Table is { } table && !b.Tables.ContainsKey(table))
                {
                    summary.Add($"- trigger {old.Name}");
                    continue;
                }

                dropObjects.Add($"DROP {old.Kind.ToUpperInvariant()} IF EXISTS {Q(old.Name)};");
                if (!b.Objects.ContainsKey(old.Key))
                {
                    summary.Add($"- {old.Kind} {old.Name}");
                }
            }

            var created = b.Objects.Values.Where(o => !Owned(o) && (!a.Objects.TryGetValue(o.Key, out var p) || p.Create != o.Create)).ToList();
            foreach (var now in DependencyOrder(created))
            {
                createObjects.Add(Statement(now.Create));
                summary.Add(a.Objects.ContainsKey(now.Key) ? $"~ {now.Kind} {now.Name}" : $"+ {now.Kind} {now.Name}");
            }
        }

        /// <summary>Views first (a view before the views that use it), then routines, triggers and events.</summary>
        private static IEnumerable<MySqlObject> DependencyOrder(List<MySqlObject> objects)
        {
            var rank = new Dictionary<string, int> { ["view"] = 0, ["function"] = 1, ["procedure"] = 2, ["trigger"] = 3, ["event"] = 4 };
            var views = objects.Where(o => o.Kind == "view").OrderBy(o => o.Name, StringComparer.Ordinal).ToList();
            var ordered = new List<MySqlObject>();
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (views.Count > 0)
            {
                var next = views.FirstOrDefault(v => !views.Any(other => other != v && v.Create.Contains(Q(other.Name), StringComparison.Ordinal))) ?? views[0];
                ordered.Add(next);
                placed.Add(next.Name);
                views.Remove(next);
            }

            return ordered.Concat(objects.Where(o => o.Kind != "view").OrderBy(o => rank[o.Kind]).ThenBy(o => o.Name, StringComparer.Ordinal));
        }

        /// <summary>A body of several statements needs another delimiter, as in the mysql client.</summary>
        private static string Statement(string create) =>
            create.Contains(';', StringComparison.Ordinal) ? $"DELIMITER //\n{create} //\nDELIMITER ;" : create + ";";

        [GeneratedRegex(@"/\*!\d+\s+PARTITION\b.*?\*/|\bPARTITION\s+BY\b.*", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
        private static partial Regex PartitionRegex();

        [GeneratedRegex(@"\bDEFAULT\s+CHARSET\s*=\s*(\w+)", RegexOptions.IgnoreCase)]
        private static partial Regex CharsetRegex();

        [GeneratedRegex(@"\bCOLLATE\s*=\s*(\w+)", RegexOptions.IgnoreCase)]
        private static partial Regex CollationRegex();
    }
}
