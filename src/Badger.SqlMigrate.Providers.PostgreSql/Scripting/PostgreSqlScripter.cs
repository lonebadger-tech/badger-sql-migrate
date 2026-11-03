using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql.Scripting;

/// <summary>
/// Scripts the differences between two PostgreSQL databases: "deploy" turns the first into the second, "revert" the
/// second back into the first. Definitions come from PostgreSQL itself (pg_get_functiondef, pg_get_viewdef,
/// pg_get_indexdef, pg_get_constraintdef, pg_get_triggerdef ...), read with an empty search_path so every name is
/// schema-qualified. Objects are ordered by pg_depend; objects that depend on something being dropped or rebuilt are
/// dropped and recreated with it. What PostgreSQL cannot change in place (removing enum values, repartitioning a table,
/// changing a generated column's expression) is reported as a warning instead of guessed.
/// </summary>
public sealed partial class PostgreSqlScripter : ISchemaScripter
{
    public async Task<SchemaScript> ScriptAsync(IDatabaseSession from, IDatabaseSession to, SchemaScriptOptions options, CancellationToken cancellationToken)
    {
        var patterns = options.Exclude.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        bool Excluded(string name) => patterns.Any(p => p.IsMatch(name));

        var source = await PostgreSqlModel.ReadAsync(from, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var target = await PostgreSqlModel.ReadAsync(to, options.StateSchema, Excluded, cancellationToken).ConfigureAwait(false);
        var owned = OwnedRoutines(options.RepeatableScripts);

        var deploy = new Generator(source, target, owned).Run();
        var revert = new Generator(target, source, owned).Run();
        return new SchemaScript(deploy.Script, revert.Script, deploy.Summary, deploy.Warnings, deploy.DataLoss);
    }

    /// <summary>"schema.name" of views, materialized views, functions and procedures that repeatable scripts create.</summary>
    public static HashSet<string> OwnedRoutines(IEnumerable<string> scripts)
    {
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var script in scripts)
        {
            foreach (Match m in RoutineDefinitionRegex().Matches(script))
            {
                var parts = IdentRegex().Matches(m.Groups["name"].Value)
                    .Select(p => p.Groups["q"].Success ? p.Groups["q"].Value.Replace("\"\"", "\"", StringComparison.Ordinal) : p.Value.ToLowerInvariant()).ToList();
                owned.Add(parts.Count >= 2 ? $"{parts[^2]}.{parts[^1]}" : $"public.{parts[0]}");
            }
        }

        return owned;
    }

    [GeneratedRegex("""\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:TEMP|TEMPORARY|RECURSIVE)\s+)*(?:MATERIALIZED\s+)?(?:VIEW|FUNCTION|PROCEDURE)\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>(?:"(?:[^"]|"")+"|[A-Za-z_][\w$]*)(?:\s*\.\s*(?:"(?:[^"]|"")+"|[A-Za-z_][\w$]*))?)""", RegexOptions.IgnoreCase)]
    private static partial Regex RoutineDefinitionRegex();

    [GeneratedRegex("""
        "(?<q>(?:[^"]|"")+)"|[A-Za-z_][\w$]*
        """)]
    private static partial Regex IdentRegex();

    private sealed record Result(string Script, IReadOnlyList<string> Summary, IReadOnlyList<string> Warnings, bool DataLoss);

    /// <summary>Region 0: drops (dependents first). 1: creates and alters (dependencies first). 2: drops of types, functions, sequences and schemas nothing uses any more.</summary>
    private sealed record Step(int Region, int Phase, int Depth, int Index, string Sql);

    /// <summary>One direction: scripts what turns <c>a</c> into <c>b</c>.</summary>
    private sealed partial class Generator(PostgreSqlModel a, PostgreSqlModel b, HashSet<string> owned)
    {
        // Pseudo-phases for steps that are not objects of their own.
        private const int First = 1000, ReplicaIdentity = 73, Comments = 95, Revokes = 96, Grants = 97, DefaultPrivileges = 98;

        private static readonly HashSet<string> RebuiltWithColumns = new(StringComparer.Ordinal) { "view", "materialized view", "trigger", "policy", "rule", "function", "procedure" };

        private readonly List<Step> steps = [];
        private readonly List<string> summary = [];
        private readonly List<string> warnings = [];
        private readonly Graph before = new(a);
        private readonly Graph after = new(b);

        /// <summary>Objects dropped and created again (changed, or depending on something that is).</summary>
        private readonly HashSet<string> rebuilt = new(StringComparer.Ordinal);

        /// <summary>Objects created by this script (new or rebuilt): their comments and grants are written again.</summary>
        private readonly HashSet<string> created = new(StringComparer.Ordinal);

        /// <summary>Tables whose columns are dropped or retyped: views, triggers, policies and rules on them are rebuilt.</summary>
        private readonly HashSet<string> reshaped = new(StringComparer.Ordinal);

        private bool dataLoss;
        private bool functions;

        public Result Run()
        {
            foreach (var table in b.Tables.Values.Where(t => a.Tables.TryGetValue(t.Key, out var old) && Reshapes(old, t)))
            {
                reshaped.Add(table.Key);
            }

            Rebuilds();
            Objects();
            Enums();
            Domains();
            Composites();
            Sequences();
            Tables();
            Publications();
            CommentsAndGrants();

            foreach (var item in b.Unsupported.Except(a.Unsupported, StringComparer.Ordinal))
            {
                warnings.Add($"Not scripted: {item}.");
            }

            foreach (var item in a.Unsupported.Except(b.Unsupported, StringComparer.Ordinal))
            {
                warnings.Add($"Not scripted (removed): {item}.");
            }

            var ordered = steps.Where(s => s.Region == 0).OrderByDescending(s => s.Phase).ThenByDescending(s => s.Depth).ThenBy(s => s.Index)
                .Concat(steps.Where(s => s.Region == 1).OrderBy(s => s.Phase).ThenBy(s => s.Depth).ThenBy(s => s.Index))
                .Concat(steps.Where(s => s.Region == 2).OrderByDescending(s => s.Phase).ThenByDescending(s => s.Depth).ThenBy(s => s.Index))
                .Select(s => s.Sql.Trim()).ToList();
            if (ordered.Count > 0 && functions)
            {
                // Function bodies may refer to objects created later in the script.
                ordered.Insert(0, "SET LOCAL check_function_bodies = off;");
            }

            return new Result(ordered.Count == 0 ? string.Empty : string.Join("\n\n", ordered) + "\n", summary, warnings, dataLoss);
        }

