using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Conversion.PgToTSql;
using Badger.SqlMigrate.Core.Conversion;

namespace Badger.SqlMigrate.Conversion.Sqlite;

/// <summary>
/// PostgreSQL to SQLite. Expressions and views are printed as SQLite. SQLite has no stored functions or procedures,
/// so those are written commented out. A trigger becomes one SQLite trigger per event: SQLite trigger bodies hold only
/// SQL statements, so the PL/pgSQL is flattened: IF branches become WHERE guards on each statement, variables are
/// inlined, RAISE EXCEPTION becomes SELECT RAISE(ABORT, ...), RETURN stops later statements with a guard, NEW.col := x
/// becomes an UPDATE of the row (after the write, as SQLite cannot change NEW), and a statement-level trigger's
/// transition tables become a one-row SELECT of NEW / OLD.
/// </summary>
internal sealed class PgToSqliteTranslator(ConversionContext ctx) : ITranslator
{
    public string Expression(string expression, ExpressionSite site) => Translate(expression, site.Owner, "NULL");

    public string Predicate(string predicate, ExpressionSite site) => Translate(predicate, site.Owner, "true");

    private string Translate(string text, string owner, string placeholder)
    {
        var issues = new List<string>();
        string result;
        try
        {
            result = new SqlPrinter(ctx, issues).Expr(new PgParser(text).ExpressionOnly());
        }
        catch (PgParseException ex)
        {
            issues.Add($"could not parse {text}: {ex.Message}");
            result = placeholder + " " + Review.Comment("could not translate", text);
        }

        foreach (var issue in issues.Distinct())
        {
            ctx.Issue(owner, issue);
        }

        return result;
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var issues = new List<string>();
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        string script;
        try
        {
            script = code.Kind switch
            {
                PortableCodeKind.View => View(code, issues),
                PortableCodeKind.Trigger => Trigger(code, all, issues),
                _ => Routine(code, issues),
            };
        }
        catch (PgParseException ex)
        {
            issues.Add($"could not parse: {ex.Message}");
            script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for SQLite"));
        }

        if (script.Length == 0)
        {
            return new TranslatedCode(code, string.Empty, string.Empty, string.Empty, []);
        }

        issues = issues.Distinct().ToList();
        foreach (var issue in issues)
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        return new TranslatedCode(code, folder, $"{ctx.TargetName(code.Name).Name}.sql", SqliteToPgTranslator.Header("PostgreSQL", code, issues) + script.TrimEnd() + "\n", issues);
    }

