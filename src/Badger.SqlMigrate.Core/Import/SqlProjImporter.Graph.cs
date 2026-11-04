using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Preprocessing;

namespace Badger.SqlMigrate.Core.Import;

public static partial class SqlProjImporter
{
    /// <summary>One object from the project: the statement creating it and the statements in its file that belong to it.</summary>
    private sealed class Unit
    {
        public required string File { get; init; }

        public required string Text { get; init; }

        public List<SqlStatementInfo> Statements { get; } = [];

        /// <summary>The script holds statements no longer part of this unit, so it is rebuilt from its statements.</summary>
        public bool Sliced { get; set; }

        public bool Unparsed { get; init; }

        public SqlStatementInfo Primary => Statements[0];

        public SqlObjectKind Kind => Primary.Kind;

        public SqlObjectName? Target => Primary.Target;

        public bool Repeatable { get; set; }

        public required int Order { get; init; }

        public List<SqlTextEdit> Edits { get; } = [];

        /// <summary>Foreign keys moved out of their tables to break a cycle: the change that adds them.</summary>
        public Unit? ForeignKeysOf { get; init; }

        public List<string> ForeignKeyDeploy { get; } = [];

        public List<string?> ForeignKeyRevert { get; } = [];

        public string Name { get; set; } = string.Empty;
    }

    /// <param name="From">Depends on <paramref name="To"/>.</param>
    /// <param name="Soft">From can be created before To exists (deferred name resolution).</param>
    /// <param name="ForeignKey">A foreign key inside From's CREATE TABLE, which can move to a later change.</param>
    /// <param name="Statement">An ALTER TABLE ... ADD CONSTRAINT in From's file, which can move to a later change.</param>
    private sealed record Edge(Unit From, Unit To, bool Soft, SqlForeignKey? ForeignKey = null, SqlStatementInfo? Statement = null);

    private sealed class Context(ISqlObjectParser parser, ImportedProject result, string projectDir, string defaultSchema, Dictionary<string, string> variableNames)
    {
        private readonly List<Unit> units = [];
        private readonly Dictionary<string, Unit> byKey = new(StringComparer.Ordinal);
        private readonly List<Edge> edges = [];
        private readonly HashSet<string> undefinedReported = new(StringComparer.OrdinalIgnoreCase);

        public void AddFile(string path)
        {
            var file = Relative(projectDir, path);
            var text = Prepare(ReadScript(path), file);
            var parsed = parser.Parse(text);
            if (parsed.Errors.Count > 0)
            {
                result.Warnings.Add($"{file}: could not be parsed ({parsed.Errors[0]}); imported as one irreversible change in file order. Check where it belongs in bsm.plan.");
                var unit = new Unit { File = file, Text = text, Order = units.Count, Unparsed = true };
                unit.Statements.Add(new SqlStatementInfo { Kind = SqlObjectKind.Other, Offset = 0, Length = text.Length, Batch = 0 });
                units.Add(unit);
                return;
            }

            if (parsed.Statements.Count == 0)
            {
                result.Warnings.Add($"{file}: no statements; not imported.");
                return;
            }

            var fileUnits = new List<Unit>();
            foreach (var statement in parsed.Statements)
            {
                Unit? owner = null;
                if (statement.Kind >= SqlObjectKind.Index && fileUnits.Count > 0)
                {
                    owner = statement.Target is null
                        ? fileUnits[^1]
                        : fileUnits.LastOrDefault(u => u.Target is not null && SameObject(u.Target, statement.Target)) ?? (statement.Kind is SqlObjectKind.ExtendedProperty or SqlObjectKind.Permission ? fileUnits[^1] : null);
                }

                if (owner is null)
                {
                    owner = new Unit { File = file, Text = text, Order = units.Count };
                    units.Add(owner);
                    fileUnits.Add(owner);
                }

                owner.Statements.Add(statement);
            }

            foreach (var unit in fileUnits)
            {
                unit.Sliced = fileUnits.Count > 1;
            }
        }