        // ------------------------------------------------------------------ steps

        private void Drop(string key, string sql, bool late = false)
        {
            var (phase, depth) = before.Of(key);
            steps.Add(new Step(late ? 2 : 0, phase, depth, steps.Count, sql));
        }

        private void Create(string key, string sql, int? phase = null)
        {
            var (p, depth) = after.Of(key);
            steps.Add(new Step(1, Math.Max(phase ?? p, p), depth, steps.Count, sql));
        }

        private void DropFirst(string sql) => steps.Add(new Step(0, First, 0, steps.Count, sql));

        private void Loss(string what)
        {
            dataLoss = true;
            warnings.Add($"DATA LOSS: {what}.");
        }

        private bool Owned(PgObject obj) => obj.Routine is { } r && owned.Contains($"{r.Schema}.{r.Name}");

        // ------------------------------------------------------------------ what gets rebuilt

        private void Rebuilds()
        {
            var sources = new Queue<string>();
            void Rebuild(string key)
            {
                if (rebuilt.Add(key))
                {
                    sources.Enqueue(key);
                }
            }

            foreach (var (key, old) in a.Objects)
            {
                if (b.Objects.TryGetValue(key, out var now) && old.Definition != now.Definition && !Replaceable(old, now) && old.Kind != "extension" && !(Owned(now) && Owned(old)))
                {
                    Rebuild(key);
                }
            }

            foreach (var (key, old) in a.Enums)
            {
                if (b.Enums.TryGetValue(key, out var now) && !Added(old.Labels, now.Labels) && !Used(key))
                {
                    Rebuild(key);
                }
            }

            foreach (var (key, old) in a.Domains)
            {
                if (b.Domains.TryGetValue(key, out var now) && (old.BaseType != now.BaseType || old.Collation != now.Collation) && !Used(key))
                {
                    Rebuild(key);
                }
            }

            // Whatever depends on something dropped, rebuilt or reshaped is rebuilt too.
            foreach (var key in a.Objects.Keys.Where(k => !b.Objects.ContainsKey(k)).Concat(DroppedTypes()).Concat(reshaped))
            {
                sources.Enqueue(key);
            }

            var dependents = before.Dependents();
            while (sources.TryDequeue(out var source))
            {
                foreach (var dependent in dependents.GetValueOrDefault(source) ?? [])
                {
                    if (!a.Objects.TryGetValue(dependent, out var obj) || !b.Objects.ContainsKey(dependent) || obj.Kind == "extension")
                    {
                        continue;
                    }

                    if (reshaped.Contains(source) && !RebuiltWithColumns.Contains(obj.Kind) && obj.Parent != source)
                    {
                        continue;
                    }

                    if (reshaped.Contains(source) && obj.Parent == source && !RebuiltWithColumns.Contains(obj.Kind))
                    {
                        continue;
                    }

                    if (Owned(obj) && !rebuilt.Contains(dependent))
                    {
                        warnings.Add($"{dependent} is defined by a repeatable script; it is dropped and recreated here because {source} changes.");
                    }

                    Rebuild(dependent);
                }
            }

            // Children (indexes, triggers ...) of rebuilt views go with them and come back with them.
            foreach (var obj in b.Objects.Values.Where(o => o.Parent is { } p && rebuilt.Contains(p)))
            {
                rebuilt.Add(obj.Key);
            }
        }

        private IEnumerable<string> DroppedTypes() =>
            a.Sequences.Keys.Where(k => !b.Sequences.ContainsKey(k))
                .Concat(a.Enums.Keys.Where(k => !b.Enums.ContainsKey(k)))
                .Concat(a.Domains.Keys.Where(k => !b.Domains.ContainsKey(k)))
                .Concat(a.Composites.Keys.Where(k => !b.Composites.ContainsKey(k)))
                .Concat(a.Tables.Keys.Where(k => !b.Tables.ContainsKey(k)));

        /// <summary>A function or procedure whose signature, result and arguments stay the same: CREATE OR REPLACE keeps its dependents.</summary>
        private static bool Replaceable(PgObject old, PgObject now) =>
            old.ReplaceSignature is not null && old.ReplaceSignature == now.ReplaceSignature;

        /// <summary>Anything in the old database depends on this key.</summary>
        private bool Used(string key) => before.Dependents().ContainsKey(key);

        private static bool Added(IReadOnlyList<string> old, IReadOnlyList<string> now)
        {
            var i = 0;
            foreach (var label in now)
            {
                if (i < old.Count && old[i] == label)
                {
                    i++;
                }
            }

            return i == old.Count;
        }

        // ------------------------------------------------------------------ objects scripted whole

