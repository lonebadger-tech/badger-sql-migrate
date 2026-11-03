using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer.Scripting;

/// <summary>
/// Scripts the differences between two SQL Server databases as T-SQL: "deploy" turns the first into the second,
/// "revert" the second back into the first. Everything is read from the catalog; nothing outside SqlClient is used.
/// What cannot be scripted safely (table rebuilds, identity changes, turning system versioning on) is reported
/// as a warning instead of guessed.
/// </summary>
public sealed partial class SqlServerScripter : ISchemaScripter
{
    public async Task<SchemaScript> ScriptAsync(IDatabaseSession from, IDatabaseSession to, SchemaScriptOptions options, CancellationToken cancellationToken)
    {
        var excluded = Excluder(options);
        var source = await SqlServerModel.ReadAsync(from, options.StateSchema, excluded, cancellationToken).ConfigureAwait(false);
        var target = await SqlServerModel.ReadAsync(to, options.StateSchema, excluded, cancellationToken).ConfigureAwait(false);
        var owned = OwnedModules(options.RepeatableScripts);

        var deploy = new Generator(source, target, owned).Run();
        var revert = new Generator(target, source, owned).Run();
        return new SchemaScript(deploy.Script, revert.Script, deploy.Summary, deploy.Warnings, deploy.DataLoss);
    }