        public void Build()
        {
            foreach (var unit in units)
            {
                if (Key(unit.Kind, unit.Target) is { } key && !byKey.TryAdd(key, unit))
                {
                    result.Warnings.Add($"{unit.File}: {Describe(unit)} is also created in {byKey[key].File}; dependencies on it point to the first.");
                }
            }

            foreach (var unit in units)
            {
                AddEdges(unit);
            }

            Classify();
            var changes = OrderChanges();
            NameAndWriteChanges(changes);
            WriteRepeatables();
        }

        /// <summary>The post-deployment script becomes a repeatable that runs after the repeatables it uses.</summary>
        public void AddPostDeploy(string file, string text)
        {
            text = Prepare(text, file);
            var parsed = parser.Parse(text);
            var requires = parsed.Statements.SelectMany(s => s.References)
                .Select(r => byKey.GetValueOrDefault(RefKey(r)))
                .OfType<Unit>()
                .Where(u => u.Repeatable)
                .Select(u => u.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var path = "post-deploy/" + Sanitize(System.IO.Path.GetFileName(file));
            var script = (requires.Count > 0 ? $"-- bsm:requires {string.Join(' ', requires)}\n" : string.Empty) + text.TrimEnd('\n') + "\n";
            result.Repeatables.Add(new ImportedRepeatable(path, Escape(script, file), file));
            result.Warnings.Add($"{file}: the post-deployment script became repeatable/{path}. SSDT ran it on every publish; bsm runs it again whenever it changes, so keep it safe to re-run. Data for one environment only belongs in environments/<name>/.");
            if (parsed.Errors.Count > 0)
            {
                result.Warnings.Add($"{file}: could not be parsed ({parsed.Errors[0]}), so it has no bsm:requires; add them if it uses views or procedures.");
            }
        }

        public void FinishVariables()
        {
            foreach (var name in PublishVariables.Where(undefinedReported.Contains))
            {
                result.Warnings.Add($"$({name}) is set by SSDT's publish; define {name} in bsm.yaml.");
            }
        }

        /// <summary>$(DatabaseName) becomes $(db); renamed variables are renamed; undeclared ones are reported.</summary>
        private string Prepare(string text, string file) => VariableRegex().Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            if (name.Equals("DatabaseName", StringComparison.OrdinalIgnoreCase))
            {
                return "$(db)";
            }

            if (variableNames.TryGetValue(name, out var mapped))
            {
                return $"$({mapped})";
            }

            if (ScriptPreprocessor.IsBuiltInName(name))
            {
                return m.Value;
            }

            if (undefinedReported.Add(name))
            {
                result.UndefinedVariables.Add(name);
                if (!PublishVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    result.Warnings.Add($"{file}: uses $({name}), which the project does not declare as a SqlCmdVariable; define it in bsm.yaml.");
                }
            }

            return m.Value;
        });

        private string Escape(string text, string file)
        {
            if (Regex.IsMatch(text, @"\{\{[A-Za-z_][A-Za-z0-9_.]*\}\}"))
            {
                result.Warnings.Add($"{file}: contains '{{{{name}}}}' text, which bsm renders as '{{name}}'. Check it, or start the script with '-- bsm:preprocess off'.");
            }

            return ScriptPreprocessor.EscapeLiteral(text, sqlCmdSyntax: false);
        }

        private string Qualified(SqlObjectName name) => $"{name.Schema ?? defaultSchema}.{name.Name}";