        private void Objects()
        {
            foreach (var (key, old) in a.Objects.Where(o => !b.Objects.ContainsKey(o.Key)))
            {
                if (Owned(old))
                {
                    warnings.Add($"{key} is defined by a repeatable script, so it is not part of the change; edit the repeatable instead.");
                    continue;
                }

                summary.Add($"- {key}");
                if (old.Parent is { } parent && (!b.Objects.ContainsKey(parent) && !b.Tables.ContainsKey(parent) || rebuilt.Contains(parent)))
                {
                    continue; // dropped with its table or view
                }

                Drop(key, old.Drop, late: old.Phase < Phase.Table && old.Parent is null && !old.Depends.Overlaps(rebuilt));
            }

            foreach (var (key, now) in b.Objects)
            {
                a.Objects.TryGetValue(key, out var old);
                if (old is null)
                {
                    if (Owned(now))
                    {
                        warnings.Add($"{key} is defined by a repeatable script, so it is not part of the change; edit the repeatable instead.");
                        continue;
                    }

                    summary.Add($"+ {key}");
                    CreateObject(now);
                    continue;
                }

                if (rebuilt.Contains(key))
                {
                    if (old.Definition != now.Definition)
                    {
                        summary.Add($"~ {key}");
                    }

                    if (old.Parent is not { } parent || !rebuilt.Contains(parent))
                    {
                        Drop(key, old.Drop);
                    }

                    CreateObject(now);
                }
                else if (old.Definition != now.Definition)
                {
                    if (Owned(now) || Owned(old))
                    {
                        warnings.Add($"{key} is defined by a repeatable script, so it is not part of the change; edit the repeatable instead.");
                    }
                    else if (now.Kind == "extension")
                    {
                        summary.Add($"~ {key}");
                        Extension(old, now);
                    }
                    else
                    {
                        summary.Add($"~ {key}");
                        CreateObject(now); // CREATE OR REPLACE
                    }
                }
            }
        }

        private void CreateObject(PgObject obj)
        {
            created.Add(obj.Key);
            functions |= obj.Kind is "function" or "procedure";
            Create(obj.Key, obj.Create);
        }

        private void Extension(PgObject old, PgObject now)
        {
            var o = old.Definition.Split('|');
            var n = now.Definition.Split('|');
            var name = old.Key["extension ".Length..];
            if (o[1] != n[1])
            {
                Create(now.Key, $"ALTER EXTENSION {name} SET SCHEMA {Sql.Ident(n[1])};");
            }

            if (o[2] != n[2])
            {
                Create(now.Key, $"ALTER EXTENSION {name} UPDATE TO {Sql.Literal(n[2])};");
                warnings.Add($"extension {name}: updated from {o[2]} to {n[2]}; the version must be installed on the server.");
            }
        }

        // ------------------------------------------------------------------ types

        private void Enums()
        {
            foreach (var (key, now) in b.Enums)
            {
                var create = $"CREATE TYPE {now.Name} AS ENUM ({string.Join(", ", now.Labels)});";
                if (!a.Enums.TryGetValue(key, out var old))
                {
                    summary.Add($"+ {key}");
                    created.Add(key);
                    Create(key, create);
                    continue;
                }

                if (old.Labels.SequenceEqual(now.Labels, StringComparer.Ordinal))
                {
                    continue;
                }

                summary.Add($"~ {key}: ({string.Join(", ", old.Labels)}) -> ({string.Join(", ", now.Labels)})");
                if (Added(old.Labels, now.Labels))
                {
                    for (var i = 0; i < now.Labels.Count; i++)
                    {
                        if (old.Labels.Contains(now.Labels[i], StringComparer.Ordinal))
                        {
                            continue;
                        }

                        var where = i > 0 ? $" AFTER {now.Labels[i - 1]}" : old.Labels.Count > 0 ? $" BEFORE {old.Labels[0]}" : string.Empty;
                        Create(key, $"ALTER TYPE {now.Name} ADD VALUE {now.Labels[i]}{where};");
                    }

                    warnings.Add($"{key}: a value added with ALTER TYPE ... ADD VALUE cannot be used in the same transaction; if later statements use it, mark the change !notx or move them to another change.");
                }
                else if (rebuilt.Contains(key))
                {
                    Drop(key, $"DROP TYPE IF EXISTS {old.Name};");
                    created.Add(key);
                    Create(key, create);
                }
                else
                {
                    warnings.Add($"{key}: PostgreSQL cannot remove or reorder enum values while columns use the type. Create a new type, convert the columns (ALTER COLUMN ... TYPE new USING col::text::new) and drop the old one.");
                }
            }

            foreach (var (key, old) in a.Enums.Where(e => !b.Enums.ContainsKey(e.Key)))
            {
                summary.Add($"- {key}");
                Drop(key, $"DROP TYPE IF EXISTS {old.Name};", late: true);
            }
        }

        private static string DomainSql(PgDomain d) =>
            $"CREATE DOMAIN {d.Name} AS {d.BaseType}{(d.Collation is null ? string.Empty : $" COLLATE {d.Collation}")}{(d.Default is null ? string.Empty : $" DEFAULT {d.Default}")}{(d.NotNull ? " NOT NULL" : string.Empty)}"
            + string.Concat(d.Constraints.Select(c => $"\n    CONSTRAINT {c.Name} {c.Definition}")) + ";";

        private void Domains()
        {
            foreach (var (key, now) in b.Domains)
            {
                if (!a.Domains.TryGetValue(key, out var old))
                {
                    summary.Add($"+ {key}");
                    created.Add(key);
                    Create(key, DomainSql(now));
                    continue;
                }

                if (old.BaseType != now.BaseType || old.Collation != now.Collation)
                {
                    summary.Add($"~ {key}: {old.BaseType} -> {now.BaseType}");
                    if (rebuilt.Contains(key))
                    {
                        Drop(key, $"DROP DOMAIN IF EXISTS {old.Name};");
                        created.Add(key);
                        Create(key, DomainSql(now));
                    }
                    else
                    {
                        warnings.Add($"{key}: PostgreSQL cannot change a domain's base type while columns use it. Create a new domain, convert the columns and drop the old one.");
                    }

                    continue;
                }

                var changed = false;
                if (old.Default != now.Default)
                {
                    Create(key, now.Default is null ? $"ALTER DOMAIN {now.Name} DROP DEFAULT;" : $"ALTER DOMAIN {now.Name} SET DEFAULT {now.Default};");
                    changed = true;
                }

                if (old.NotNull != now.NotNull)
                {
                    Create(key, $"ALTER DOMAIN {now.Name} {(now.NotNull ? "SET" : "DROP")} NOT NULL;");
                    changed = true;
                }

                foreach (var c in old.Constraints.Where(c => !now.Constraints.Contains(c)))
                {
                    Create(key, $"ALTER DOMAIN {now.Name} DROP CONSTRAINT IF EXISTS {c.Name};");
                    changed = true;
                }

                foreach (var c in now.Constraints.Where(c => !old.Constraints.Contains(c)))
                {
                    Create(key, $"ALTER DOMAIN {now.Name} ADD CONSTRAINT {c.Name} {c.Definition};");
                    changed = true;
                }

                if (changed)
                {
                    summary.Add($"~ {key}");
                }
            }

            foreach (var (key, old) in a.Domains.Where(d => !b.Domains.ContainsKey(d.Key)))
            {
                summary.Add($"- {key}");
                Drop(key, $"DROP DOMAIN IF EXISTS {old.Name};", late: true);
            }
        }