    private string View(PortableCode code, List<string> issues)
    {
        var m = Regex.Match(code.Definition, @"\ACREATE\s+(?:OR\s+REPLACE\s+)?(?<mat>MATERIALIZED\s+)?VIEW\s+(?<name>(?:""[^""]*""|[^\s(])+)\s*(?:\((?<cols>[^)]*)\))?\s+AS\s*\n?", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var query = new PgParser(m.Success ? code.Definition[(m.Index + m.Length)..] : code.Definition).QueryOnly();
        if (m.Success && m.Groups["mat"].Success)
        {
            issues.Add("materialized view became a plain view (SQLite has no materialized views)");
        }

        var name = ctx.Qualified(code.Name);
        var columns = m.Success && m.Groups["cols"].Success
            ? " (" + string.Join(", ", m.Groups["cols"].Value.Split(',').Select(c => ctx.Ident(c.Trim().Trim('"')))) + ")"
            : string.Empty;
        return $"DROP VIEW IF EXISTS {name};\nCREATE VIEW {name}{columns} AS\n{new SqlPrinter(ctx, issues).Query(query)};\n";
    }

    private static string Routine(PortableCode code, List<string> issues)
    {
        var header = new PgParser(code.Definition).RoutineHeader();
        if (header.ReturnsTrigger)
        {
            return string.Empty;
        }

        issues.Add($"SQLite has no stored {(code.Kind == PortableCodeKind.Procedure ? "procedures" : "functions")}: move this logic into the application, a view, or a trigger");
        return Review.CommentedOut(code.Definition, Review.Comment($"SQLite has no stored {(code.Kind == PortableCodeKind.Procedure ? "procedures" : "functions")}"));
    }

    // ------------------------------------------------------------------ triggers

    private string Trigger(PortableCode code, IReadOnlyList<PortableCode> all, List<string> issues)
    {
        var definition = PgTriggerDefinition.Parse(code.Definition);
        var function = code.TriggerFunction is { } fn ? all.FirstOrDefault(c => c.Kind == PortableCodeKind.Function && c.Name.Key == fn.Key) : null;
        if (definition is null || function is null || code.Table is not { } owner)
        {
            issues.Add("trigger not translated: its definition or function could not be read");
            return Review.CommentedOut(code.Definition, Review.Comment("trigger not translated"));
        }

        var header = new PgParser(function.Definition).RoutineHeader();
        if (header.Language != "plpgsql")
        {
            issues.Add($"trigger function in {header.Language} is not translated");
            return Review.CommentedOut(code.Definition + "\n\n" + function.Definition, Review.Comment("trigger not translated"));
        }

        if (definition.Arguments.Length > 0)
        {
            issues.Add($"trigger arguments ({definition.Arguments}) are not passed: TG_ARGV is not translated");
        }

        var body = new PgParser(header.Body).PlpgsqlBody();
        var table = ctx.Schema.Tables.FirstOrDefault(t => t.Name.Key == owner.Key);
        var columns = table?.Columns.Select(c => c.Name).ToList() ?? [];
        var tableName = ctx.Qualified(owner);
        var sb = new StringBuilder();
        foreach (var ev in definition.Events.Where(e => e is "INSERT" or "UPDATE" or "DELETE"))
        {
            var name = ctx.Quote(ctx.Names.Name(definition.Events.Count == 1 ? definition.Name : $"{definition.Name}_{ev.ToLowerInvariant()[..3]}"));
            var printer = new SqlPrinter(ctx, issues);
            printer.NameReplacements["tg_op"] = $"'{ev}'";
            printer.NameReplacements["tg_table_name"] = $"'{ctx.TargetName(owner).Name}'";
            printer.NameReplacements["tg_when"] = $"'{definition.When}'";
            printer.NameReplacements["tg_name"] = $"'{definition.Name}'";
            printer.NameReplacements["tg_level"] = "'ROW'";
            var hasNew = ev is "INSERT" or "UPDATE";
            var hasOld = ev is "UPDATE" or "DELETE";
            foreach (var c in columns)
            {
                if (!hasNew)
                {
                    printer.NameReplacements["new." + c] = "NULL";
                }

                if (!hasOld)
                {
                    printer.NameReplacements["old." + c] = "NULL";
                }
            }

            if (!definition.Row)
            {
                // A statement-level trigger runs once per row in SQLite: its transition tables become that row.
                issues.Add("statement-level trigger became a row-level trigger (SQLite has only row triggers): it runs once per row, over that row");
                foreach (var (alias, which) in definition.TransitionTables)
                {
                    var present = which == "new" ? hasNew : hasOld;
                    var row = string.Join(", ", columns.Select(c => $"{which.ToUpperInvariant()}.{ctx.Ident(c)} AS {ctx.Ident(c)}"));
                    printer.TableReplacements[alias] = present && columns.Count > 0 ? $"(SELECT {row})" : $"(SELECT {(columns.Count == 0 ? "NULL" : string.Join(", ", columns.Select(c => "NULL AS " + ctx.Ident(c))))} WHERE 0)";
                }
            }

            var flattener = new Flattener(ctx, printer, issues, definition.When, tableName, table);
            flattener.Block(body, null);
            var timing = definition.When;
            if (flattener.WritesNew)
            {
                if (timing == "BEFORE")
                {
                    issues.Add("BEFORE trigger that changes NEW became an AFTER trigger that updates the row (SQLite cannot change NEW)");
                }

                timing = "AFTER";
            }

            var events = ev + (ev == "UPDATE" && definition.UpdateOf.Count > 0 ? " OF " + string.Join(", ", definition.UpdateOf.Select(ctx.Ident)) : string.Empty);
            var when = definition.Condition is null ? string.Empty : $" WHEN {printer.Expr(new PgParser(definition.Condition).ExpressionOnly())}";
            sb.Append($"DROP TRIGGER IF EXISTS {name};\n");
            sb.Append($"CREATE TRIGGER {name} {timing} {events} ON {tableName} FOR EACH ROW{when}\nBEGIN\n");
            foreach (var statement in flattener.Statements.DefaultIfEmpty("SELECT NULL;"))
            {
                sb.Append("    ").Append(statement.Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
            }

            sb.Append("END;\n\n");
        }

        return sb.ToString();
    }

    /// <summary>Turns a PL/pgSQL body into a flat list of guarded SQLite statements.</summary>
    private sealed class Flattener(ConversionContext ctx, SqlPrinter printer, List<string> issues, string timing, string table, PortableTable? source)
    {
        private readonly List<string> returned = [];
        private bool stopped;

        public List<string> Statements { get; } = [];

        public bool WritesNew { get; private set; }

        private static string And(params string?[] parts)
        {
            var list = parts.Where(p => p is not null).ToList();
            return list.Count == 0 ? string.Empty : string.Join(" AND ", list);
        }

        /// <summary>The guard for a statement here: the enclosing branches, and no earlier RETURN taken.</summary>
        private string? Effective(string? guard)
        {
            var g = And([guard, .. returned.Select(r => $"NOT ({r})")]);
            return g.Length == 0 ? null : g;
        }

        public void Block(PgBlock block, string? guard)
        {
            foreach (var d in block.Declarations)
            {
                printer.NameReplacements[d.Name] = d.Default is null ? "NULL" : "(" + printer.Expr(d.Default) + ")";
                if (d.Special is not null)
                {
                    issues.Add($"declaration {d.Name} ({d.Special}) is not translated");
                }
            }

            if (block.Handlers.Count > 0)
            {
                issues.Add("EXCEPTION handlers are not translated: SQLite triggers stop at the first error");
            }

            List(block.Body, guard);
        }

        private void List(IEnumerable<PgStatement> statements, string? guard)
        {
            foreach (var s in statements)
            {
                if (stopped)
                {
                    return;
                }

                Statement(s, guard);
            }
        }

        private void Statement(PgStatement s, string? guard)
        {
            var g = Effective(guard);
            switch (s)
            {
                case PgBlock b:
                    Block(b, guard);
                    break;

                case PgIf i:
                {
                    var previous = new List<string>();
                    foreach (var (condition, body) in i.Branches)
                    {
                        var c = $"({printer.Expr(condition)}) IS TRUE";
                        List(body, And([guard, .. previous.Select(p => $"NOT {p}"), c]));
                        previous.Add(c);
                    }

                    if (i.Else is not null)
                    {
                        List(i.Else, And([guard, .. previous.Select(p => $"NOT {p}")]));
                    }

                    break;
                }

                case PgAssign { Target: PgName { Parts: [var record, var column] } } a when record.Equals("new", StringComparison.OrdinalIgnoreCase):
                {
                    var value = printer.Expr(a.Value);
                    WritesNew = true;
                    var key = source?.PrimaryKey?.Columns is { Count: > 0 } pk
                        ? string.Join(" AND ", pk.Select(k => $"{ctx.Ident(k)} = NEW.{ctx.Ident(k)}"))
                        : "rowid = NEW.rowid";
                    Statements.Add($"UPDATE {table} SET {ctx.Ident(column)} = {value} WHERE {And(key, g)};");
                    printer.NameReplacements[$"new.{column}"] = g is null ? $"({value})" : $"(CASE WHEN {g} THEN {value} ELSE NEW.{ctx.Ident(column)} END)";
                    break;
                }

                case PgAssign { Target: PgName { Parts: [var variable] } } a:
                {
                    var value = printer.Expr(a.Value);
                    var previous = printer.NameReplacements.GetValueOrDefault(variable, "NULL");
                    printer.NameReplacements[variable] = g is null ? $"({value})" : $"(CASE WHEN {g} THEN {value} ELSE {previous} END)";
                    break;
                }

                case PgGetDiagnostics d:
                    foreach (var (target, item) in d.Items)
                    {
                        if (target is PgName { Parts: [var variable] } && item.Equals("row_count", StringComparison.OrdinalIgnoreCase))
                        {
                            printer.NameReplacements[variable] = "changes()";
                        }
                    }

                    break;

                case PgSelectStatement { Query.Body: PgSelectCore { Into.Count: > 0 } core } sel:
                {
                    // SELECT x INTO v FROM ...: v becomes a scalar subquery.
                    for (var n = 0; n < core.Into.Count && n < core.Targets.Count; n++)
                    {
                        if (core.Into[n] is PgName { Parts: [var variable] })
                        {
                            var q = sel.Query with { Body = core with { Into = [], Targets = [core.Targets[n]] }, Limit = new PgLiteral(LiteralKind.Number, "1") };
                            printer.NameReplacements[variable] = "(" + printer.Query(q) + ")";
                        }
                    }

                    break;
                }

                case PgRaise r when r.Level == "exception":
                {
                    var message = r.Format is PgLiteral { Kind: LiteralKind.String } f ? f.Value : r.Using.TryGetValue("message", out var m) && m is PgLiteral ml ? ml.Value : "error";
                    if (r.Args.Count > 0 || message.Contains('%', StringComparison.Ordinal))
                    {
                        issues.Add("RAISE with values: SQLite's RAISE takes a fixed message; the values were left out");
                        message = message.Replace("%", "?", StringComparison.Ordinal);
                    }

                    Statements.Add($"SELECT RAISE(ABORT, {Lit(message)}){(g is null ? string.Empty : " WHERE " + g)};");
                    if (g is null)
                    {
                        stopped = true;
                    }

                    break;
                }

                case PgRaise:
                    issues.Add("RAISE NOTICE / WARNING has no SQLite counterpart; dropped");
                    break;

                case PgReturn ret:
                {
                    if (timing == "BEFORE" && ret.Value is PgLiteral { Kind: LiteralKind.Null })
                    {
                        // RETURN NULL in a BEFORE trigger skips the row.
                        Statements.Add($"SELECT RAISE(IGNORE){(g is null ? string.Empty : " WHERE " + g)};");
                    }

                    if (guard is null)
                    {
                        stopped = true;
                    }
                    else
                    {
                        returned.Add(guard);
                    }

                    break;
                }

                case PgInsert ins:
                    Statements.Add(printer.Insert(ins, g));
                    break;

                case PgUpdate upd:
                    Statements.Add(printer.Update(upd, g));
                    break;

                case PgDelete del:
                    Statements.Add(printer.Delete(del, g));
                    break;

                case PgNull:
                case PgPerform:
                    break;

                default:
                    issues.Add($"not translated: {Regex.Replace(s.Source, @"\s+", " ").Trim()}");
                    Statements.Add(Review.Comment("not translated", s.Source));
                    break;
            }
        }

        private static string Lit(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