    /// <summary>Names of views, procedures, functions and triggers that repeatable scripts define (they are maintained there, not in changes).</summary>
    public static HashSet<string> OwnedModules(IEnumerable<string> scripts)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            foreach (Match m in ModuleDefinitionRegex().Matches(script))
            {
                var parts = NamePartRegex().Matches(m.Groups["name"].Value).Select(p => p.Groups["b"].Success ? p.Groups["b"].Value.Replace("]]", "]", StringComparison.Ordinal) : p.Groups["w"].Value).ToList();
                owned.Add(parts.Count >= 2 ? new ObjectName(parts[^2], parts[^1]).Key : new ObjectName("dbo", parts[0]).Key);
            }
        }

        return owned;
    }

    private static Func<ObjectName, bool> Excluder(SchemaScriptOptions options)
    {
        var patterns = options.Exclude.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        return name => patterns.Any(p => p.IsMatch($"{name.Schema}.{name.Name}"));
    }

    [GeneratedRegex(@"\bCREATE\s+(?:OR\s+ALTER\s+)?(?:VIEW|PROC|PROCEDURE|FUNCTION|TRIGGER)\s+(?<name>(?:(?:\[(?:[^\]]|\]\])+\]|[\w@#$]+)\s*\.\s*)?(?:\[(?:[^\]]|\]\])+\]|[\w@#$]+))", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleDefinitionRegex();

    [GeneratedRegex(@"\[(?<b>(?:[^\]]|\]\])+)\]|(?<w>[\w@#$]+)")]
    private static partial Regex NamePartRegex();

    private sealed record Step(int Phase, string Sql);

    private sealed record Result(string Script, IReadOnlyList<string> Summary, IReadOnlyList<string> Warnings, bool DataLoss);

    /// <summary>One direction: scripts what turns <c>a</c> into <c>b</c>.</summary>
    private sealed partial class Generator(SqlServerModel a, SqlServerModel b, HashSet<string> owned)
    {
        // Phases: drops first (dependents before what they depend on), then creates in the opposite order.
        private const int DropForeignKey = 10, DropModule = 20, DropSynonym = 22, DropCheck = 25, DropDefault = 27, DropIndex = 30, DropKey = 35,
            RenameColumn = 38, DropColumn = 40, DropTable = 45, DropType = 50, DropSequence = 52, DropScheme = 55, DropFunction = 56,
            CreateSchema = 60, CreateType = 62, CreateSequence = 63, CreatePartitionFunction = 64, CreateScheme = 65, AlterPartitions = 66,
            CreateTable = 70, AddColumn = 74, AlterColumn = 75, AddDefault = 78, AddKey = 80, AddCheck = 82, CreateIndex = 84, AddForeignKey = 86,
            AlterTemporal = 88, CreateModule = 90, CreateSynonym = 95, DropSchema = 99;

        private readonly List<Step> steps = [];
        private readonly List<string> summary = [];
        private readonly List<string> warnings = [];
        private readonly HashSet<string> rebuildForeignKeys = new(StringComparer.OrdinalIgnoreCase);
        private bool dataLoss;

        public Result Run()
        {
            Tables();
            Modules();
            Types();
            Sequences();
            Synonyms();
            Partitions();
            Schemas();
            var ordered = steps.Select((s, i) => (s, i)).OrderBy(x => x.s.Phase).ThenBy(x => x.i).Select(x => x.s.Sql.Trim()).ToList();
            return new Result(ordered.Count == 0 ? string.Empty : string.Join("\nGO\n\n", ordered) + "\nGO\n", summary, warnings, dataLoss);
        }

        private void Add(int phase, string sql) => steps.Add(new Step(phase, sql));

        private void Loss(string what)
        {
            dataLoss = true;
            warnings.Add($"DATA LOSS: {what}.");
        }

        // ------------------------------------------------------------------ schemas

        private void Schemas()
        {
            foreach (var name in b.Schemas.Keys.Where(n => !a.Schemas.ContainsKey(n)))
            {
                Add(CreateSchema, $"CREATE SCHEMA {Sql.Quote(name)};");
                summary.Add($"+ schema {name}");
            }

            foreach (var name in a.Schemas.Keys.Where(n => !b.Schemas.ContainsKey(n)))
            {
                Add(DropSchema, $"DROP SCHEMA {Sql.Quote(name)};");
                summary.Add($"- schema {name}");
            }
        }

        // ------------------------------------------------------------------ tables

        private void Tables()
        {
            // Foreign keys are dropped and re-added whenever a key they reference changes.
            foreach (var (key, table) in b.Tables.Where(t => t.Value.HistoryOf is null))
            {
                if (!a.Tables.TryGetValue(key, out var old))
                {
                    CreateTableStatement(table);
                    summary.Add($"+ table {table.Name}");
                    continue;
                }

                if (old.HistoryOf is null)
                {
                    AlterTable(old, table);
                }
            }

            foreach (var (key, table) in a.Tables.Where(t => t.Value.HistoryOf is null && !b.Tables.ContainsKey(t.Key)))
            {
                foreach (var fk in table.ForeignKeys)
                {
                    Add(DropForeignKey, DropForeignKeySql(table.Name, fk));
                }

                var sql = new StringBuilder();
                if (table.Temporal?.History is { } history)
                {
                    sql.Append($"ALTER TABLE {table.Name.Quoted} SET (SYSTEM_VERSIONING = OFF);\n");
                    sql.Append($"DROP TABLE {table.Name.Quoted};\n");
                    sql.Append($"DROP TABLE {history.Quoted};");
                }
                else
                {
                    sql.Append($"DROP TABLE {table.Name.Quoted};");
                }

                Add(DropTable, sql.ToString());
                summary.Add($"- table {table.Name}");
                Loss($"drops table {table.Name} and its rows");
            }

            // Foreign keys elsewhere that reference a rebuilt key.
            foreach (var table in a.Tables.Values.Where(t => b.Tables.ContainsKey(t.Name.Key)))
            {
                var after = b.Tables[table.Name.Key];
                foreach (var fk in table.ForeignKeys.Where(f => rebuildForeignKeys.Contains(f.Referenced.Key)))
                {
                    if (after.ForeignKeys.FirstOrDefault(f => f.Match.Equals(fk.Match, StringComparison.OrdinalIgnoreCase)) is { } same && same.Shape == fk.Shape)
                    {
                        Add(DropForeignKey, DropForeignKeySql(table.Name, fk));
                        Add(AddForeignKey, AddForeignKeySql(after.Name, same));
                    }
                }
            }
        }

        private void CreateTableStatement(Table table)
        {
            var lines = table.Columns.Select(ColumnDefinition).ToList();
            if (table.Temporal is { } t)
            {
                lines.Add($"PERIOD FOR SYSTEM_TIME ({Sql.Quote(t.Start)}, {Sql.Quote(t.End)})");
            }

            if (table.PrimaryKey is { } pk)
            {
                lines.Add(KeyClause(pk));
            }

            lines.AddRange(table.Uniques.Select(KeyClause));
            lines.AddRange(table.Checks.Select(c => (c.Name is null ? string.Empty : $"CONSTRAINT {Sql.Quote(c.Name)} ") + $"CHECK {c.Definition}"));

            var sql = new StringBuilder($"CREATE TABLE {table.Name.Quoted} (\n    {string.Join(",\n    ", lines)})");
            if (table.DataSpace is { } space && (space.PartitionColumn is not null || !space.Name.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)))
            {
                sql.Append($"\nON {space.Script}");
            }

            var with = new List<string>();
            if (table.Temporal is { } temporal)
            {
                var parts = new List<string>();
                if (temporal.History is { } history && !temporal.HistoryNamedBySystem)
                {
                    parts.Add($"HISTORY_TABLE = {history.Quoted}");
                }

                if (temporal.Retention is { } retention)
                {
                    parts.Add($"HISTORY_RETENTION_PERIOD = {retention}");
                }

                with.Add(parts.Count == 0 ? "SYSTEM_VERSIONING = ON" : $"SYSTEM_VERSIONING = ON ({string.Join(", ", parts)})");
            }

            if (table.Compression is { } compression)
            {
                with.Add($"DATA_COMPRESSION = {compression}");
            }

            if (with.Count > 0)
            {
                sql.Append($"\nWITH ({string.Join(", ", with)})");
            }

            Add(CreateTable, sql.Append(';').ToString());
            foreach (var index in table.Indexes)
            {
                Add(CreateIndex, CreateIndexSql(table.Name, index));
            }

            foreach (var fk in table.ForeignKeys)
            {
                Add(AddForeignKey, AddForeignKeySql(table.Name, fk));
            }

            foreach (var item in table.Unsupported)
            {
                warnings.Add($"{table.Name}: {item} is not scripted; add it by hand.");
            }
        }

        private void AlterTable(Table old, Table now)
        {
            var name = now.Name;
            var oldColumns = old.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var newColumns = now.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var removed = old.Columns.Where(c => !newColumns.ContainsKey(c.Name)).ToList();
            var added = now.Columns.Where(c => !oldColumns.ContainsKey(c.Name)).ToList();

            // A column removed and one added with exactly the same definition is most likely a rename.
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in removed.ToList())
            {
                var candidates = added.Where(c => c.Shape == gone.Shape).ToList();
                if (candidates.Count == 1 && removed.Count(r => r.Shape == gone.Shape) == 1)
                {
                    var target = candidates[0];
                    renames[gone.Name] = target.Name;
                    removed.Remove(gone);
                    added.Remove(target);
                    Add(RenameColumn, $"EXEC sp_rename N'{Escape(name.Quoted)}.{Escape(Sql.Quote(gone.Name))}', N'{Escape(target.Name)}', 'COLUMN';");
                    summary.Add($"~ column {name}.{gone.Name} renamed to {target.Name}");
                    warnings.Add($"{name}: column {gone.Name} was replaced by {target.Name} with the same definition; scripted as a rename (sp_rename). If it really is a new column, replace the rename with DROP/ADD.");
                    if (!Equals(gone.Default, target.Default))
                    {
                        if (gone.Default is not null)
                        {
                            Add(DropDefault, DropDefaultSql(name, gone));
                        }

                        if (target.Default is not null)
                        {
                            Add(AddDefault, AddDefaultSql(name, target));
                        }
                    }
                }
            }

            // Columns whose type changes: everything built on them is dropped and re-created around the ALTER.
            var retyped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in now.Columns.Where(c => oldColumns.ContainsKey(c.Name)))
            {
                var before = oldColumns[column.Name];
                if (before.Shape == column.Shape)
                {
                    continue;
                }

                if (!SameExceptAlterable(before, column))
                {
                    if (before.Computed is not null && column.Computed is not null)
                    {
                        retyped.Add(column.Name);
                        Add(DropColumn, $"ALTER TABLE {name.Quoted} DROP COLUMN {Sql.Quote(column.Name)};");
                        Add(AddColumn, $"ALTER TABLE {name.Quoted} ADD {ColumnDefinition(column)};");
                        summary.Add($"~ computed column {name}.{column.Name}");
                        continue;
                    }

                    warnings.Add($"{name}.{column.Name}: changing identity, computed, sparse, ROWGUIDCOL, period or encryption settings needs the table rebuilt; not scripted. Write this part by hand.");
                    continue;
                }

                retyped.Add(column.Name);
                var nullability = column.Nullable ? "NULL" : "NOT NULL";
                var collation = column.Collation is null ? string.Empty : $" COLLATE {column.Collation}";
                if (before.Type != column.Type || before.Nullable != column.Nullable || before.Collation != column.Collation)
                {
                    Add(AlterColumn, $"ALTER TABLE {name.Quoted} ALTER COLUMN {Sql.Quote(column.Name)} {column.Type}{collation} {nullability};");
                    summary.Add($"~ column {name}.{column.Name}: {before.Type}{(before.Nullable ? " NULL" : " NOT NULL")} -> {column.Type} {nullability}");
                    if (before.Type != column.Type)
                    {
                        Loss($"{name}.{column.Name} changes from {before.Type} to {column.Type}; values that do not fit fail the deploy or are converted");
                    }

                    if (before.Nullable && !column.Nullable)
                    {
                        warnings.Add($"{name}.{column.Name} becomes NOT NULL: the deploy fails if any row holds NULL. Update those rows first in the same script.");
                    }
                }

                if (before.Mask != column.Mask)
                {
                    Add(AlterColumn, column.Mask is null
                        ? $"ALTER TABLE {name.Quoted} ALTER COLUMN {Sql.Quote(column.Name)} DROP MASKED;"
                        : $"ALTER TABLE {name.Quoted} ALTER COLUMN {Sql.Quote(column.Name)} ADD MASKED WITH (FUNCTION = {Sql.Literal(column.Mask)});");
                    summary.Add($"~ mask {name}.{column.Name}");
                }
            }

            foreach (var column in removed)
            {
                if (column.Default is not null)
                {
                    Add(DropDefault, DropDefaultSql(name, column));
                }

                Add(DropColumn, $"ALTER TABLE {name.Quoted} DROP COLUMN {Sql.Quote(column.Name)};");
                summary.Add($"- column {name}.{column.Name}");
                if (column.Computed is null)
                {
                    Loss($"drops column {name}.{column.Name} and its data");
                }
            }

            foreach (var column in added)
            {
                Add(AddColumn, $"ALTER TABLE {name.Quoted} ADD {ColumnDefinition(column)};");
                summary.Add($"+ column {name}.{column.Name} {column.Type}");
                if (!column.Nullable && column.Default is null && column.Identity is null && column.Computed is null && column.GeneratedAlways is null)
                {
                    warnings.Add($"{name}.{column.Name} is added NOT NULL without a default: the deploy fails if the table has rows. Add a default, or add it NULL, fill it, then make it NOT NULL.");
                }
            }

            if (now.Columns.Where(c => oldColumns.ContainsKey(c.Name) || renames.ContainsValue(c.Name)).Select(c => renames.FirstOrDefault(r => r.Value == c.Name).Key ?? c.Name)
                .SequenceEqual(old.Columns.Where(c => newColumns.ContainsKey(c.Name) || renames.ContainsKey(c.Name)).Select(c => c.Name), StringComparer.OrdinalIgnoreCase) is false)
            {
                warnings.Add($"{name}: the column order differs; bsm does not reorder columns (it needs a table rebuild). Queries should name columns.");
            }

            bool Touches(IEnumerable<string> columns) => columns.Any(c => retyped.Contains(c) || renames.ContainsKey(c) || renames.ContainsValue(c));

            // Defaults on surviving columns.
            foreach (var column in now.Columns.Where(c => oldColumns.ContainsKey(c.Name)))
            {
                var before = oldColumns[column.Name];
                if (Equals(before.Default, column.Default) && !retyped.Contains(column.Name))
                {
                    continue;
                }

                if (before.Default is not null && before.Computed is null)
                {
                    Add(DropDefault, DropDefaultSql(name, before));
                }

                if (column.Default is not null && column.Computed is null)
                {
                    Add(AddDefault, AddDefaultSql(name, column));
                }

                if (!Equals(before.Default?.Definition, column.Default?.Definition))
                {
                    summary.Add($"~ default {name}.{column.Name}: {before.Default?.Definition ?? "none"} -> {column.Default?.Definition ?? "none"}");
                }
            }

            // Primary key and unique constraints.
            if (KeyChanged(old.PrimaryKey, now.PrimaryKey) || (old.PrimaryKey is { } opk && Touches(opk.Columns.Select(c => c.Name))))
            {
                rebuildForeignKeys.Add(name.Key);
                if (old.PrimaryKey is { } before)
                {
                    Add(DropKey, DropKeySql(name, before));
                }

                if (now.PrimaryKey is { } after)
                {
                    Add(AddKey, $"ALTER TABLE {name.Quoted} ADD {KeyClause(after)};");
                }

                if (KeyChanged(old.PrimaryKey, now.PrimaryKey))
                {
                    summary.Add($"~ primary key {name}");
                }
            }

            Pair(old.Uniques, now.Uniques, u => u.Name ?? "<uq>" + u.Shape, (u1, u2) => !KeyChanged(u1, u2) && !Touches(u1.Columns.Select(c => c.Name)),
                u => { Add(DropKey, DropKeySql(name, u)); rebuildForeignKeys.Add(name.Key); },
                u => Add(AddKey, $"ALTER TABLE {name.Quoted} ADD {KeyClause(u)};"),
                u => $"unique constraint {name}.{u.Name ?? string.Join(",", u.Columns.Select(c => c.Name))}");

            Pair(old.Indexes, now.Indexes, i => i.Name, (i1, i2) => i1.Shape == i2.Shape && !Touches(i1.Columns.Select(c => c.Name).Concat(i1.Included)),
                i => Add(DropIndex, $"DROP INDEX {Sql.Quote(i.Name)} ON {name.Quoted};"),
                i => Add(CreateIndex, CreateIndexSql(name, i)),
                i => $"index {name}.{i.Name}");

            Pair(old.Checks, now.Checks, c => c.Match, (c1, c2) => c1.Definition == c2.Definition && c1.Disabled == c2.Disabled && !retyped.Any(r => c1.Definition.Contains($"[{r}]", StringComparison.OrdinalIgnoreCase)),
                c => Add(DropCheck, DropCheckSql(name, c)),
                c => Add(AddCheck, AddCheckSql(name, c)),
                c => $"check {name}.{c.Name ?? c.Definition}");

            Pair(old.ForeignKeys, now.ForeignKeys, f => f.Match, (f1, f2) => f1.Shape == f2.Shape && !Touches(f1.Columns),
                f => Add(DropForeignKey, DropForeignKeySql(name, f)),
                f => Add(AddForeignKey, AddForeignKeySql(name, f)),
                f => $"foreign key {name}.{f.Name ?? string.Join(",", f.Columns)}");

            // Retyped columns referenced by other tables' foreign keys.
            if (retyped.Count > 0 || renames.Count > 0)
            {
                rebuildForeignKeys.Add(name.Key);
            }

            if (!Equals(old.DataSpace?.Name, now.DataSpace?.Name) || old.DataSpace?.PartitionColumn != now.DataSpace?.PartitionColumn || old.Compression != now.Compression)
            {
                warnings.Add($"{name}: moving the table to another filegroup or partition scheme, or changing its compression, rebuilds it; not scripted. Rebuild the clustered index by hand (ALTER INDEX ... REBUILD / CREATE ... WITH (DROP_EXISTING = ON)).");
            }

            if ((old.Temporal is null) != (now.Temporal is null) || old.Temporal is { } ot && now.Temporal is { } nt && (ot.Start != nt.Start || ot.End != nt.End || !Equals(ot.History?.Key, nt.History?.Key) && !(ot.HistoryNamedBySystem && nt.HistoryNamedBySystem)))
            {
                warnings.Add($"{name}: turning system versioning on or off, or changing its period or history table, is not scripted. Write it by hand (see 'Temporal tables and partitions' in the docs).");
            }
            else if (old.Temporal is { } before && now.Temporal is { } after && before.Retention != after.Retention)
            {
                Add(AlterTemporal, $"ALTER TABLE {name.Quoted} SET (SYSTEM_VERSIONING = ON ({(after.History is { } h && !after.HistoryNamedBySystem ? $"HISTORY_TABLE = {h.Quoted}, " : string.Empty)}HISTORY_RETENTION_PERIOD = {after.Retention ?? "INFINITE"}));");
                summary.Add($"~ history retention {name}: {before.Retention ?? "infinite"} -> {after.Retention ?? "infinite"}");
            }

            foreach (var item in now.Unsupported.Except(old.Unsupported))
            {
                warnings.Add($"{name}: {item} is not scripted; add it by hand.");
            }
        }

        /// <summary>Only type, nullability, collation and mask can change with ALTER COLUMN.</summary>
        private static bool SameExceptAlterable(Column before, Column after) =>
            before.Identity == after.Identity && before.Computed == after.Computed && before.Persisted == after.Persisted && before.Sparse == after.Sparse && before.RowGuid == after.RowGuid
            && before.GeneratedAlways == after.GeneratedAlways && before.Hidden == after.Hidden && Equals(before.Encryption, after.Encryption);

        private static bool KeyChanged(KeyConstraint? before, KeyConstraint? after) =>
            (before is null) != (after is null) || before is not null && (before.Shape != after!.Shape || before.Name != after.Name);

        private void Pair<T>(IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> key, Func<T, T, bool> same, Action<T> drop, Action<T> create, Func<T, string> describe)
        {
            var old = before.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var now = after.GroupBy(key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var (k, item) in old)
            {
                if (!now.TryGetValue(k, out var other))
                {
                    drop(item);
                    summary.Add($"- {describe(item)}");
                }
                else if (!same(item, other))
                {
                    drop(item);
                    create(other);
                    if (!same(other, item) || !EqualityComparer<T>.Default.Equals(item, other))
                    {
                        summary.Add($"~ {describe(other)}");
                    }
                }
            }

            foreach (var (k, item) in now.Where(kv => !old.ContainsKey(kv.Key)))
            {
                create(item);
                summary.Add($"+ {describe(item)}");
            }
        }

        private static string ColumnDefinition(Column c)
        {
            if (c.Computed is not null)
            {
                return $"{Sql.Quote(c.Name)} AS {c.Computed}{(c.Persisted ? " PERSISTED" : string.Empty)}{(c.Persisted && !c.Nullable ? " NOT NULL" : string.Empty)}";
            }

            var sb = new StringBuilder($"{Sql.Quote(c.Name)} {c.Type}");
            if (c.Collation is not null)
            {
                sb.Append(" COLLATE ").Append(c.Collation);
            }

            if (c.Identity is { } identity)
            {
                sb.Append($" IDENTITY({identity.Seed}, {identity.Increment})");
            }

            if (c.Sparse)
            {
                sb.Append(" SPARSE");
            }

            if (c.RowGuid)
            {
                sb.Append(" ROWGUIDCOL");
            }

            if (c.GeneratedAlways is { } generated)
            {
                sb.Append(" GENERATED ALWAYS AS ").Append(generated).Append(c.Hidden ? " HIDDEN" : string.Empty);
            }

            if (c.Mask is { } mask)
            {
                sb.Append($" MASKED WITH (FUNCTION = {Sql.Literal(mask)})");
            }

            if (c.Encryption is { } e)
            {
                sb.Append($" ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = {Sql.Quote(e.Key)}, ENCRYPTION_TYPE = {e.Type}, ALGORITHM = {Sql.Literal(e.Algorithm)[1..]})");
            }

            sb.Append(c.Nullable ? " NULL" : " NOT NULL");
            if (c.Default is { } d)
            {
                sb.Append(d.Name is null ? string.Empty : $" CONSTRAINT {Sql.Quote(d.Name)}").Append(" DEFAULT ").Append(d.Definition);
            }

            return sb.ToString();
        }

        private static string KeyClause(KeyConstraint k)
        {
            var sb = new StringBuilder(k.Name is null ? string.Empty : $"CONSTRAINT {Sql.Quote(k.Name)} ");
            sb.Append(k.Primary ? "PRIMARY KEY" : "UNIQUE").Append(k.Clustered ? " CLUSTERED" : " NONCLUSTERED");
            sb.Append(" (").Append(string.Join(", ", k.Columns.Select(c => Sql.Quote(c.Name) + (c.Descending ? " DESC" : string.Empty)))).Append(')');
            if (k.DataSpace is { PartitionColumn: not null } space)
            {
                sb.Append(" ON ").Append(space.Script);
            }

            return sb.ToString();
        }

        private static string CreateIndexSql(ObjectName table, IndexDefinition i)
        {
            var sb = new StringBuilder("CREATE ");
            if (i.Kind.EndsWith("COLUMNSTORE", StringComparison.Ordinal))
            {
                sb.Append($"{i.Kind} INDEX {Sql.Quote(i.Name)} ON {table.Quoted}");
                if (i.Kind.StartsWith("NONCLUSTERED", StringComparison.Ordinal))
                {
                    sb.Append(" (").Append(string.Join(", ", i.Included.Select(Sql.Quote))).Append(')');
                }
            }
            else
            {
                sb.Append(i.Unique ? "UNIQUE " : string.Empty).Append(i.Kind).Append($" INDEX {Sql.Quote(i.Name)} ON {table.Quoted} (");
                sb.Append(string.Join(", ", i.Columns.Select(c => Sql.Quote(c.Name) + (c.Descending ? " DESC" : string.Empty)))).Append(')');
                if (i.Included.Count > 0)
                {
                    sb.Append(" INCLUDE (").Append(string.Join(", ", i.Included.Select(Sql.Quote))).Append(')');
                }

                if (i.Filter is not null)
                {
                    sb.Append(" WHERE ").Append(i.Filter);
                }
            }

            if (i.Compression is not null)
            {
                sb.Append($" WITH (DATA_COMPRESSION = {i.Compression})");
            }

            if (i.DataSpace is { } space && (space.PartitionColumn is not null || !space.Name.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append(" ON ").Append(space.Script);
            }

            return sb.Append(';').ToString();
        }

        private static string AddForeignKeySql(ObjectName table, ForeignKey f)
        {
            var sb = new StringBuilder($"ALTER TABLE {table.Quoted} WITH {(f.NotTrusted ? "NOCHECK" : "CHECK")} ADD ");
            sb.Append(f.Name is null ? string.Empty : $"CONSTRAINT {Sql.Quote(f.Name)} ");
            sb.Append($"FOREIGN KEY ({string.Join(", ", f.Columns.Select(Sql.Quote))}) REFERENCES {f.Referenced.Quoted} ({string.Join(", ", f.ReferencedColumns.Select(Sql.Quote))})");
            if (f.OnDelete != "NO ACTION")
            {
                sb.Append(" ON DELETE ").Append(f.OnDelete);
            }

            if (f.OnUpdate != "NO ACTION")
            {
                sb.Append(" ON UPDATE ").Append(f.OnUpdate);
            }

            sb.Append(';');
            if (f.Disabled && f.Name is not null)
            {
                sb.Append($"\nALTER TABLE {table.Quoted} NOCHECK CONSTRAINT {Sql.Quote(f.Name)};");
            }

            return sb.ToString();
        }

        private static string AddCheckSql(ObjectName table, CheckConstraint c)
        {
            var sql = $"ALTER TABLE {table.Quoted} WITH CHECK ADD {(c.Name is null ? string.Empty : $"CONSTRAINT {Sql.Quote(c.Name)} ")}CHECK {c.Definition};";
            return c.Disabled && c.Name is not null ? sql + $"\nALTER TABLE {table.Quoted} NOCHECK CONSTRAINT {Sql.Quote(c.Name)};" : sql;
        }

        private static string AddDefaultSql(ObjectName table, Column c) =>
            $"ALTER TABLE {table.Quoted} ADD {(c.Default!.Name is null ? string.Empty : $"CONSTRAINT {Sql.Quote(c.Default.Name)} ")}DEFAULT {c.Default.Definition} FOR {Sql.Quote(c.Name)};";

        // Constraints SQL Server named itself have a different name on every server, so drops look the name up.
        private static string DropByLookup(ObjectName table, string lookup) =>
            $"DECLARE @name sysname = ({lookup});\nDECLARE @drop nvarchar(max) = N'ALTER TABLE {Escape(table.Quoted)} DROP CONSTRAINT ' + QUOTENAME(@name);\nIF @name IS NOT NULL EXEC (@drop);";

        private static string DropDefaultSql(ObjectName table, Column c) => c.Default!.Name is { } n
            ? $"ALTER TABLE {table.Quoted} DROP CONSTRAINT {Sql.Quote(n)};"
            : DropByLookup(table, $"SELECT d.name FROM sys.default_constraints d JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id WHERE d.parent_object_id = OBJECT_ID(N'{Escape(table.Quoted)}') AND c.name = {Sql.Literal(c.Name)}");

        private static string DropKeySql(ObjectName table, KeyConstraint k) => k.Name is { } n
            ? $"ALTER TABLE {table.Quoted} DROP CONSTRAINT {Sql.Quote(n)};"
            : DropByLookup(table, k.Primary
                ? $"SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'{Escape(table.Quoted)}') AND type = 'PK'"
                : $"SELECT k.name FROM sys.key_constraints k WHERE k.parent_object_id = OBJECT_ID(N'{Escape(table.Quoted)}') AND k.type = 'UQ' AND (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id AND ic.key_ordinal > 0) = {Sql.Literal(string.Join(",", k.Columns.Select(c => c.Name)))}");

        private static string DropCheckSql(ObjectName table, CheckConstraint c) => c.Name is { } n
            ? $"ALTER TABLE {table.Quoted} DROP CONSTRAINT {Sql.Quote(n)};"
            : DropByLookup(table, $"SELECT name FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(N'{Escape(table.Quoted)}') AND definition = {Sql.Literal(c.Definition)}");

        private static string DropForeignKeySql(ObjectName table, ForeignKey f) => f.Name is { } n
            ? $"ALTER TABLE {table.Quoted} DROP CONSTRAINT {Sql.Quote(n)};"
            : DropByLookup(table, $"SELECT fk.name FROM sys.foreign_keys fk WHERE fk.parent_object_id = OBJECT_ID(N'{Escape(table.Quoted)}') AND fk.referenced_object_id = OBJECT_ID(N'{Escape(f.Referenced.Quoted)}') AND (SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id) FROM sys.foreign_key_columns fkc JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id WHERE fkc.constraint_object_id = fk.object_id) = {Sql.Literal(string.Join(",", f.Columns))}");

        private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);

        // ------------------------------------------------------------------ modules

        private void Modules()
        {
            foreach (var key in a.Modules.Keys.Union(b.Modules.Keys).Where(owned.Contains))
            {
                var before = a.Modules.GetValueOrDefault(key);
                var after = b.Modules.GetValueOrDefault(key);
                if (before?.Definition != after?.Definition)
                {
                    warnings.Add($"{(after ?? before)!.Name} is defined by a repeatable script, so it is not part of the change; edit the repeatable instead.");
                }
            }

            var changed = new List<Module>();
            foreach (var (key, module) in b.Modules.Where(m => !owned.Contains(m.Key)))
            {
                if (!a.Modules.TryGetValue(key, out var old))
                {
                    changed.Add(module);
                    summary.Add($"+ {Kind(module.Type)} {module.Name}");
                }
                else if (old.Definition != module.Definition)
                {
                    if (old.Type != module.Type)
                    {
                        Add(DropModule, DropModuleSql(old));
                    }

                    changed.Add(module);
                    summary.Add($"~ {Kind(module.Type)} {module.Name}");
                }
            }

            foreach (var module in Ordered(a.Modules.Values.Where(m => !owned.Contains(m.Name.Key) && !b.Modules.ContainsKey(m.Name.Key)), a).Reverse())
            {
                Add(DropModule, DropModuleSql(module));
                summary.Add($"- {Kind(module.Type)} {module.Name}");
            }

            foreach (var module in Ordered(changed, b))
            {
                Add(CreateModule, CreateOrAlter(module.Definition));
            }

            if (changed.Any(m => SchemaBindingRegex().IsMatch(m.Definition)) || a.Modules.Values.Any(m => SchemaBindingRegex().IsMatch(m.Definition) && m.References.Any(r => b.Tables.ContainsKey(r) && a.Tables.ContainsKey(r) && a.Tables[r] != b.Tables[r])))
            {
                warnings.Add("schema-bound views or functions reference changed tables; SQL Server refuses some table changes while they exist. If the deploy fails, drop and re-create those modules around the change.");
            }
        }

        /// <summary>Dependencies first, then by name.</summary>
        private static IEnumerable<Module> Ordered(IEnumerable<Module> modules, SqlServerModel model)
        {
            var set = modules.ToDictionary(m => m.Name.Key, StringComparer.OrdinalIgnoreCase);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<Module>();

            void Visit(Module m, HashSet<string> path)
            {
                if (done.Contains(m.Name.Key) || !path.Add(m.Name.Key))
                {
                    return;
                }

                foreach (var dependency in m.References.Where(set.ContainsKey).Order(StringComparer.OrdinalIgnoreCase))
                {
                    Visit(set[dependency], path);
                }

                done.Add(m.Name.Key);
                result.Add(m);
            }

            foreach (var module in set.Values.OrderBy(m => m.Type == "TR" ? 1 : 0).ThenBy(m => m.Name.Key, StringComparer.OrdinalIgnoreCase))
            {
                Visit(module, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            _ = model;
            return result;
        }

        private static string Kind(string type) => type switch
        {
            "V" => "view",
            "P" => "procedure",
            "TR" => "trigger",
            _ => "function",
        };

        private static string DropModuleSql(Module m) => $"DROP {Kind(m.Type).ToUpperInvariant()} IF EXISTS {m.Name.Quoted};";

        /// <summary>The stored definition with its first CREATE turned into CREATE OR ALTER (comments before it are kept).</summary>
        internal static string CreateOrAlter(string definition)
        {
            var m = LeadingCreateRegex().Match(definition);
            return m.Success && !m.Groups["orAlter"].Success
                ? definition[..m.Groups["create"].Index] + m.Groups["create"].Value + " OR ALTER" + definition[(m.Groups["create"].Index + m.Groups["create"].Length)..]
                : definition;
        }

        [GeneratedRegex(@"\A(?:\s+|--[^\n]*\n|/\*.*?\*/)*(?<create>CREATE)(?<orAlter>\s+OR\s+ALTER)?\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex LeadingCreateRegex();

        [GeneratedRegex(@"\bSCHEMABINDING\b", RegexOptions.IgnoreCase)]
        private static partial Regex SchemaBindingRegex();

        // ------------------------------------------------------------------ types, sequences, synonyms

        private void Types()
        {
            foreach (var (key, type) in b.AliasTypes)
            {
                if (!a.AliasTypes.TryGetValue(key, out var old))
                {
                    Add(CreateType, $"CREATE TYPE {type.Name.Quoted} FROM {type.BaseType}{(type.Nullable ? " NULL" : " NOT NULL")};");
                    summary.Add($"+ type {type.Name}");
                }
                else if (old != type)
                {
                    warnings.Add($"type {type.Name} changed ({old.BaseType} -> {type.BaseType}); columns using it must be changed first. Not scripted.");
                }
            }

            foreach (var type in a.AliasTypes.Values.Where(t => !b.AliasTypes.ContainsKey(t.Name.Key)))
            {
                Add(DropType, $"DROP TYPE {type.Name.Quoted};");
                summary.Add($"- type {type.Name}");
            }

            foreach (var (key, type) in b.TableTypes)
            {
                var exists = a.TableTypes.TryGetValue(key, out var old);
                if (exists && old!.Shape == type.Shape)
                {
                    continue;
                }

                if (exists)
                {
                    Add(DropType, $"DROP TYPE {type.Name.Quoted};");
                    warnings.Add($"table type {type.Name} is dropped and re-created; procedures that use it as a parameter must be dropped first and re-created after.");
                }

                var lines = type.Columns.Select(ColumnDefinition).ToList();
                if (type.PrimaryKey is { } pk)
                {
                    lines.Add(KeyClause(pk with { Name = null }));
                }

                lines.AddRange(type.Checks.Select(c => $"CHECK {c.Definition}"));
                Add(CreateType, $"CREATE TYPE {type.Name.Quoted} AS TABLE (\n    {string.Join(",\n    ", lines)});");
                summary.Add($"{(exists ? "~" : "+")} table type {type.Name}");
            }

            foreach (var type in a.TableTypes.Values.Where(t => !b.TableTypes.ContainsKey(t.Name.Key)))
            {
                Add(DropType, $"DROP TYPE {type.Name.Quoted};");
                summary.Add($"- table type {type.Name}");
            }
        }

        private void Sequences()
        {
            foreach (var (key, s) in b.Sequences)
            {
                if (!a.Sequences.TryGetValue(key, out var old))
                {
                    Add(CreateSequence, $"CREATE SEQUENCE {s.Name.Quoted} AS {s.Type} START WITH {s.Start} INCREMENT BY {s.Increment} MINVALUE {s.Minimum} MAXVALUE {s.Maximum} {(s.Cycle ? "CYCLE" : "NO CYCLE")} {(s.Cache is null ? "NO CACHE" : s.Cache.Length == 0 ? "CACHE" : $"CACHE {s.Cache}")};");
                    summary.Add($"+ sequence {s.Name}");
                }
                else if (old with { Start = s.Start } != s)
                {
                    if (old.Type != s.Type)
                    {
                        warnings.Add($"sequence {s.Name} changed type; not scripted.");
                        continue;
                    }

                    Add(CreateSequence, $"ALTER SEQUENCE {s.Name.Quoted} INCREMENT BY {s.Increment} MINVALUE {s.Minimum} MAXVALUE {s.Maximum} {(s.Cycle ? "CYCLE" : "NO CYCLE")} {(s.Cache is null ? "NO CACHE" : s.Cache.Length == 0 ? "CACHE" : $"CACHE {s.Cache}")};");
                    summary.Add($"~ sequence {s.Name}");
                }
            }

            foreach (var s in a.Sequences.Values.Where(s => !b.Sequences.ContainsKey(s.Name.Key)))
            {
                Add(DropSequence, $"DROP SEQUENCE {s.Name.Quoted};");
                summary.Add($"- sequence {s.Name}");
            }
        }

        private void Synonyms()
        {
            foreach (var (key, s) in b.Synonyms)
            {
                var exists = a.Synonyms.TryGetValue(key, out var old);
                if (exists && old!.Target == s.Target)
                {
                    continue;
                }

                if (exists)
                {
                    Add(DropSynonym, $"DROP SYNONYM {s.Name.Quoted};");
                }

                Add(CreateSynonym, $"CREATE SYNONYM {s.Name.Quoted} FOR {s.Target};");
                summary.Add($"{(exists ? "~" : "+")} synonym {s.Name}");
            }

            foreach (var s in a.Synonyms.Values.Where(s => !b.Synonyms.ContainsKey(s.Name.Key)))
            {
                Add(DropSynonym, $"DROP SYNONYM {s.Name.Quoted};");
                summary.Add($"- synonym {s.Name}");
            }
        }

        // ------------------------------------------------------------------ partitions

        private void Partitions()
        {
            string Value(PartitionFunction f, string v) => f.QuoteBoundaries ? Sql.Literal(v) : v;

            foreach (var (name, f) in b.PartitionFunctions)
            {
                if (!a.PartitionFunctions.TryGetValue(name, out var old))
                {
                    Add(CreatePartitionFunction, $"CREATE PARTITION FUNCTION {Sql.Quote(f.Name)} ({f.Type}) AS RANGE {(f.RangeRight ? "RIGHT" : "LEFT")} FOR VALUES ({string.Join(", ", f.Boundaries.Select(v => Value(f, v)))});");
                    summary.Add($"+ partition function {f.Name}");
                    continue;
                }

                if (old.Type != f.Type || old.RangeRight != f.RangeRight)
                {
                    warnings.Add($"partition function {f.Name} changed type or direction; it must be re-created with everything on it. Not scripted.");
                    continue;
                }

                var schemes = b.PartitionSchemes.Values.Where(s => s.Function.Equals(f.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var gone in old.Boundaries.Except(f.Boundaries))
                {
                    Add(AlterPartitions, $"ALTER PARTITION FUNCTION {Sql.Quote(f.Name)}() MERGE RANGE ({Value(f, gone)});");
                    summary.Add($"- partition boundary {f.Name} {gone}");
                }

                foreach (var added in f.Boundaries.Except(old.Boundaries))
                {
                    var next = string.Join("\n", schemes.Select(s => $"ALTER PARTITION SCHEME {Sql.Quote(s.Name)} NEXT USED {Sql.Quote(s.Filegroups.LastOrDefault() ?? "PRIMARY")};"));
                    Add(AlterPartitions, $"{next}\nALTER PARTITION FUNCTION {Sql.Quote(f.Name)}() SPLIT RANGE ({Value(f, added)});".Trim());
                    summary.Add($"+ partition boundary {f.Name} {added}");
                }

                if (!old.Boundaries.SequenceEqual(f.Boundaries))
                {
                    // A scheme with one more filegroup than partitions has a "next used" filegroup waiting; keep it.
                    foreach (var scheme in schemes.Where(s => s.Filegroups.Count > f.Boundaries.Count + 1))
                    {
                        Add(AlterPartitions, $"ALTER PARTITION SCHEME {Sql.Quote(scheme.Name)} NEXT USED {Sql.Quote(scheme.Filegroups[^1])};");
                    }

                    warnings.Add($"partition function {f.Name}: splitting or merging partitions that hold rows moves data and locks the tables on it.");
                }
            }

            foreach (var f in a.PartitionFunctions.Values.Where(f => !b.PartitionFunctions.ContainsKey(f.Name)))
            {
                Add(DropFunction, $"DROP PARTITION FUNCTION {Sql.Quote(f.Name)};");
                summary.Add($"- partition function {f.Name}");
            }

            foreach (var (name, s) in b.PartitionSchemes)
            {
                if (!a.PartitionSchemes.TryGetValue(name, out var old))
                {
                    Add(CreateScheme, $"CREATE PARTITION SCHEME {Sql.Quote(s.Name)} AS PARTITION {Sql.Quote(s.Function)} TO ({string.Join(", ", s.Filegroups.Select(Sql.Quote))});");
                    summary.Add($"+ partition scheme {s.Name}");
                }
                else if (old.Function != s.Function || !old.Filegroups.Distinct().Order().SequenceEqual(s.Filegroups.Distinct().Order()))
                {
                    warnings.Add($"partition scheme {s.Name} changed function or filegroups; not scripted.");
                }
            }

            foreach (var s in a.PartitionSchemes.Values.Where(s => !b.PartitionSchemes.ContainsKey(s.Name)))
            {
                Add(DropScheme, $"DROP PARTITION SCHEME {Sql.Quote(s.Name)};");
                summary.Add($"- partition scheme {s.Name}");
            }
        }
    }
}