        private void Composites()
        {
            foreach (var (key, now) in b.Composites)
            {
                if (!a.Composites.TryGetValue(key, out var old))
                {
                    summary.Add($"+ {key}");
                    created.Add(key);
                    Create(key, $"CREATE TYPE {now.Name} AS ({string.Join(", ", now.Attributes.Select(x => $"{x.Name} {x.Type}"))});");
                    continue;
                }

                var parts = new List<string>();
                parts.AddRange(old.Attributes.Where(o => now.Attributes.All(n => n.Name != o.Name)).Select(o => $"DROP ATTRIBUTE {o.Name}"));
                parts.AddRange(now.Attributes.Where(n => old.Attributes.All(o => o.Name != n.Name)).Select(n => $"ADD ATTRIBUTE {n.Name} {n.Type}"));
                parts.AddRange(now.Attributes.Where(n => old.Attributes.Any(o => o.Name == n.Name && o.Type != n.Type)).Select(n => $"ALTER ATTRIBUTE {n.Name} TYPE {n.Type}"));
                if (parts.Count > 0)
                {
                    summary.Add($"~ {key}");
                    Create(key, $"ALTER TYPE {now.Name} {string.Join(", ", parts)};");
                }
            }

            foreach (var (key, old) in a.Composites.Where(c => !b.Composites.ContainsKey(c.Key)))
            {
                summary.Add($"- {key}");
                Drop(key, $"DROP TYPE IF EXISTS {old.Name};", late: true);
            }
        }

        // ------------------------------------------------------------------ sequences

        private void Sequences()
        {
            foreach (var (key, now) in b.Sequences)
            {
                if (!a.Sequences.TryGetValue(key, out var old))
                {
                    summary.Add($"+ {key}");
                    created.Add(key);
                    Create(key, $"CREATE SEQUENCE {now.Name} AS {now.Type} START WITH {now.Start} INCREMENT BY {now.Increment} MINVALUE {now.Min} MAXVALUE {now.Max} CACHE {now.Cache}{(now.Cycle ? " CYCLE" : string.Empty)};");
                    if (now.OwnedBy is { } owner)
                    {
                        Create(key, $"ALTER SEQUENCE {now.Name} OWNED BY {owner};", Phase.SequenceOwner);
                    }

                    continue;
                }

                var parts = new List<string>();
                if (old.Type != now.Type)
                {
                    parts.Add($"AS {now.Type}");
                }

                if (old.Start != now.Start)
                {
                    parts.Add($"START WITH {now.Start}");
                }

                if (old.Increment != now.Increment)
                {
                    parts.Add($"INCREMENT BY {now.Increment}");
                }

                if (old.Min != now.Min)
                {
                    parts.Add($"MINVALUE {now.Min}");
                }

                if (old.Max != now.Max)
                {
                    parts.Add($"MAXVALUE {now.Max}");
                }

                if (old.Cache != now.Cache)
                {
                    parts.Add($"CACHE {now.Cache}");
                }

                if (old.Cycle != now.Cycle)
                {
                    parts.Add(now.Cycle ? "CYCLE" : "NO CYCLE");
                }

                if (parts.Count > 0)
                {
                    Create(key, $"ALTER SEQUENCE {now.Name} {string.Join(' ', parts)};");
                }

                if (old.OwnedBy != now.OwnedBy)
                {
                    if (old.OwnedBy is not null)
                    {
                        DropFirst($"ALTER SEQUENCE {now.Name} OWNED BY NONE;");
                    }

                    if (now.OwnedBy is { } owner)
                    {
                        Create(key, $"ALTER SEQUENCE {now.Name} OWNED BY {owner};", Phase.SequenceOwner);
                    }
                }

                if (parts.Count > 0 || old.OwnedBy != now.OwnedBy)
                {
                    summary.Add($"~ {key}");
                }
            }

            foreach (var (key, old) in a.Sequences.Where(s => !b.Sequences.ContainsKey(s.Key)))
            {
                summary.Add($"- {key}");
                Drop(key, $"DROP SEQUENCE IF EXISTS {old.Name};", late: true);
            }
        }

        // ------------------------------------------------------------------ tables

        private static bool Reshapes(PgTable old, PgTable now)
        {
            var renames = Renames(old, now);
            return old.Columns.Any(c => now.Columns.All(n => n.Name != c.Name) && !renames.ContainsKey(c.Name))
                || old.Columns.Any(c => now.Columns.FirstOrDefault(n => n.Name == c.Name) is { } n && (n.Type != c.Type || n.Collation != c.Collation));
        }

        /// <summary>A column removed and one added with exactly the same definition: most likely a rename (old name -> new name).</summary>
        private static Dictionary<string, string> Renames(PgTable old, PgTable now)
        {
            var removed = old.Columns.Where(c => now.Columns.All(n => n.Name != c.Name)).ToList();
            var added = now.Columns.Where(c => old.Columns.All(o => o.Name != c.Name)).ToList();
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var gone in removed)
            {
                var matches = added.Where(c => c.Shape == gone.Shape).ToList();
                if (matches.Count == 1 && removed.Count(r => r.Shape == matches[0].Shape) == 1)
                {
                    renames[gone.Name] = matches[0].Name;
                }
            }

            return renames;
        }

