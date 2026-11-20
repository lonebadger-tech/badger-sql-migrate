using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.PgToTSql;

internal sealed partial class PgToTSqlTranslator
{
    private const string RowCountMarker = "\u0001ROWCOUNT\u0001";

    private sealed class Writer
    {
        private readonly List<string> lines = [];
        private int depth = 1;

        public int Count => lines.Count;

        public void Line(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                lines.Add(new string(' ', depth * 4) + line);
            }
        }

        public void Indent() => depth++;

        public void Outdent() => depth--;

        public string Render(bool rowCount)
        {
            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.Contains(RowCountMarker, StringComparison.Ordinal))
                {
                    if (rowCount)
                    {
                        sb.Append(line.Replace(RowCountMarker, "SET @trek_rowcount = @@ROWCOUNT;", StringComparison.Ordinal)).Append('\n');
                    }

                    continue;
                }

                sb.Append(line).Append('\n');
            }

            return sb.ToString();
        }
    }

    /// <summary>An enclosing loop: its label, what CONTINUE must do first, and the label EXIT jumps to for outer loops.</summary>
    private sealed record LoopFrame(string? Label, string? ContinuePrelude, string ExitLabel);

    private readonly List<LoopFrame> loops = [];
    private string? resultVariable;
    private string? outputPrefix;

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        scope = new Scope(code.Name.ToString());
        loops.Clear();
        resultVariable = null;
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        var fileName = $"{ctx.Names.Schema(code.Name.Schema)}.{ctx.Names.Name(code.Name.Name)}.sql";
        string script;
        try
        {
            script = code.Kind switch
            {
                PortableCodeKind.View => View(code),
                PortableCodeKind.Trigger => Trigger(code, all),
                _ => Routine(code, ref folder),
            };
        }
        catch (PgParseException ex)
        {
            scope.Issues.Add($"could not parse: {ex.Message}");
            script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for SQL Server"));
        }

        if (script.Length == 0)
        {
            return new TranslatedCode(code, string.Empty, string.Empty, string.Empty, []);
        }

        var issues = scope.Issues.Distinct().ToList();
        foreach (var issue in issues)
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        var header = new StringBuilder($"-- Converted by trek convert from PostgreSQL {code.Kind.ToString().ToLowerInvariant()} {code.Name}.");
        header.Append(issues.Count == 0 ? " Check it before deploying.\n" : $" {issues.Count} item(s) to review (search for '{Review.Marker}').\n");
        foreach (var issue in issues)
        {
            header.Append("-- * ").Append(issue.Replace('\n', ' ')).Append('\n');
        }

        return new TranslatedCode(code, folder, fileName, header + script.TrimEnd() + "\n", issues);
    }

    // ------------------------------------------------------------------ views

    private string View(PortableCode code)
    {
        var m = Regex.Match(code.Definition, @"\AVIEW\s|\ACREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\s+.+?\s+AS\s*\n?", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var queryText = m.Success ? code.Definition[(m.Index + m.Length)..] : code.Definition;
        var query = new PgParser(queryText).QueryOnly();
        if (code.Materialized)
        {
            scope.Issues.Add("materialized view became a plain view; use an indexed view or a table refreshed by a job if it must be stored");
        }

        scope.Kind = "view";
        return $"CREATE OR ALTER VIEW {ctx.Qualified(code.Name)}\nAS\n{Query(query, QueryContext.Subquery)};\n";
    }

    // ------------------------------------------------------------------ routines

    private string Routine(PortableCode code, ref string folder)
    {
        var header = new PgParser(code.Definition).RoutineHeader();
        if (header.ReturnsTrigger)
        {
            // Written into each trigger that uses it.
            return string.Empty;
        }

        var target = ctx.Qualified(code.Name);
        var parameters = new List<string>();
        var anyOut = false;
        for (var i = 0; i < header.Args.Count; i++)
        {
            var arg = header.Args[i];
            var name = "@" + SafeVar(ctx.Names.Name(arg.Name ?? $"p{i + 1}"));
            var type = TypeMapper.PgToTSql(arg.Type, ctx).Type;
            scope.Positional.Add(name);
            if (arg.Name is not null)
            {
                scope.Variables[arg.Name] = (name, type);
            }

            var output = arg.Mode is "out" or "inout";
            anyOut |= output;
            parameters.Add($"{name} {type}{(arg.Default is null ? string.Empty : " = " + Value(arg.Default))}{(output ? " OUTPUT" : string.Empty)}");
        }

        List<PgStatement> statements;
        if (header.Language == "plpgsql")
        {
            statements = [new PgParser(header.Body).PlpgsqlBody()];
        }
        else if (header.Language == "sql")
        {
            statements = new PgParser(header.Body).SqlBody();
        }
        else
        {
            scope.Issues.Add($"LANGUAGE {header.Language} is not translated");
            return Review.CommentedOut(code.Definition, Review.Comment("not translated"));
        }

        var returnsSet = header.ReturnsTable.Count > 0 || header.SetOf;
        var writes = Writes(statements);
        var procedure = header.Procedure || header.Returns is "void" || anyOut || writes;
        if (procedure && !header.Procedure && header.Returns is not ("void" or null))
        {
            scope.Issues.Add(writes
                ? "this function changes data, which a SQL Server function cannot: it became a procedure; callers must EXEC it"
                : "this function has OUT parameters: it became a procedure; callers must EXEC it");
            if (!returnsSet)
            {
                resultVariable = "@result";
                parameters.Add($"@result {TypeMapper.PgToTSql(header.Returns, ctx).Type} OUTPUT");
            }
        }

        if (procedure)
        {
            folder = "procedures";
            scope.Kind = "procedure";
            var w = new Writer();
            w.Line("SET NOCOUNT ON;");
            Statements(statements, w);
            return $"CREATE OR ALTER PROCEDURE {target}{(parameters.Count == 0 ? string.Empty : "\n    " + string.Join(",\n    ", parameters))}\nAS\nBEGIN\n{Declarations()}{w.Render(scope.UsesRowCount)}END;\n";
        }

        var signature = $"CREATE OR ALTER FUNCTION {target}({string.Join(", ", parameters)})";
        if (returnsSet)
        {
            var columns = ReturnColumns(header, code);
            if (header.Language == "sql" && statements is [PgSelectStatement only])
            {
                scope.Kind = "inline";
                return $"{signature}\nRETURNS TABLE\nAS\nRETURN\n(\n{Query(only.Query, QueryContext.Subquery)}\n);\n";
            }

            scope.Kind = "tvf";
            resultVariable = "@result";
            var w = new Writer();
            Statements(statements, w);
            w.Line("RETURN;");
            return $"{signature}\nRETURNS @result TABLE ({columns})\nAS\nBEGIN\n{Declarations()}{w.Render(scope.UsesRowCount)}END;\n";
        }

        scope.Kind = "scalar";
        var returnType = TypeMapper.PgToTSql(header.Returns ?? "text", ctx).Type;
        if (header.Language == "sql" && statements is [PgSelectStatement single])
        {
            var value = single.Query.Body is PgSelectCore { From.Count: 0, Where: null, Targets: [var t] } ? Value(t.Expr) : "(" + Query(single.Query, QueryContext.Subquery) + ")";
            return $"{signature}\nRETURNS {returnType}\nAS\nBEGIN\n    RETURN {value};\nEND;\n";
        }

        var body = new Writer();
        Statements(statements, body);
        body.Line("RETURN NULL;");
        return $"{signature}\nRETURNS {returnType}\nAS\nBEGIN\n{Declarations()}{body.Render(scope.UsesRowCount)}END;\n";
    }

    private string Declarations()
    {
        var list = new List<string>(scope.Declarations);
        if (scope.UsesRowCount)
        {
            list.Insert(0, "DECLARE @trek_rowcount bigint = 0;");
        }

        return string.Concat(list.Distinct().Select(d => "    " + d + "\n"));
    }

    private string ReturnColumns(RoutineHeader header, PortableCode code)
    {
        if (header.ReturnsTable.Count > 0)
        {
            return string.Join(", ", header.ReturnsTable.Select(c => $"{Q(ctx.Names.Name(c.Name))} {TypeMapper.PgToTSql(c.Type, ctx).Type}"));
        }

        if (header.Returns is { } type && FindTable(type) is { } table)
        {
            return string.Join(", ", table.Columns.Select(c => $"{Q(ctx.Names.Name(c.Name))} {TypeMapper.PgToTSql(c.Type, ctx).Type}"));
        }

        if (header.Returns is "record")
        {
            scope.Issues.Add("SETOF record: the result columns are unknown; declare them");
            return "[value] sql_variant";
        }

        return $"{Q(ctx.Names.Name(code.Name.Name))} {TypeMapper.PgToTSql(header.Returns ?? "text", ctx).Type}";
    }

    private PortableTable? FindTable(string qualified)
    {
        var parts = qualified.Split('.').Select(p => p.Trim('"')).ToList();
        var schema = parts.Count > 1 ? parts[^2] : ctx.Schema.DefaultSchema;
        return ctx.Schema.Tables.FirstOrDefault(t => t.Name.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) && t.Name.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
    }

    private PortableTable? FindTable(PgName name) => FindTable(string.Join('.', name.Parts));

    private static bool Writes(IEnumerable<PgStatement> statements) => statements.Any(s => s switch
    {
        PgInsert or PgUpdate or PgDelete or PgExecute or PgCallStatement => true,
        PgRaise { Level: "exception" } => true,
        PgBlock b => Writes(b.Body) || b.Handlers.Any(h => Writes(h.Body)),
        PgIf i => i.Branches.Any(b => Writes(b.Body)) || (i.Else is not null && Writes(i.Else)),
        PgLoop l => Writes(l.Body),
        PgWhile w => Writes(w.Body),
        PgForRange f => Writes(f.Body),
        PgForQuery f => Writes(f.Body),
        PgSelectStatement { Query.With: var with } => with.Any(c => c.Body is not PgSelectStatement),
        _ => false,
    });

    // ------------------------------------------------------------------ statements

    private void Statements(IEnumerable<PgStatement> statements, Writer w)
    {
        foreach (var s in statements)
        {
            Statement(s, w);
        }
    }

    /// <summary>A BEGIN ... END block that is never empty (T-SQL rejects empty blocks).</summary>
    private void Block(IEnumerable<PgStatement> statements, Writer w)
    {
        w.Line("BEGIN");
        w.Indent();
        var before = w.Count;
        Statements(statements, w);
        if (w.Count == before)
        {
            w.Line("DECLARE @trek_noop bit;");
        }

        w.Outdent();
        w.Line("END");
    }

    private void Statement(PgStatement s, Writer w)
    {
        switch (s)
        {
            case PgBlock b:
                foreach (var d in b.Declarations)
                {
                    Declare(d, w);
                }

                if (b.Handlers.Count == 0)
                {
                    Statements(b.Body, w);
                    break;
                }

                scope.Issues.Add("EXCEPTION block became TRY/CATCH: PostgreSQL undid the block's changes when it caught the error, SQL Server does not; use a transaction savepoint if that matters");
                w.Line("BEGIN TRY");
                w.Indent();
                Statements(b.Body, w);
                w.Outdent();
                w.Line("END TRY");
                w.Line("BEGIN CATCH");
                w.Indent();
                Handlers(b.Handlers, w);
                w.Outdent();
                w.Line("END CATCH;");
                break;

            case PgAssign a:
                Assign(a, w);
                break;

            case PgSelectStatement sel:
                SelectStatement(sel, w);
                break;

            case PgInsert i:
                Insert(i, w);
                break;

            case PgUpdate u:
                Update(u, w);
                break;

            case PgDelete d:
                Delete(d, w);
                break;

            case PgIf i:
                for (var n = 0; n < i.Branches.Count; n++)
                {
                    w.Line($"{(n == 0 ? "IF" : "ELSE IF")} {Pred(i.Branches[n].Condition)}");
                    Block(i.Branches[n].Body, w);
                }

                if (i.Else is not null)
                {
                    w.Line("ELSE");
                    Block(i.Else, w);
                }

                break;

            case PgLoop l:
                Loop(l.Label, null, "1 = 1", l.Body, w, null);
                break;

            case PgWhile wh:
                Loop(wh.Label, null, Pred(wh.Condition), wh.Body, w, null);
                break;

            case PgForRange f:
            {
                var variable = LoopVariable(f.Variable, "int");
                var step = f.Step is null ? "1" : Value(f.Step);
                w.Line($"SET {variable} = {Value(f.From)};");
                var increment = $"SET {variable} = {variable} {(f.Reverse ? "-" : "+")} {step};";
                Loop(f.Label, increment, $"{variable} {(f.Reverse ? ">=" : "<=")} {Value(f.To)}", f.Body, w, increment);
                break;
            }

            case PgForQuery f:
                ForQuery(f, w);
                break;

            case PgExit e:
                Exit(e, w);
                break;

            case PgReturn r:
                Return(r, w);
                break;

            case PgReturnQuery rq:
                if (resultVariable is not null && scope.Kind == "tvf")
                {
                    w.Line($"INSERT INTO @result {Query(rq.Query, QueryContext.Statement)};");
                }
                else
                {
                    w.Line(Query(rq.Query, QueryContext.Statement) + ";");
                    scope.Issues.Add("RETURN QUERY became a result set returned by the procedure");
                }

                break;

            case PgReturnNext { Value: { } next } when scope.Kind == "tvf" && !(next is PgName { Parts.Count: 1 } r && scope.Records.ContainsKey(r.Last)):
                w.Line($"INSERT INTO @result VALUES ({Value(next)});");
                break;

            case PgReturnNext:
                w.Line(Review.Comment("RETURN NEXT not translated: insert the row into @result", s.Source));
                scope.Issues.Add("RETURN NEXT is not translated");
                break;

            case PgRaise r:
                Raise(r, w);
                break;

            case PgPerform p:
                Perform(p, w);
                break;

            case PgExecute e:
                w.Line($"EXEC sp_executesql {Value(e.Sql)};" + Note("dynamic SQL: the statement text is still PostgreSQL" + (e.Into.Count > 0 || e.Using.Count > 0 ? ", and INTO / USING are not passed" : string.Empty)));
                break;

            case PgGetDiagnostics g:
                foreach (var (target, item) in g.Items)
                {
                    if (item.Equals("row_count", StringComparison.OrdinalIgnoreCase))
                    {
                        scope.UsesRowCount = true;
                        w.Line($"SET {Value(target)} = @trek_rowcount;");
                    }
                    else
                    {
                        w.Line(Review.Comment($"GET DIAGNOSTICS {item} not translated"));
                        scope.Issues.Add($"GET DIAGNOSTICS {item} is not translated");
                    }
                }

                break;

            case PgCallStatement c:
                w.Line(Exec(c.Call));
                break;

            case PgNull:
                break;

            case PgRaw raw:
                w.Line(Review.Comment($"not translated ({raw.Why})", s.Source));
                scope.Issues.Add($"not translated ({raw.Why}): {Collapse(s.Source)}");
                break;

            default:
                w.Line(Review.Comment("not translated", s.Source));
                scope.Issues.Add($"not translated: {Collapse(s.Source)}");
                break;
        }
    }

    private void Handlers(IReadOnlyList<PgHandler> handlers, Writer w)
    {
        static string? Numbers(string condition) => condition.ToLowerInvariant() switch
        {
            "unique_violation" => "2601, 2627",
            "foreign_key_violation" or "check_violation" => "547",
            "not_null_violation" => "515",
            "division_by_zero" => "8134",
            "deadlock_detected" => "1205",
            "lock_not_available" => "1222",
            _ => null,
        };

        if (handlers is [{ Conditions: ["others"] } only])
        {
            Statements(only.Body, w);
            return;
        }

        var first = true;
        foreach (var handler in handlers)
        {
            if (handler.Conditions.Contains("others", StringComparer.OrdinalIgnoreCase))
            {
                w.Line("ELSE");
                Block(handler.Body, w);
                return;
            }

            var numbers = handler.Conditions.Select(Numbers).ToList();
            if (numbers.Any(n => n is null))
            {
                scope.Issues.Add($"exception condition {string.Join(" OR ", handler.Conditions)} has no SQL Server error number; check the CATCH");
            }

            w.Line($"{(first ? "IF" : "ELSE IF")} ERROR_NUMBER() IN ({string.Join(", ", numbers.Where(n => n is not null))}{(numbers.All(n => n is null) ? "0" : string.Empty)})");
            Block(handler.Body, w);
            first = false;
        }

        w.Line("ELSE");
        w.Line("    THROW;");
    }

    private void Declare(PgDeclaration d, Writer w)
    {
        var name = "@" + SafeVar(ctx.Names.Name(d.Name));
        if (d.Special is { } special)
        {
            if (special.StartsWith("alias ", StringComparison.Ordinal))
            {
                var target = special[6..];
                if (target.StartsWith('$') && int.TryParse(target[1..], out var index) && index <= scope.Positional.Count)
                {
                    scope.Variables[d.Name] = (scope.Positional[index - 1], "sql_variant");
                }

                return;
            }

            if (special == "record")
            {
                scope.Records[d.Name] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            if (special.EndsWith("%rowtype", StringComparison.Ordinal) && FindTable(special[..^8]) is { } rowTable)
            {
                var fields = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in rowTable.Columns)
                {
                    var field = $"@{SafeVar(d.Name)}_{SafeVar(c.Name)}";
                    var type = TypeMapper.PgToTSql(c.Type, ctx).Type;
                    fields[c.Name] = (field, type);
                    scope.Declarations.Add($"DECLARE {field} {type};");
                }

                scope.Records[d.Name] = fields;
                return;
            }

            if (special.EndsWith("%type", StringComparison.Ordinal))
            {
                var source = special[..^5];
                var dot = source.LastIndexOf('.');
                var columnType = dot > 0 && FindTable(source[..dot]) is { } t && t.Columns.FirstOrDefault(c => c.Name.Equals(source[(dot + 1)..], StringComparison.OrdinalIgnoreCase)) is { } col
                    ? TypeMapper.PgToTSql(col.Type, ctx).Type
                    : scope.Variables.TryGetValue(source, out var v) ? v.Type : "sql_variant";
                scope.Variables[d.Name] = (name, columnType);
                scope.Declarations.Add($"DECLARE {name} {columnType};");
                if (d.Default is not null)
                {
                    w.Line($"SET {name} = {Value(d.Default)};");
                }

                return;
            }

            scope.Issues.Add($"declaration of {d.Name} is not translated ({special})");
            w.Line(Review.Comment($"declaration of {d.Name} not translated ({special})"));
            return;
        }

        var mapped = TypeMapper.PgToTSql(d.Type ?? "text", ctx).Type;
        scope.Variables[d.Name] = (name, mapped);
        scope.Declarations.Add($"DECLARE {name} {mapped};");
        if (d.Default is not null)
        {
            w.Line($"SET {name} = {Value(d.Default)};");
        }
    }

    private string LoopVariable(string name, string type)
    {
        if (!scope.Variables.TryGetValue(name, out var v))
        {
            v = ("@" + SafeVar(ctx.Names.Name(name)), type);
            scope.Variables[name] = v;
            scope.Declarations.Add($"DECLARE {v.Name} {type};");
        }

        return v.Name;
    }

    private void Loop(string? label, string? continuePrelude, string condition, IReadOnlyList<PgStatement> body, Writer w, string? atEnd)
    {
        var exitLabel = $"trek_exit_{++scope.Counter}";
        loops.Add(new LoopFrame(label, continuePrelude, exitLabel));
        w.Line($"WHILE {condition}");
        w.Line("BEGIN");
        w.Indent();
        Statements(body, w);
        if (atEnd is not null)
        {
            w.Line(atEnd);
        }

        w.Outdent();
        w.Line("END;");
        loops.RemoveAt(loops.Count - 1);
        if (usedExitLabels.Remove(exitLabel))
        {
            w.Line($"{exitLabel}:");
        }
    }

    private readonly HashSet<string> usedExitLabels = new(StringComparer.Ordinal);

    private void Exit(PgExit e, Writer w)
    {
        if (loops.Count == 0)
        {
            w.Line(Review.Comment("EXIT outside a loop not translated", e.Source));
            scope.Issues.Add("EXIT outside a loop is not translated");
            return;
        }

        var index = e.Label is null ? loops.Count - 1 : loops.FindLastIndex(l => string.Equals(l.Label, e.Label, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = loops.Count - 1;
        }

        var frame = loops[index];
        string action;
        if (e.Continue)
        {
            if (index != loops.Count - 1)
            {
                scope.Issues.Add("CONTINUE of an outer loop is not translated");
            }

            action = (frame.ContinuePrelude is null ? string.Empty : frame.ContinuePrelude + " ") + "CONTINUE;";
        }
        else if (index == loops.Count - 1)
        {
            action = "BREAK;";
        }
        else
        {
            usedExitLabels.Add(frame.ExitLabel);
            action = $"GOTO {frame.ExitLabel};";
        }

        if (e.When is not null)
        {
            w.Line($"IF {Pred(e.When)}");
            w.Line("BEGIN");
            w.Line("    " + action);
            w.Line("END");
        }
        else
        {
            w.Line(action);
        }
    }

    private void Return(PgReturn r, Writer w)
    {
        switch (scope.Kind)
        {
            case "scalar":
                w.Line($"RETURN {(r.Value is null ? "NULL" : Value(r.Value))};");
                break;
            case "trigger_row":
                var record = r.Value is PgName { Parts.Count: 1 } n ? n.Last.ToLowerInvariant() : r.Value is PgLiteral { Kind: LiteralKind.Null } ? "null" : "other";
                if (record == "null" && scope.TriggerValues.TryGetValue("tg_when", out var when) && when == "N'BEFORE'")
                {
                    scope.Issues.Add("a BEFORE trigger that returns NULL skips the row in PostgreSQL; SQL Server has already written it: delete it or raise an error instead");
                }

                w.Line(record is "new" ? "GOTO trek_apply;" : "GOTO trek_next_row;");
                break;
            case "trigger_statement":
                w.Line("RETURN;");
                break;
            default:
                if (r.Value is not null && resultVariable == "@result" && scope.Kind == "procedure")
                {
                    w.Line($"SET @result = {Value(r.Value)};");
                }

                w.Line("RETURN;");
                break;
        }
    }

    private void Raise(PgRaise r, Writer w)
    {
        var message = r.Using.TryGetValue("message", out var m) ? Value(m) : null;
        if (message is null && r.Format is PgLiteral { Kind: LiteralKind.String } f)
        {
            var pattern = f.Value.Replace("%%", "\u0002", StringComparison.Ordinal).Replace("%", "%s", StringComparison.Ordinal).Replace("\u0002", "%%", StringComparison.Ordinal);
            message = r.Args.Count == 0
                ? ctx.Literal(f.Value.Replace("%%", "%", StringComparison.Ordinal))
                : $"FORMATMESSAGE({string.Join(", ", new[] { ctx.Literal(pattern) }.Concat(r.Args.Select(a => $"CAST({Value(a)} AS nvarchar(4000))")))})";
        }

        if (r.Level != "exception")
        {
            if (scope.Kind is "scalar" or "tvf")
            {
                return;
            }

            w.Line($"PRINT {message ?? "N''"};");
            return;
        }

        if (scope.Kind is "scalar" or "tvf")
        {
            w.Line(Review.Comment("a SQL Server function cannot raise errors", r.Source));
            scope.Issues.Add("RAISE EXCEPTION inside a function: SQL Server functions cannot raise errors; validate in the caller or make it a procedure");
            return;
        }

        if (message is null && r.Format is null && r.Using.Count == 0)
        {
            w.Line("THROW;");
            return;
        }

        if (message is null)
        {
            message = r.Using.TryGetValue("errcode", out var code) ? $"N'error ' + {Value(code)}" : "N'error'";
        }

        if (r.Using.ContainsKey("detail") || r.Using.ContainsKey("hint"))
        {
            scope.Issues.Add("RAISE ... USING DETAIL/HINT dropped");
        }

        if (message.StartsWith("N'", StringComparison.Ordinal) && message.EndsWith('\'') && !message.Contains("' +", StringComparison.Ordinal))
        {
            w.Line($"THROW 50000, {message}, 1;");
            return;
        }

        // THROW takes only a literal or a variable as its message.
        scope.Declarations.Add("DECLARE @trek_message nvarchar(2048);");
        w.Line($"SET @trek_message = {message};");
        w.Line("THROW 50000, @trek_message, 1;");
    }

    private void Perform(PgPerform p, Writer w)
    {
        if (p.Query.Body is PgSelectCore { From.Count: 0, Targets: [{ Expr: PgCall call }] } && (call.Function.Parts.Count > 1 || UserFunctions.Contains(call.Function.Last) || UserProcedures.Contains(call.Function.Last)))
        {
            if (UserProcedures.Contains(call.Function.Last))
            {
                w.Line(Exec(call));
                return;
            }

            scope.Declarations.Add("DECLARE @trek_ignore sql_variant;");
            w.Line($"SET @trek_ignore = {Call(call)};");
            return;
        }

        if (p.Query.Body is PgSelectCore { From.Count: 0, Targets: [{ Expr: PgCall { Function.Last: var fn } }] } && fn is "pg_sleep" or "pg_notify" or "set_config" or "pg_advisory_lock" or "pg_advisory_xact_lock")
        {
            w.Line(Review.Comment($"{fn}() not translated", p.Source));
            scope.Issues.Add($"{fn}() is not translated");
            return;
        }

        scope.UsesRowCount = true;
        w.Line($"SET @trek_rowcount = CASE WHEN EXISTS ({Query(p.Query, QueryContext.Subquery)}) THEN 1 ELSE 0 END;");
    }

    private string Exec(PgCall call)
    {
        var schema = call.Function.Parts.Count > 1 ? call.Function.Parts[^2] : ctx.Schema.DefaultSchema;
        var args = call.Args.Select((a, i) =>
        {
            var name = i < call.ArgNames.Count ? call.ArgNames[i] : null;
            var value = Value(a);
            if (a is not (PgLiteral or PgName or PgParam))
            {
                scope.Issues.Add($"EXEC arguments must be constants or variables: compute {Collapse(value)} into a variable first");
            }

            return (name is null ? string.Empty : $"@{SafeVar(ctx.Names.Name(name))} = ") + value;
        });
        return $"EXEC {Q(ctx.Names.Schema(schema))}.{Q(ctx.Names.Name(call.Function.Last))}{(call.Args.Count == 0 ? string.Empty : " " + string.Join(", ", args))};";
    }

    private void Assign(PgAssign a, Writer w)
    {
        switch (a.Target)
        {
            case PgName { Parts.Count: 2 } field when scope.Records.ContainsKey(field.Parts[0]):
                if (field.Parts[0].Equals("new", StringComparison.OrdinalIgnoreCase))
                {
                    scope.AssignedNew.Add(field.Parts[1]);
                }

                w.Line($"SET {Name(field)} = {Value(a.Value)};");
                break;
            case PgName { Parts.Count: 1 } variable:
                w.Line($"SET {Name(variable)} = {Value(a.Value)};");
                break;
            default:
                w.Line(Review.Comment("assignment not translated", a.Source));
                scope.Issues.Add($"assignment not translated: {Collapse(a.Source)}");
                break;
        }
    }

    /// <summary>Variables for SELECT ... INTO targets; a single record target gets one variable per selected column.</summary>
    private List<string> IntoVariables(IReadOnlyList<PgExpr> into, IReadOnlyList<PgTarget> targets, IReadOnlyList<PgFrom> from)
    {
        if (into is [PgName { Parts.Count: 1 } rec] && scope.Records.TryGetValue(rec.Last, out var fields))
        {
            var list = new List<string>();
            foreach (var t in targets)
            {
                var field = t.Alias ?? (t.Expr as PgName)?.Last ?? $"col{list.Count + 1}";
                if (!fields.TryGetValue(field, out var variable))
                {
                    var type = ColumnType(t.Expr, from) ?? "sql_variant";
                    variable = ($"@{SafeVar(rec.Last)}_{SafeVar(field)}", type);
                    fields[field] = variable;
                    scope.Declarations.Add($"DECLARE {variable.Name} {type};");
                }

                list.Add(variable.Name);
            }

            return list;
        }

        return into.Select(Value).ToList();
    }

    /// <summary>The target type of a selected column when it names a column of a table in FROM.</summary>
    private string? ColumnType(PgExpr e, IReadOnlyList<PgFrom> from)
    {
        if (e is not PgName name)
        {
            return null;
        }

        foreach (var table in Tables(from))
        {
            if (FindTable(table) is { } t && t.Columns.FirstOrDefault(c => c.Name.Equals(name.Last, StringComparison.OrdinalIgnoreCase)) is { } col)
            {
                return TypeMapper.PgToTSql(col.Type, ctx).Type;
            }
        }

        return null;
    }

    private static IEnumerable<PgName> Tables(IEnumerable<PgFrom> from)
    {
        foreach (var f in from)
        {
            switch (f)
            {
                case PgTableRef t:
                    yield return t.Name;
                    break;
                case PgJoin j:
                    foreach (var n in Tables([j.Left, j.Right]))
                    {
                        yield return n;
                    }

                    break;
            }
        }
    }

    private void SelectStatement(PgSelectStatement sel, Writer w)
    {
        if (sel.Query.Body is PgSelectCore core && core.Into.Count > 0)
        {
            if (core.IntoStrict)
            {
                scope.Issues.Add("SELECT ... INTO STRICT: SQL Server does not raise an error when no row or several rows match");
            }

            var variables = IntoVariables(core.Into, core.Targets, core.From);
            w.Line(Query(sel.Query with { Body = core with { Into = [] } }, QueryContext.Statement, variables) + ";");
            w.Line(RowCountMarker);
            return;
        }

        if (scope.Kind == "tvf")
        {
            w.Line($"INSERT INTO @result {Query(sel.Query, QueryContext.Statement)};");
            return;
        }

        w.Line(Query(sel.Query, QueryContext.Statement) + ";");
        w.Line(RowCountMarker);
    }

    private string With(IReadOnlyList<PgCte> with) =>
        with.Count == 0 ? string.Empty : Query(new PgQuery(with, false, new PgSelectCore(false, null, [], [], null, [], null), [], null, null), QueryContext.Statement)
            .Split(" SELECT NULL AS trek_empty", 2)[0] + " ";

    /// <summary>OUTPUT for RETURNING: a result set, or (RETURNING ... INTO) a table variable copied into the variables afterwards.</summary>
    private (string Output, string? After) Returning(IReadOnlyList<PgTarget> returning, IReadOnlyList<PgExpr> into, string pseudo, PgName table)
    {
        if (returning.Count == 0)
        {
            return (string.Empty, null);
        }

        outputPrefix = pseudo;
        var columns = returning.Select(r => r.Expr is PgStar ? $"{pseudo}.*" : Value(r.Expr) + (r.Alias is null ? string.Empty : " AS " + Q(ctx.Names.Name(r.Alias)))).ToList();
        outputPrefix = null;

        // Always OUTPUT ... INTO a table variable: SQL Server rejects a bare OUTPUT on a table that has triggers.
        var variable = $"@trek_out_{++scope.Counter}";
        var types = returning.Select(r => r.Expr is PgName n && FindTable(table)?.Columns.FirstOrDefault(c => c.Name.Equals(n.Last, StringComparison.OrdinalIgnoreCase)) is { } col
            ? TypeMapper.PgToTSql(col.Type, ctx).Type
            : "sql_variant").ToList();
        scope.Declarations.Add($"DECLARE {variable} TABLE ({string.Join(", ", types.Select((t, i) => $"c{i + 1} {t}"))});");
        var after = into.Count == 0
            ? $"SELECT {string.Join(", ", returning.Select((r, i) => $"c{i + 1} AS {Q(ctx.Names.Name(r.Alias ?? (r.Expr as PgName)?.Last ?? $"column{i + 1}"))}"))} FROM {variable};"
            : $"SELECT {string.Join(", ", into.Select(Value).Select((t, i) => $"{t} = c{i + 1}"))} FROM {variable};";
        return ($" OUTPUT {string.Join(", ", columns.Select((c, i) => Regex.Replace(c, @"\s+AS\s+\S+$", string.Empty)))} INTO {variable}", after);
    }

    private void Insert(PgInsert i, Writer w)
    {
        var table = Table(i.Table);
        var columns = i.Columns.Count == 0 ? string.Empty : " (" + string.Join(", ", i.Columns.Select(c => Q(ctx.Names.Name(c)))) + ")";
        var (output, after) = Returning(i.Returning, i.Into, "inserted", i.Table);
        string source;
        if (i.DefaultValues)
        {
            source = "DEFAULT VALUES";
        }
        else if (i.Rows is { Body: PgValuesBody values, OrderBy.Count: 0, Limit: null })
        {
            source = "VALUES " + string.Join(", ", values.Rows.Select(r => "(" + string.Join(", ", r.Select(Value)) + ")"));
        }
        else
        {
            source = Query(i.Rows!, QueryContext.Statement);
        }

        var conflict = i.OnConflict is null ? string.Empty : Note($"{i.OnConflict} is not translated; use MERGE or IF NOT EXISTS");
        w.Line($"{With(i.With)}INSERT INTO {table}{columns}{output} {source};{conflict}");
        w.Line(RowCountMarker);
        if (after is not null)
        {
            w.Line(after);
        }
    }

    private void Update(PgUpdate u, Writer w)
    {
        var table = Table(u.Table);
        var alias = u.Alias is null ? null : Q(ctx.Names.Name(u.Alias));
        var sets = string.Join(", ", u.Sets.Select(s => $"{Q(ctx.Names.Name(s.Column))} = {Value(s.Value)}"));
        var (output, after) = Returning(u.Returning, u.Into, "inserted", u.Table);
        var where = u.Where is null ? string.Empty : " WHERE " + Pred(u.Where);
        if (u.From.Count > 0 || alias is not null)
        {
            var from = $"{table}{(alias is null ? string.Empty : " AS " + alias)}" + string.Concat(u.From.Select(f => ", " + From(f)));
            w.Line($"{With(u.With)}UPDATE {alias ?? table} SET {sets}{output} FROM {from}{where};");
        }
        else
        {
            w.Line($"{With(u.With)}UPDATE {table} SET {sets}{output}{where};");
        }

        w.Line(RowCountMarker);
        if (after is not null)
        {
            w.Line(after);
        }
    }

    private void Delete(PgDelete d, Writer w)
    {
        var table = Table(d.Table);
        var alias = d.Alias is null ? null : Q(ctx.Names.Name(d.Alias));
        var (output, after) = Returning(d.Returning, d.Into, "deleted", d.Table);
        var where = d.Where is null ? string.Empty : " WHERE " + Pred(d.Where);
        if (d.Using.Count > 0 || alias is not null)
        {
            var from = $"{table}{(alias is null ? string.Empty : " AS " + alias)}" + string.Concat(d.Using.Select(f => ", " + From(f)));
            w.Line($"{With(d.With)}DELETE {alias ?? table}{output} FROM {from}{where};");
        }
        else
        {
            w.Line($"{With(d.With)}DELETE FROM {table}{output}{where};");
        }

        w.Line(RowCountMarker);
        if (after is not null)
        {
            w.Line(after);
        }
    }

    private void ForQuery(PgForQuery f, Writer w)
    {
        if (f.Query is null)
        {
            w.Line(Review.Comment("FOR ... IN EXECUTE not translated", f.Source));
            scope.Issues.Add("FOR ... IN EXECUTE (dynamic SQL loop) is not translated");
            return;
        }

        var cursor = $"trek_cursor_{++scope.Counter}";
        List<string> targets;
        if (scope.Variables.TryGetValue(f.Variable, out var scalar))
        {
            targets = [scalar.Name];
        }
        else
        {
            if (!scope.Records.ContainsKey(f.Variable))
            {
                scope.Records[f.Variable] = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            }

            var core = f.Query.Body as PgSelectCore;
            targets = core is null
                ? [LoopVariable(f.Variable, "sql_variant")]
                : IntoVariables([new PgName([f.Variable])], core.Targets, core.From);
            if (core?.Targets.Any(t => t.Expr is PgStar) == true)
            {
                scope.Issues.Add($"FOR {f.Variable} IN SELECT *: list the columns so each gets a variable");
            }
        }

        var fetch = $"FETCH NEXT FROM {cursor} INTO {string.Join(", ", targets)};";
        w.Line($"DECLARE {cursor} CURSOR LOCAL FAST_FORWARD FOR {Query(f.Query, QueryContext.Statement)};");
        w.Line($"OPEN {cursor};");
        w.Line(fetch);
        Loop(f.Label, fetch, "@@FETCH_STATUS = 0", f.Body, w, fetch);
        w.Line($"CLOSE {cursor};");
        w.Line($"DEALLOCATE {cursor};");
    }

    // ------------------------------------------------------------------ triggers

    [GeneratedRegex(@"^CREATE\s+(?:CONSTRAINT\s+)?TRIGGER\s+(?<name>\S+)\s+(?<when>BEFORE|AFTER|INSTEAD\s+OF)\s+(?<events>.+?)\s+ON\s+(?<table>\S+)(?<rest>.*?)\s+FOR\s+EACH\s+(?<level>ROW|STATEMENT)(?:\s+WHEN\s+\((?<cond>.*)\))?\s+EXECUTE\s+(?:FUNCTION|PROCEDURE)\s+(?<fn>[^(]+)\((?<args>.*)\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TriggerRegex();

    private string Trigger(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var m = TriggerRegex().Match(code.Definition.Trim());
        var function = code.TriggerFunction is { } fn ? all.FirstOrDefault(c => c.Kind == PortableCodeKind.Function && c.Name.Key == fn.Key) : null;
        if (!m.Success || function is null || code.Table is not { } owner)
        {
            scope.Issues.Add("trigger not translated: its definition or function could not be read");
            return Review.CommentedOut(code.Definition, Review.Comment("trigger not translated"));
        }

        var when = Regex.Replace(m.Groups["when"].Value.ToUpperInvariant(), @"\s+", " ");
        var row = m.Groups["level"].Value.Equals("ROW", StringComparison.OrdinalIgnoreCase);
        var events = Regex.Split(m.Groups["events"].Value, @"\s+OR\s+", RegexOptions.IgnoreCase).Select(e => e.Trim()).ToList();
        var updateOf = events.FirstOrDefault(e => e.StartsWith("UPDATE OF", StringComparison.OrdinalIgnoreCase)) is { } uo
            ? uo[9..].Split(',').Select(c => c.Trim().Trim('"')).ToList()
            : [];
        var kinds = events.Select(e => e.Split(' ')[0].ToUpperInvariant()).Where(e => e is "INSERT" or "UPDATE" or "DELETE").Distinct().ToList();
        if (events.Any(e => e.StartsWith("TRUNCATE", StringComparison.OrdinalIgnoreCase)))
        {
            scope.Issues.Add("TRUNCATE triggers have no SQL Server counterpart");
        }

        if (m.Groups["args"].Value.Trim().Length > 0)
        {
            scope.Issues.Add($"trigger arguments ({m.Groups["args"].Value}) are not passed: TG_ARGV is not translated");
        }

        var table = FindTable(new PgName([owner.Schema, owner.Name]));
        var target = ctx.Qualified(owner);
        var header = new PgParser(function.Definition).RoutineHeader();
        if (header.Language != "plpgsql")
        {
            scope.Issues.Add($"trigger function in {header.Language} is not translated");
            return Review.CommentedOut(code.Definition + "\n\n" + function.Definition, Review.Comment("trigger not translated"));
        }

        var body = new PgParser(header.Body).PlpgsqlBody();
        scope.TriggerValues["tg_op"] = "@tg_op";
        scope.TriggerValues["tg_table_name"] = ctx.Literal(ctx.Names.Name(owner.Name));
        scope.TriggerValues["tg_table_schema"] = ctx.Literal(ctx.Names.Schema(owner.Schema));
        scope.TriggerValues["tg_relname"] = ctx.Literal(ctx.Names.Name(owner.Name));
        scope.TriggerValues["tg_name"] = ctx.Literal(code.Name.Name);
        scope.TriggerValues["tg_when"] = $"N'{when}'";
        scope.TriggerValues["tg_level"] = row ? "N'ROW'" : "N'STATEMENT'";

        var name = ctx.Names.Name(code.Name.Name);
        if (all.Count(c => c.Kind == PortableCodeKind.Trigger && c.Name.Schema == code.Name.Schema && c.Name.Name.Equals(code.Name.Name, StringComparison.OrdinalIgnoreCase)) > 1)
        {
            name = $"{ctx.Names.Name(owner.Name)}_{name}";
        }

        var sb = new StringBuilder();
        var timing = when == "INSTEAD OF" ? "INSTEAD OF" : "AFTER";
        if (when == "BEFORE")
        {
            scope.Issues.Add("BEFORE trigger became an AFTER trigger: changes to NEW are written back with an UPDATE by primary key, and errors still roll the statement back");
        }

        sb.Append($"CREATE OR ALTER TRIGGER {ctx.QualifiedTarget(ctx.Names.Schema(code.Name.Schema), name)} ON {target}\n{timing} {string.Join(", ", kinds)}\nAS\nBEGIN\n");
        sb.Append("    SET NOCOUNT ON;\n");
        sb.Append("    IF NOT EXISTS (SELECT 1 FROM inserted) AND NOT EXISTS (SELECT 1 FROM deleted) RETURN;\n");
        sb.Append("    DECLARE @tg_op nvarchar(6) = CASE WHEN EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted) THEN N'UPDATE' WHEN EXISTS (SELECT 1 FROM inserted) THEN N'INSERT' ELSE N'DELETE' END;\n");
        if (updateOf.Count > 0)
        {
            sb.Append($"    IF @tg_op = N'UPDATE' AND NOT ({string.Join(" OR ", updateOf.Select(c => $"UPDATE({Q(ctx.Names.Name(c))})"))}) RETURN;\n");
        }

        var w = new Writer();
        if (!row)
        {
            scope.Kind = "trigger_statement";
            foreach (Match r in Regex.Matches(m.Groups["rest"].Value, @"(?<which>NEW|OLD)\s+TABLE\s+AS\s+(?<alias>\S+)", RegexOptions.IgnoreCase))
            {
                scope.TransitionTables[r.Groups["alias"].Value.Trim('"')] = r.Groups["which"].Value.Equals("NEW", StringComparison.OrdinalIgnoreCase) ? "inserted" : "deleted";
            }

            Statement(body, w);
            sb.Append(Declarations()).Append(w.Render(scope.UsesRowCount)).Append("END;\n");
            return sb.ToString();
        }

        if (table is null)
        {
            scope.Issues.Add("row trigger on a table whose columns are unknown (a view?): not translated");
            return Review.CommentedOut(code.Definition + "\n\n" + function.Definition, Review.Comment("trigger not translated"));
        }

        scope.Kind = "trigger_row";
        var newFields = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var oldFields = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var declarations = new List<string>();
        foreach (var c in table.Columns)
        {
            var type = TypeMapper.PgToTSql(c.Type, ctx).Type;
            newFields[c.Name] = ($"@new_{SafeVar(ctx.Names.Name(c.Name))}", type);
            oldFields[c.Name] = ($"@old_{SafeVar(ctx.Names.Name(c.Name))}", type);
            declarations.Add($"DECLARE @new_{SafeVar(ctx.Names.Name(c.Name))} {type}, @old_{SafeVar(ctx.Names.Name(c.Name))} {type};");
        }

        scope.Records["new"] = newFields;
        scope.Records["old"] = oldFields;
        var key = table.PrimaryKey?.Columns ?? [];
        string source;
        var newColumns = string.Join(", ", table.Columns.Select(c => "i." + Q(ctx.Names.Name(c.Name))));
        var oldColumns = string.Join(", ", table.Columns.Select(c => "d." + Q(ctx.Names.Name(c.Name))));
        if (kinds is ["INSERT"])
        {
            source = $"SELECT {newColumns}, {string.Join(", ", table.Columns.Select(_ => "NULL"))} FROM inserted AS i";
        }
        else if (kinds is ["DELETE"])
        {
            source = $"SELECT {string.Join(", ", table.Columns.Select(_ => "NULL"))}, {oldColumns} FROM deleted AS d";
        }
        else if (key.Count > 0)
        {
            source = $"SELECT {newColumns}, {oldColumns} FROM inserted AS i FULL JOIN deleted AS d ON {string.Join(" AND ", key.Select(k => $"i.{Q(ctx.Names.Name(k))} = d.{Q(ctx.Names.Name(k))}"))}";
        }
        else
        {
            scope.Issues.Add("the table has no primary key, so NEW and OLD rows of an UPDATE cannot be paired; OLD is NULL");
            source = $"SELECT {newColumns}, {string.Join(", ", table.Columns.Select(_ => "NULL"))} FROM inserted AS i UNION ALL SELECT {string.Join(", ", table.Columns.Select(_ => "NULL"))}, {oldColumns} FROM deleted AS d WHERE NOT EXISTS (SELECT 1 FROM inserted)";
        }

        var fetch = $"FETCH NEXT FROM trek_rows INTO {string.Join(", ", newFields.Values.Select(v => v.Item1).Concat(oldFields.Values.Select(v => v.Item1)))};";
        w.Line($"DECLARE trek_rows CURSOR LOCAL FAST_FORWARD FOR {source};");
        w.Line("OPEN trek_rows;");
        w.Line(fetch);
        w.Line("WHILE @@FETCH_STATUS = 0");
        w.Line("BEGIN");
        w.Indent();
        foreach (var k in key)
        {
            var keyVar = $"@trek_key_{SafeVar(ctx.Names.Name(k))}";
            declarations.Add($"DECLARE {keyVar} {newFields[k].Item2};");
            w.Line($"SET {keyVar} = COALESCE({newFields[k].Item1}, {oldFields[k].Item1});");
        }

        if (m.Groups["cond"].Success)
        {
            w.Line($"IF NOT ({Pred(new PgParser(m.Groups["cond"].Value).ExpressionOnly())}) GOTO trek_next_row;");
        }

        loops.Add(new LoopFrame(null, fetch, "trek_rows_end"));
        Statement(body, w);
        loops.RemoveAt(loops.Count - 1);
        w.Line("trek_apply:");
        if (when == "BEFORE" && scope.AssignedNew.Count > 0)
        {
            if (key.Count == 0)
            {
                scope.Issues.Add("changes to NEW cannot be written back: the table has no primary key");
            }
            else
            {
                w.Line($"IF @tg_op <> N'DELETE'");
                w.Line($"    UPDATE {target} SET {string.Join(", ", scope.AssignedNew.Select(c => $"{Q(ctx.Names.Name(c))} = {newFields[c].Item1}"))} WHERE {string.Join(" AND ", key.Select(k => $"{Q(ctx.Names.Name(k))} = @trek_key_{SafeVar(ctx.Names.Name(k))}"))};");
            }
        }

        w.Line("trek_next_row:");
        w.Line(fetch);
        w.Outdent();
        w.Line("END;");
        w.Line("CLOSE trek_rows;");
        w.Line("DEALLOCATE trek_rows;");
        scope.Issues.Add("row-level trigger runs row by row with a cursor over inserted/deleted; rewrite it set-based if statements touch many rows");
        sb.Append(string.Concat(declarations.Select(d => "    " + d + "\n"))).Append(Declarations()).Append(w.Render(scope.UsesRowCount)).Append("END;\n");
        return sb.ToString();
    }
}