        private bool SameObject(SqlObjectName a, SqlObjectName b) =>
            string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Schema ?? defaultSchema, b.Schema ?? defaultSchema, StringComparison.OrdinalIgnoreCase);

        private string? Key(SqlObjectKind kind, SqlObjectName? target) => target is null ? null : kind switch
        {
            SqlObjectKind.Schema => "schema:" + target.Name.ToLowerInvariant(),
            SqlObjectKind.User or SqlObjectKind.Role => "principal:" + target.Name.ToLowerInvariant(),
            SqlObjectKind.PartitionFunction or SqlObjectKind.PartitionScheme => "partition:" + target.Name.ToLowerInvariant(),
            SqlObjectKind.Assembly => "assembly:" + target.Name.ToLowerInvariant(),
            >= SqlObjectKind.Index => null,
            _ => "object:" + Qualified(target).ToLowerInvariant(),
        };

        private string RefKey(SqlReference reference) => reference.Kind switch
        {
            SqlReferenceKind.Principal => "principal:",
            SqlReferenceKind.Schema => "schema:",
            SqlReferenceKind.Partition => "partition:",
            _ => "object:",
        } + (reference.Kind == SqlReferenceKind.Object ? Qualified(reference.Name) : reference.Name.Name).ToLowerInvariant();

        private void AddEdges(Unit unit)
        {
            var found = new Dictionary<Unit, bool>();
            void Add(Unit? to, bool soft)
            {
                if (to is not null && to != unit)
                {
                    found[to] = found.TryGetValue(to, out var existing) ? existing && soft : soft;
                }
            }

            if (unit.Target is { } target && unit.Kind is not (SqlObjectKind.Schema or SqlObjectKind.User or SqlObjectKind.Role or SqlObjectKind.PartitionFunction or SqlObjectKind.PartitionScheme or SqlObjectKind.Assembly))
            {
                Add(byKey.GetValueOrDefault("schema:" + (target.Schema ?? defaultSchema).ToLowerInvariant()), false);
            }

            foreach (var statement in unit.Statements)
            {
                var foreignKeys = statement.ForeignKeys.ToLookup(fk => RefKey(new SqlReference(SqlReferenceKind.Object, fk.References)));
                var movable = statement != unit.Primary && statement.Kind == SqlObjectKind.Constraint;
                foreach (var reference in statement.References)
                {
                    var key = RefKey(reference);
                    if (byKey.GetValueOrDefault(key) is not { } to || to == unit)
                    {
                        continue;
                    }

                    if (foreignKeys.Contains(key))
                    {
                        continue;
                    }

                    if (movable && to.Kind == SqlObjectKind.Table && reference.Kind == SqlReferenceKind.Object)
                    {
                        edges.Add(new Edge(unit, to, false, Statement: statement));
                        continue;
                    }

                    var soft = statement.Deferred && to.Kind is SqlObjectKind.Table or SqlObjectKind.View or SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.Synonym or SqlObjectKind.Sequence;
                    Add(to, soft);
                }

                foreach (var group in foreignKeys)
                {
                    if (byKey.GetValueOrDefault(group.Key) is { } to && to != unit)
                    {
                        edges.AddRange(group.Select(fk => new Edge(unit, to, false, ForeignKey: fk)));
                    }
                }
            }

            edges.AddRange(found.Select(f => new Edge(unit, f.Key, f.Value)));
        }

        /// <summary>
        /// Modules become repeatables unless a change needs them (repeatables run after every change), they carry an
        /// index (CREATE OR ALTER VIEW drops it), or a schema-bound module uses them (they could not be altered).
        /// </summary>
        private void Classify()
        {
            foreach (var unit in units.Where(u => !u.Unparsed))
            {
                unit.Repeatable = unit.Kind is SqlObjectKind.View or SqlObjectKind.Procedure or SqlObjectKind.Function or SqlObjectKind.Trigger or SqlObjectKind.Permission or SqlObjectKind.RoleMembership;
                if (unit.Kind == SqlObjectKind.View && unit.Statements.Any(s => s.Kind == SqlObjectKind.Index))
                {
                    unit.Repeatable = false;
                    result.Warnings.Add($"{unit.File}: {Describe(unit)} has an index, so it is a change (CREATE OR ALTER VIEW would drop the index).");
                }
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var edge in edges.Where(e => !e.Soft && e.To.Repeatable && e.To.Kind is not (SqlObjectKind.Permission or SqlObjectKind.RoleMembership)).ToList())
                {
                    string? reason = !edge.From.Repeatable ? $"{Describe(edge.From)} needs it and changes run before repeatables"
                        : edge.From.Primary.SchemaBound ? $"{Describe(edge.From)} uses it WITH SCHEMABINDING, so it cannot be altered in place"
                        : null;
                    if (reason is not null && edge.From.Kind is not (SqlObjectKind.Permission or SqlObjectKind.RoleMembership))
                    {
                        edge.To.Repeatable = false;
                        changed = true;
                        result.Warnings.Add($"{edge.To.File}: {Describe(edge.To)} is a change, not a repeatable: {reason}. Change it later with a new change.");
                    }
                }
            }
            while (changed);
        }

        /// <summary>Changes in dependency order; cycles are broken by moving foreign keys into later changes.</summary>
        private List<Unit> OrderChanges()
        {
            var changes = units.Where(u => !u.Repeatable).ToList();
            var foreignKeyUnits = new Dictionary<Unit, Unit>();
            while (true)
            {
                var cycles = StronglyConnected(changes, edges).Where(c => c.Count > 1).ToList();
                if (cycles.Count == 0)
                {
                    break;
                }

                foreach (var cycle in cycles)
                {
                    var members = cycle.ToHashSet();
                    var inner = edges.Where(e => members.Contains(e.From) && members.Contains(e.To)).ToList();
                    // One table's foreign keys at a time (the first in project order), then look again: moves as few as needed.
                    var movable = inner.Where(e => e.ForeignKey is not null || e.Statement is not null).GroupBy(e => e.From).OrderBy(g => g.Key.Order).FirstOrDefault()?.ToList() ?? [];
                    var names = string.Join(", ", cycle.OrderBy(u => u.Order).Select(Describe));
                    if (movable.Count > 0)
                    {
                        foreach (var edge in movable)
                        {
                            if (!foreignKeyUnits.TryGetValue(edge.From, out var fkUnit))
                            {
                                fkUnit = new Unit { File = edge.From.File, Text = string.Empty, Order = units.Count + foreignKeyUnits.Count, ForeignKeysOf = edge.From };
                                fkUnit.Statements.Add(new SqlStatementInfo { Kind = SqlObjectKind.Constraint, Target = edge.From.Target, Offset = 0, Length = 0, Batch = 0 });
                                foreignKeyUnits[edge.From] = fkUnit;
                                changes.Add(fkUnit);
                                edges.Add(new Edge(fkUnit, edge.From, false));
                            }

                            if (edge.ForeignKey is { } fk)
                            {
                                edge.From.Edits.Add(fk.Removal);
                                fkUnit.ForeignKeyDeploy.Add(fk.AddStatement);
                                fkUnit.ForeignKeyRevert.Insert(0, fk.DropStatement);
                            }
                            else
                            {
                                var statement = edge.Statement!;
                                if (edge.From.Statements.Remove(statement))
                                {
                                    edge.From.Sliced = true;
                                    fkUnit.ForeignKeyDeploy.Add(edge.From.Text.Substring(statement.Offset, statement.Length));
                                    fkUnit.ForeignKeyRevert.Insert(0, statement.Revert);
                                }
                            }

                            edges.Remove(edge);
                            edges.Add(new Edge(fkUnit, edge.To, false));
                        }

                        result.Warnings.Add($"foreign keys form a cycle between {names}: those of {Describe(movable[0].From)} moved to a separate change after the tables.");
                        continue;
                    }

                    var soft = inner.Where(e => e.Soft).ToList();
                    if (soft.Count > 0)
                    {
                        edges.RemoveAll(soft.Contains);
                        continue;
                    }

                    edges.RemoveAll(inner.Contains);
                    result.Warnings.Add($"dependency cycle between {names}: kept in project order; check the order in bsm.plan.");
                }
            }

            // Kahn's algorithm: ready changes go by kind (schemas, principals, types, tables, ...) then project order.
            var members2 = changes.ToHashSet();
            var dependsOn = changes.ToDictionary(u => u, u => edges.Where(e => e.From == u && members2.Contains(e.To)).Select(e => e.To).ToHashSet());
            var ready = new SortedSet<Unit>(Comparer<Unit>.Create((a, b) => (Rank(a), a.Order).CompareTo((Rank(b), b.Order))));
            var remaining = dependsOn.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            foreach (var unit in changes.Where(u => remaining[u] == 0))
            {
                ready.Add(unit);
            }

            var dependents = changes.ToDictionary(u => u, _ => new List<Unit>());
            foreach (var (unit, deps) in dependsOn)
            {
                foreach (var dep in deps)
                {
                    dependents[dep].Add(unit);
                }
            }

            var ordered = new List<Unit>();
            while (ready.Count > 0)
            {
                var next = ready.Min!;
                ready.Remove(next);
                ordered.Add(next);
                foreach (var dependent in dependents[next])
                {
                    if (--remaining[dependent] == 0)
                    {
                        ready.Add(dependent);
                    }
                }
            }

            // Every cycle was broken above; a change left over would silently go missing from the plan.
            if (ordered.Count != changes.Count)
            {
                var left = changes.Where(u => !ordered.Contains(u)).OrderBy(u => u.Order).Select(u => $"{Describe(u)} ({u.File})");
                throw new MigrationException($"Could not order these objects (a dependency cycle bsm could not break): {string.Join(", ", left)}. Please report this with the project.");
            }

            return ordered;
        }

        private static int Rank(Unit unit) => unit.ForeignKeysOf is not null || unit.Unparsed ? 100 : (int)unit.Kind;

        private void NameAndWriteChanges(List<Unit> ordered)
        {
            foreach (var unit in ordered)
            {
                unit.Name = result.UniqueName(ChangeName(unit), "change", null);
                var deploy = unit.ForeignKeysOf is not null ? string.Join("\n", unit.ForeignKeyDeploy) + "\n" : Assemble(unit, orAlter: false);
                var revert = Revert(unit);
                var flags = revert is null ? ChangeFlags.Irreversible : ChangeFlags.None;
                var members = ordered.ToHashSet();
                var requires = edges.Where(e => e.From == unit && members.Contains(e.To) && !e.To.Repeatable && e.To.Name.Length > 0)
                    .Select(e => e.To).Distinct().OrderBy(ordered.IndexOf).Select(u => u.Name).ToList();
                var note = unit.ForeignKeysOf is { } table ? $"{NotePrefix}{unit.File}: foreign keys of {Qualified(table.Target!)}" : $"{NotePrefix}{unit.File}: {Describe(unit)}";
                result.Changes.Add(new ImportedChange(unit.Name, Escape(deploy, unit.File), revert is null ? null : Escape(revert + "\n", unit.File), flags, note, unit.File) { Requires = requires });
            }
        }

        private string ChangeName(Unit unit)
        {
            if (unit.ForeignKeysOf is { } table)
            {
                return $"add-foreign-keys-{table.Target!.Schema ?? defaultSchema}-{table.Target.Name}";
            }

            var kind = KindWord(unit.Kind).Replace(' ', '-');
            return unit switch
            {
                { Unparsed: true } or { Target: null } => System.IO.Path.GetFileNameWithoutExtension(unit.File),
                { Kind: SqlObjectKind.Table } => $"create-{unit.Target.Schema ?? defaultSchema}-{unit.Target.Name}",
                { Kind: SqlObjectKind.Index or SqlObjectKind.Constraint or SqlObjectKind.FullText } when unit.Primary.ChildName is { } child => $"create-{kind}-{child}",
                { Kind: >= SqlObjectKind.Index } => $"{kind}-{unit.Target.Schema ?? defaultSchema}-{unit.Target.Name}",
                { Target.Schema: null } when Key(unit.Kind, unit.Target)?.StartsWith("object:", StringComparison.Ordinal) != true => $"create-{kind}-{unit.Target.Name}",
                _ => $"create-{kind}-{unit.Target.Schema ?? defaultSchema}-{unit.Target.Name}",
            };
        }

        private string Describe(Unit unit)
        {
            if (unit.Target is null)
            {
                return KindWord(unit.Kind) == "other" ? "script" : KindWord(unit.Kind);
            }

            var name = Key(unit.Kind, unit.Target)?.StartsWith("object:", StringComparison.Ordinal) == true || unit.Kind >= SqlObjectKind.Index ? Qualified(unit.Target) : unit.Target.Name;
            return unit.Primary.ChildName is { } child ? $"{KindWord(unit.Kind)} {child} on {name}" : $"{KindWord(unit.Kind)} {name}";
        }

        private static string KindWord(SqlObjectKind kind) => kind switch
        {
            SqlObjectKind.PartitionFunction => "partition function",
            SqlObjectKind.PartitionScheme => "partition scheme",
            SqlObjectKind.XmlSchemaCollection => "xml schema collection",
            SqlObjectKind.FullText => "fulltext",
            SqlObjectKind.RoleMembership => "role membership",
            SqlObjectKind.ExtendedProperty => "extended property",
            _ => kind.ToString().ToLowerInvariant(),
        };

        private static string? Revert(Unit unit)
        {
            if (unit.ForeignKeysOf is not null)
            {
                return unit.ForeignKeyRevert.Any(r => r is null) ? null : string.Join("\n", unit.ForeignKeyRevert);
            }

            if (unit.Unparsed)
            {
                return null;
            }

            if (unit.Kind < SqlObjectKind.Index)
            {
                // Dropping the object drops its indexes, constraints, permissions and properties too.
                return unit.Primary.Revert;
            }

            var reverts = unit.Statements.AsEnumerable().Reverse().Select(s => s.Revert).ToList();
            return reverts.Any(r => r is null) ? null : string.Join("\n", reverts);
        }

        /// <summary>The unit's script: the whole file when it is the only object in it, else its statements with GO between batches.</summary>
        private static string Assemble(Unit unit, bool orAlter)
        {
            var edits = unit.Edits.Concat(orAlter ? unit.Statements.Select(s => s.CreateOrAlter).OfType<SqlTextEdit>() : []).ToList();
            if (!unit.Sliced)
            {
                return Apply(unit.Text, 0, unit.Text.Length, edits).Trim('\n') + "\n";
            }

            var sb = new StringBuilder();
            int? batch = null;
            foreach (var statement in unit.Statements.OrderBy(s => s.Offset))
            {
                if (batch is not null)
                {
                    sb.Append(batch == statement.Batch ? "\n" : "\nGO\n");
                }

                sb.Append(Apply(unit.Text, statement.Offset, statement.Length, edits));
                batch = statement.Batch;
            }

            return sb.ToString().Trim('\n') + "\n";
        }

        private static string Apply(string text, int start, int length, List<SqlTextEdit> edits)
        {
            var slice = new StringBuilder(text.Substring(start, length));
            foreach (var edit in edits.Where(e => e.Offset >= start && e.Offset + e.Length <= start + length).OrderByDescending(e => e.Offset))
            {
                slice.Remove(edit.Offset - start, edit.Length).Insert(edit.Offset - start, edit.Replacement);
            }

            return slice.ToString();
        }

        private void WriteRepeatables()
        {
            var repeatables = units.Where(u => u.Repeatable).ToList();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var unit in repeatables.OrderBy(u => u.Order))
            {
                var folder = unit.Kind switch
                {
                    SqlObjectKind.Function => "functions",
                    SqlObjectKind.View => "views",
                    SqlObjectKind.Procedure => "procedures",
                    SqlObjectKind.Trigger => "triggers",
                    _ => "permissions",
                };
                var stem = unit.Target is null || unit.Kind is SqlObjectKind.Permission or SqlObjectKind.RoleMembership
                    ? System.IO.Path.GetFileNameWithoutExtension(unit.File)
                    : unit.Target.Schema is null && unit.Kind == SqlObjectKind.Trigger ? unit.Target.Name : Qualified(unit.Target);
                var name = $"{folder}/{Sanitize(stem)}.sql";
                for (var n = 2; !used.Add(name); n++)
                {
                    name = $"{folder}/{Sanitize(stem)}-{n}.sql";
                }

                unit.Name = name;
            }

            // Cycles between repeatables (procedures calling each other) are broken at deferred references.
            while (StronglyConnected(repeatables, edges).FirstOrDefault(c => c.Count > 1) is { } cycle)
            {
                var members = cycle.ToHashSet();
                var inner = edges.Where(e => members.Contains(e.From) && members.Contains(e.To)).ToList();
                var soft = inner.Where(e => e.Soft).ToList();
                edges.RemoveAll((soft.Count > 0 ? soft : inner).Contains);
                if (soft.Count == 0)
                {
                    result.Warnings.Add($"repeatables {string.Join(", ", cycle.Select(u => u.Name).Order(StringComparer.Ordinal))} reference each other; their bsm:requires lines leave the cycle out.");
                }
            }

            foreach (var unit in repeatables.OrderBy(u => u.Order))
            {
                var requires = edges.Where(e => e.From == unit && e.To.Repeatable).Select(e => e.To.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (unit.Statements.Any(s => s.Kind == SqlObjectKind.ExtendedProperty))
                {
                    result.Warnings.Add($"{unit.File}: sp_addextendedproperty in repeatable/{unit.Name} fails when it runs again (the property exists); move it to a change, or guard it.");
                }

                // The header gets its own batch: SQL Server keeps comments before CREATE in the module's definition.
                var script = (requires.Count > 0 ? $"-- bsm:requires {string.Join(' ', requires)}\nGO\n" : string.Empty) + Assemble(unit, orAlter: true);
                result.Repeatables.Add(new ImportedRepeatable(unit.Name, Escape(script, unit.File), unit.File));
            }
        }

        /// <summary>Tarjan's strongly connected components of <paramref name="nodes"/> over edges between them.</summary>
        private static List<List<Unit>> StronglyConnected(List<Unit> nodes, List<Edge> edges)
        {
            var set = nodes.ToHashSet();
            var adjacency = nodes.ToDictionary(n => n, n => edges.Where(e => e.From == n && set.Contains(e.To)).Select(e => e.To).Distinct().ToList());
            var index = new Dictionary<Unit, int>();
            var low = new Dictionary<Unit, int>();
            var stack = new Stack<Unit>();
            var onStack = new HashSet<Unit>();
            var components = new List<List<Unit>>();
            var counter = 0;

            void Visit(Unit node)
            {
                index[node] = low[node] = counter++;
                stack.Push(node);
                onStack.Add(node);
                foreach (var next in adjacency[node])
                {
                    if (!index.ContainsKey(next))
                    {
                        Visit(next);
                        low[node] = Math.Min(low[node], low[next]);
                    }
                    else if (onStack.Contains(next))
                    {
                        low[node] = Math.Min(low[node], index[next]);
                    }
                }

                if (low[node] == index[node])
                {
                    var component = new List<Unit>();
                    Unit member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (member != node);
                    components.Add(component);
                }
            }

            foreach (var node in nodes.Where(n => !index.ContainsKey(n)))
            {
                Visit(node);
            }

            return components;
        }
    }

    private static string Sanitize(string name) => Regex.Replace(name, @"[^A-Za-z0-9._-]", "_");
}