        private void Tables()
        {
            foreach (var (key, now) in b.Tables)
            {
                if (!a.Tables.TryGetValue(key, out var old))
                {
                    summary.Add($"+ {key}");
                    CreateTable(now);
                }
                else
                {
                    AlterTable(old, now);
                }
            }

            foreach (var (key, old) in a.Tables.Where(t => !b.Tables.ContainsKey(t.Key)))
            {
                summary.Add($"- {key}");
                if (old.Kind != 'f')
                {
                    Loss($"drops {key} and its rows");
                }

                if (old.PartitionOf is { } parent && !b.Tables.ContainsKey($"table {parent}"))
                {
                    continue; // dropped with the partitioned table
                }

                Drop(key, $"DROP {(old.Kind == 'f' ? "FOREIGN TABLE" : "TABLE")} IF EXISTS {old.Name};");
            }
        }

        private void CreateTable(PgTable t)
        {
            created.Add(t.Key);
            var unlogged = t.Persistence == 'u' ? "UNLOGGED " : string.Empty;
            var columns = t.Columns.Select(c => c.Definition()).ToList();
            var sql = new StringBuilder();
            if (t.Kind == 'f')
            {
                sql.Append($"CREATE FOREIGN TABLE {t.Name} (\n    {string.Join(",\n    ", columns)})");
                if (t.Parents.Count > 0)
                {
                    sql.Append($"\nINHERITS ({string.Join(", ", t.Parents)})");
                }

                sql.Append($"\nSERVER {t.ForeignServer}{t.ForeignOptions}");
            }
            else if (t.PartitionOf is { } parent)
            {
                sql.Append($"CREATE {unlogged}TABLE {t.Name} PARTITION OF {parent}");
                if (columns.Count > 0)
                {
                    sql.Append($" (\n    {string.Join(",\n    ", columns)})");
                }

                sql.Append($"\n{t.Bound}");
            }
            else
            {
                sql.Append(columns.Count == 0 ? $"CREATE {unlogged}TABLE {t.Name} ()" : $"CREATE {unlogged}TABLE {t.Name} (\n    {string.Join(",\n    ", columns)})");
                if (t.Parents.Count > 0)
                {
                    sql.Append($"\nINHERITS ({string.Join(", ", t.Parents)})");
                }
            }

            if (t.Kind != 'f')
            {
                if (t.PartitionKey is { } partitionKey)
                {
                    sql.Append($"\nPARTITION BY {partitionKey}");
                }

                if (t.Options is { } options)
                {
                    sql.Append($"\nWITH ({options})");
                }

                if (t.Tablespace is { } tablespace)
                {
                    sql.Append($"\nTABLESPACE {tablespace}");
                }
            }

            sql.Append(';');
            foreach (var c in t.Columns)
            {
                if (c.Storage is { } storage)
                {
                    sql.Append($"\nALTER TABLE {t.Name} ALTER COLUMN {Sql.Ident(c.Name)} SET STORAGE {storage};");
                }

                if (c.Options is { } options)
                {
                    sql.Append($"\nALTER TABLE {t.Name} ALTER COLUMN {Sql.Ident(c.Name)} SET ({options});");
                }
            }

            if (t.Rls)
            {
                sql.Append($"\nALTER TABLE {t.Name} ENABLE ROW LEVEL SECURITY;");
            }

            if (t.ForceRls)
            {
                sql.Append($"\nALTER TABLE {t.Name} FORCE ROW LEVEL SECURITY;");
            }

            Create(t.Key, sql.ToString());
            if (t.ReplicaIdentity != "DEFAULT")
            {
                Create(t.Key, $"ALTER TABLE {t.Name} REPLICA IDENTITY {t.ReplicaIdentity};", ReplicaIdentity);
            }
        }

        private void AlterTable(PgTable old, PgTable now)
        {
            var name = now.Name;
            var key = now.Key;
            var changes = new List<string>();
            void Alter(string sql, int? phase = null)
            {
                Create(key, sql, phase);
            }

            if (old.Kind != now.Kind || old.PartitionKey != now.PartitionKey)
            {
                summary.Add($"~ {key}: partitioning {old.PartitionKey ?? "none"} -> {now.PartitionKey ?? "none"}");
                warnings.Add($"{key}: PostgreSQL cannot change how a table is partitioned in place. Create a new table partitioned the new way, copy the rows, and swap the names.");
                return;
            }

            if (old.Persistence != now.Persistence)
            {
                Alter($"ALTER TABLE {name} SET {(now.Persistence == 'u' ? "UNLOGGED" : "LOGGED")};");
                changes.Add(now.Persistence == 'u' ? "unlogged" : "logged");
            }

            if (old.Options != now.Options)
            {
                var o = Options(old.Options);
                var n = Options(now.Options);
                var reset = o.Keys.Where(k => !n.ContainsKey(k)).ToList();
                if (reset.Count > 0)
                {
                    Alter($"ALTER TABLE {name} RESET ({string.Join(", ", reset)});");
                }

                if (n.Count > 0)
                {
                    Alter($"ALTER TABLE {name} SET ({now.Options});");
                }

                changes.Add($"options ({now.Options})");
            }

            if (old.Tablespace != now.Tablespace)
            {
                Alter($"ALTER TABLE {name} SET TABLESPACE {now.Tablespace ?? "pg_default"};");
                changes.Add($"tablespace {now.Tablespace ?? "pg_default"}");
            }

            foreach (var parent in old.Parents.Except(now.Parents))
            {
                DropFirst($"ALTER TABLE {name} NO INHERIT {parent};");
                changes.Add($"no inherit {parent}");
            }

            foreach (var parent in now.Parents.Except(old.Parents))
            {
                Alter($"ALTER TABLE {name} INHERIT {parent};", Phase.Partition);
                changes.Add($"inherit {parent}");
            }

            if (old.PartitionOf != now.PartitionOf || old.Bound != now.Bound)
            {
                if (old.PartitionOf is { } from && b.Tables.ContainsKey($"table {from}"))
                {
                    DropFirst($"ALTER TABLE {from} DETACH PARTITION {name};");
                }

                if (now.PartitionOf is { } to)
                {
                    Alter($"ALTER TABLE {to} ATTACH PARTITION {name} {now.Bound};", Phase.Partition);
                }

                changes.Add(now.PartitionOf is null ? "detached" : $"partition of {now.PartitionOf} {now.Bound}");
            }

            if (old.ForeignServer != now.ForeignServer || old.ForeignOptions != now.ForeignOptions)
            {
                warnings.Add($"{key}: the foreign server or options changed; adjust them with ALTER FOREIGN TABLE ... OPTIONS (SET ...) by hand.");
            }

            if (old.Rls != now.Rls)
            {
                Alter($"ALTER TABLE {name} {(now.Rls ? "ENABLE" : "DISABLE")} ROW LEVEL SECURITY;");
                changes.Add(now.Rls ? "row level security on" : "row level security off");
            }

            if (old.ForceRls != now.ForceRls)
            {
                Alter($"ALTER TABLE {name} {(now.ForceRls ? "FORCE" : "NO FORCE")} ROW LEVEL SECURITY;");
            }

            if (old.ReplicaIdentity != now.ReplicaIdentity)
            {
                Alter($"ALTER TABLE {name} REPLICA IDENTITY {now.ReplicaIdentity};", ReplicaIdentity);
                changes.Add($"replica identity {now.ReplicaIdentity.ToLowerInvariant()}");
            }

            if (changes.Count > 0)
            {
                summary.Add($"~ {key}: {string.Join(", ", changes)}");
            }

            Columns(old, now);
        }

        private void Columns(PgTable old, PgTable now)
        {
            var name = now.Name;
            var key = now.Key;
            var renames = Renames(old, now);
            void Alter(string sql) => Create(key, sql);

            foreach (var (from, to) in renames)
            {
                Alter($"ALTER TABLE {name} RENAME COLUMN {Sql.Ident(from)} TO {Sql.Ident(to)};");
                summary.Add($"~ column {name}.{from} renamed to {to}");
                warnings.Add($"{name}: column {from} was replaced by {to} with the same definition; scripted as a rename. If it really is a new column, replace the rename with DROP/ADD.");
            }

            foreach (var gone in old.Columns.Where(c => now.Columns.All(n => n.Name != c.Name) && !renames.ContainsKey(c.Name)))
            {
                Drop(key, $"ALTER TABLE {name} DROP COLUMN IF EXISTS {Sql.Ident(gone.Name)};");
                summary.Add($"- column {name}.{gone.Name}");
                Loss($"drops column {name}.{gone.Name}");
            }

            foreach (var added in now.Columns.Where(c => old.Columns.All(o => o.Name != c.Name) && !renames.ContainsValue(c.Name)))
            {
                Alter($"ALTER TABLE {name} ADD COLUMN {added.Definition()};");
                summary.Add($"+ column {name}.{added.Name} {added.Type}");
                if (added.NotNull && added.Default is null && added.Identity == '\0')
                {
                    warnings.Add($"{name}: column {added.Name} is NOT NULL without a default; adding it fails if the table has rows. Add a default, or add it nullable, fill it, then SET NOT NULL.");
                }
            }

            foreach (var n in now.Columns)
            {
                var source = renames.FirstOrDefault(r => r.Value == n.Name).Key ?? n.Name;
                if (old.Columns.FirstOrDefault(c => c.Name == source) is not { } o || renames.ContainsValue(n.Name))
                {
                    continue;
                }

                var column = $"ALTER TABLE {name} ALTER COLUMN {Sql.Ident(n.Name)}";
                var details = new List<string>();
                var generatedChanged = o.Generated != n.Generated || (n.Generated != '\0' && o.Default != n.Default);
                if (generatedChanged)
                {
                    warnings.Add($"{name}: the generated expression of column {n.Name} changed; PostgreSQL cannot change it in place on every version. Drop and re-add the column (and anything that uses it), or on PostgreSQL 17+ use ALTER COLUMN ... SET EXPRESSION AS (...).");
                    details.Add("generated expression");
                }

                var retyped = o.Type != n.Type || o.Collation != n.Collation;
                var defaultChanged = n.Generated == '\0' && o.Generated == '\0' && o.Default != n.Default;
                if (o.Identity != '\0' && n.Identity == '\0')
                {
                    Alter($"{column} DROP IDENTITY;");
                    details.Add("identity dropped");
                }

                if (retyped && o.Default is not null && o.Generated == '\0' && !defaultChanged)
                {
                    Alter($"{column} DROP DEFAULT;");
                    defaultChanged = n.Default is not null;
                }

                if (retyped)
                {
                    Alter($"{column} TYPE {n.Type}{(n.Collation is null ? string.Empty : $" COLLATE {n.Collation}")} USING {Sql.Ident(n.Name)}::{n.Type};");
                    details.Add($"{o.Type} -> {n.Type}");
                }

                if (!o.NotNull && n.NotNull)
                {
                    Alter($"{column} SET NOT NULL;");
                    details.Add("not null");
                }

                if (o.Identity == '\0' && n.Identity != '\0')
                {
                    Alter($"{column} ADD GENERATED {(n.Identity == 'a' ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY{(n.IdentityOptions is null ? string.Empty : $" ({n.IdentityOptions})")};");
                    details.Add("identity");
                }
                else if (o.Identity != '\0' && n.Identity != '\0')
                {
                    var sets = new List<string>();
                    if (o.Identity != n.Identity)
                    {
                        sets.Add($"SET GENERATED {(n.Identity == 'a' ? "ALWAYS" : "BY DEFAULT")}");
                    }

                    if (o.IdentityOptions != n.IdentityOptions && n.IdentityOptions is not null)
                    {
                        sets.AddRange(IdentitySets(o.IdentityOptions, n.IdentityOptions));
                    }

                    if (sets.Count > 0)
                    {
                        Alter($"{column} {string.Join(' ', sets)};");
                        details.Add("identity options");
                    }
                }

                if (defaultChanged)
                {
                    Alter(n.Default is null ? $"{column} DROP DEFAULT;" : $"{column} SET DEFAULT {n.Default};");
                    details.Add(n.Default is null ? "default dropped" : $"default {n.Default}");
                }

                if (o.NotNull && !n.NotNull)
                {
                    Alter($"{column} DROP NOT NULL;");
                    details.Add("null");
                }

                if (o.Storage != n.Storage)
                {
                    Alter($"{column} SET STORAGE {n.Storage ?? n.DefaultStorage};");
                    details.Add($"storage {(n.Storage ?? n.DefaultStorage).ToLowerInvariant()}");
                }

                if (o.Options != n.Options)
                {
                    var reset = Options(o.Options).Keys.Where(k => !Options(n.Options).ContainsKey(k)).ToList();
                    if (reset.Count > 0)
                    {
                        Alter($"{column} RESET ({string.Join(", ", reset)});");
                    }

                    if (n.Options is not null)
                    {
                        Alter($"{column} SET ({n.Options});");
                    }

                    details.Add("options");
                }

                if (details.Count > 0)
                {
                    summary.Add($"~ column {name}.{n.Name}: {string.Join(", ", details)}");
                }
            }

            // Defaults that call a function being dropped and recreated: dropped first, set again after.
            if (old.Columns.Any(c => c.Default is not null) && before.DependsOnAny(key, rebuilt))
            {
                foreach (var c in old.Columns.Where(c => c.Default is not null && c.Generated == '\0' && now.Columns.Any(n => n.Name == c.Name && n.Default == c.Default)))
                {
                    Drop(key, $"ALTER TABLE {old.Name} ALTER COLUMN {Sql.Ident(c.Name)} DROP DEFAULT;");
                    Alter($"ALTER TABLE {name} ALTER COLUMN {Sql.Ident(c.Name)} SET DEFAULT {c.Default};");
                }
            }
        }

        private static IEnumerable<string> IdentitySets(string? old, string now)
        {
            var o = IdentityOptionRegex().Matches(old ?? string.Empty).ToDictionary(m => m.Groups["k"].Value, m => m.Value, StringComparer.Ordinal);
            foreach (Match m in IdentityOptionRegex().Matches(now))
            {
                if (!o.TryGetValue(m.Groups["k"].Value, out var was) || was != m.Value)
                {
                    yield return m.Groups["k"].Value == "CYCLE" ? "SET CYCLE" : $"SET {m.Value}";
                }
            }

            if (old?.Contains("CYCLE", StringComparison.Ordinal) == true && !now.Contains(" CYCLE", StringComparison.Ordinal))
            {
                yield return "SET NO CYCLE";
            }
        }

        [GeneratedRegex(@"(?<k>START WITH|INCREMENT BY|MINVALUE|MAXVALUE|CACHE) -?\d+|(?<k>(?<!NO )CYCLE)$")]
        private static partial Regex IdentityOptionRegex();

        private static Dictionary<string, string> Options(string? options) =>
            (options ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries)
                .Select(o => o.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : string.Empty, StringComparer.Ordinal);

        // ------------------------------------------------------------------ publications

        private static string PublicationSql(PgPublication p) =>
            $"CREATE PUBLICATION {p.Name}{(p.AllTables ? " FOR ALL TABLES" : p.Tables.Count > 0 ? $" FOR TABLE {string.Join(", ", p.Tables)}" : string.Empty)} WITH (publish = {Sql.Literal(p.Publish)});";

        private void Publications()
        {
            foreach (var (key, now) in b.Publications)
            {
                if (!a.Publications.TryGetValue(key, out var old))
                {
                    summary.Add($"+ {key}");
                    created.Add(key);
                    Create(key, PublicationSql(now));
                    continue;
                }

                if (old.AllTables == now.AllTables && old.Publish == now.Publish && old.Tables.SequenceEqual(now.Tables))
                {
                    continue;
                }

                summary.Add($"~ {key}");
                if (old.AllTables != now.AllTables)
                {
                    Drop(key, $"DROP PUBLICATION IF EXISTS {old.Name};");
                    created.Add(key);
                    Create(key, PublicationSql(now));
                    continue;
                }

                if (old.Publish != now.Publish)
                {
                    Create(key, $"ALTER PUBLICATION {now.Name} SET (publish = {Sql.Literal(now.Publish)});");
                }

                var dropped = old.Tables.Except(now.Tables).Where(t => b.Tables.ContainsKey($"table {t}")).ToList();
                var added = now.Tables.Except(old.Tables).ToList();
                if (dropped.Count > 0)
                {
                    DropFirst($"ALTER PUBLICATION {now.Name} DROP TABLE {string.Join(", ", dropped)};");
                }

                if (added.Count > 0)
                {
                    Create(key, $"ALTER PUBLICATION {now.Name} ADD TABLE {string.Join(", ", added)};");
                }
            }

            foreach (var (key, old) in a.Publications.Where(p => !b.Publications.ContainsKey(p.Key)))
            {
                summary.Add($"- {key}");
                Drop(key, $"DROP PUBLICATION IF EXISTS {old.Name};");
            }
        }

        // ------------------------------------------------------------------ comments, grants, default privileges

        /// <summary>The object exists before and after, and this script does not recreate it.</summary>
        private bool Kept(string owner) => !created.Contains(owner) && !rebuilt.Contains(owner) && Exists(a, owner) && Exists(b, owner);

        private static bool Exists(PostgreSqlModel m, string key) =>
            key == "schema public" || m.Objects.ContainsKey(key) || m.Tables.ContainsKey(key) || m.Sequences.ContainsKey(key) || m.Enums.ContainsKey(key) || m.Domains.ContainsKey(key)
            || m.Composites.ContainsKey(key) || m.Publications.ContainsKey(key);

        /// <summary>"COLUMN schema.table.col" or "x (col) ON TABLE ...": the column still exists in b under that name.</summary>
        private bool ColumnKept(string owner, string column) =>
            b.Tables.TryGetValue(owner, out var t) && t.Columns.Any(c => Sql.Ident(c.Name) == column);

        private void CommentsAndGrants()
        {
            foreach (var (target, (owner, text)) in b.Comments)
            {
                if (!a.Comments.TryGetValue(target, out var was) || was.Text != text || !Kept(owner))
                {
                    if (Exists(b, owner))
                    {
                        Create(owner, $"COMMENT ON {target} IS {Sql.Literal(text)};", Comments);
                    }
                }
            }

            foreach (var (target, (owner, _)) in a.Comments.Where(c => !b.Comments.ContainsKey(c.Key)))
            {
                var column = target.StartsWith("COLUMN ", StringComparison.Ordinal) ? target[(target.LastIndexOf('.') + 1)..] : null;
                if (Kept(owner) && (column is null || ColumnKept(owner, column)))
                {
                    Create(owner, $"COMMENT ON {target} IS NULL;", Comments);
                }
            }

            foreach (var (grant, owner) in a.Grants.Where(g => !b.Grants.ContainsKey(g.Key)))
            {
                var column = GrantColumnRegex().Match(grant) is { Success: true } m ? m.Groups["c"].Value : null;
                if (Kept(owner) && (column is null || ColumnKept(owner, column)))
                {
                    Create(owner, $"REVOKE {Revoke(grant)};", Revokes);
                }
            }

            foreach (var (grant, owner) in b.Grants)
            {
                if (!a.Grants.ContainsKey(grant) || !Kept(owner))
                {
                    Create(owner, $"GRANT {grant};", Grants);
                }
            }

            foreach (var entry in a.DefaultPrivileges.Except(b.DefaultPrivileges))
            {
                var (prefix, rest) = Split(entry);
                steps.Add(new Step(1, DefaultPrivileges, 0, steps.Count, $"ALTER DEFAULT PRIVILEGES {prefix} REVOKE {Revoke(rest)};"));
                summary.Add($"- default privileges {prefix}: {rest}");
            }

            foreach (var entry in b.DefaultPrivileges.Except(a.DefaultPrivileges))
            {
                var (prefix, rest) = Split(entry);
                steps.Add(new Step(1, DefaultPrivileges, 0, steps.Count, $"ALTER DEFAULT PRIVILEGES {prefix} GRANT {rest};"));
                summary.Add($"+ default privileges {prefix}: {rest}");
            }

            static (string Prefix, string Tail) Split(string entry)
            {
                var bar = entry.IndexOf('|', StringComparison.Ordinal);
                return (entry[..bar], entry[(bar + 1)..]);
            }
        }

        /// <summary>"privilege ON x TO grantee [WITH GRANT OPTION]" as "privilege ON x FROM grantee".</summary>
        private static string Revoke(string grant)
        {
            var text = grant.EndsWith(" WITH GRANT OPTION", StringComparison.Ordinal) ? grant[..^" WITH GRANT OPTION".Length] : grant;
            var to = text.LastIndexOf(" TO ", StringComparison.Ordinal);
            return text[..to] + " FROM " + text[(to + 4)..];
        }

        [GeneratedRegex(@"^\S+(?: \S+)* \((?<c>[^)]+)\) ON ")]
        private static partial Regex GrantColumnRegex();
    }

    /// <summary>Phases and depths from pg_depend: an object comes after (and is dropped before) everything it depends on.</summary>
    private sealed class Graph(PostgreSqlModel m)
    {
        private readonly Dictionary<string, (int Phase, int Depth)> memo = new(StringComparer.Ordinal);
        private readonly HashSet<string> visiting = new(StringComparer.Ordinal);
        private Dictionary<string, List<string>>? dependents;

        public (int Phase, int Depth) Of(string key)
        {
            if (memo.TryGetValue(key, out var known))
            {
                return known;
            }

            var phase = PhaseOf(key);
            if (!visiting.Add(key))
            {
                return (phase, 0); // a cycle (a table default calling a function on the table): the phase decides
            }

            var depth = 0;
            foreach (var dep in Deps(key))
            {
                var (p, d) = Of(dep);
                phase = Math.Max(phase, p);
                depth = Math.Max(depth, d + 1);
            }

            visiting.Remove(key);
            return memo[key] = (phase, depth);
        }

        public bool DependsOnAny(string key, IReadOnlySet<string> keys) => Deps(key).Any(keys.Contains);

        /// <summary>Key -> the keys that depend on it.</summary>
        public Dictionary<string, List<string>> Dependents()
        {
            if (dependents is not null)
            {
                return dependents;
            }

            dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var key in m.Objects.Keys.Concat(m.Tables.Keys))
            {
                foreach (var dep in Deps(key))
                {
                    if (!dependents.TryGetValue(dep, out var list))
                    {
                        dependents[dep] = list = [];
                    }

                    list.Add(key);
                }
            }

            return dependents;
        }

        private IEnumerable<string> Deps(string key)
        {
            var deps = new HashSet<string>(m.Depends.GetValueOrDefault(key) ?? [], StringComparer.Ordinal);
            if (m.Objects.TryGetValue(key, out var obj) && obj.Parent is { } parent)
            {
                deps.Add(parent);
            }

            if (m.Tables.TryGetValue(key, out var table))
            {
                foreach (var p in table.Parents.Append(table.PartitionOf).OfType<string>())
                {
                    deps.Add($"table {p}");
                }
            }

            deps.Remove(key);
            return deps.Where(Known);
        }

        private bool Known(string key) =>
            m.Objects.ContainsKey(key) || m.Tables.ContainsKey(key) || m.Sequences.ContainsKey(key) || m.Enums.ContainsKey(key)
            || m.Domains.ContainsKey(key) || m.Composites.ContainsKey(key) || m.Publications.ContainsKey(key);

        private int PhaseOf(string key) =>
            m.Objects.TryGetValue(key, out var obj) ? obj.Phase
            : m.Tables.TryGetValue(key, out var t) ? (t.Kind == 'f' ? Phase.ForeignTable : t.IsPartition ? Phase.Partition : Phase.Table)
            : m.Sequences.ContainsKey(key) ? Phase.Sequence
            : m.Enums.ContainsKey(key) ? Phase.Enum
            : m.Domains.ContainsKey(key) ? Phase.Domain
            : m.Composites.ContainsKey(key) ? Phase.Composite
            : m.Publications.ContainsKey(key) ? Phase.Publication
            : 0;
    }
}
