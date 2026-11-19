using System.Text;
using Badger.SqlMigrate.Core.Conversion;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Badger.SqlMigrate.Conversion.TSqlToPg;

internal sealed partial class TSqlToPgTranslator
{
    private const string RowCountMarker = "\u0001ROWCOUNT\u0001";

    /// <summary>Lines of a PL/pgSQL body with indentation.</summary>
    private sealed class Body
    {
        private readonly List<string> lines = [];
        private int depth = 1;

        public List<string> Declarations { get; } = [];

        /// <summary>A multi-statement TVF's @result variable: inserts into it become RETURN QUERY.</summary>
        public string? ResultVariable { get; set; }

        public string Kind { get; set; } = "procedure";

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
                        sb.Append(line.Replace(RowCountMarker, "GET DIAGNOSTICS v_rowcount = ROW_COUNT;", StringComparison.Ordinal)).Append('\n');
                    }

                    continue;
                }

                sb.Append(line).Append('\n');
            }

            return sb.ToString();
        }
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var target = ctx.Qualified(code.Name);
        scope = new Scope(code.Name.ToString());
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        var fileName = $"{ctx.Names.Schema(code.Name.Schema)}.{ctx.Names.Name(code.Name.Name)}.sql";

        string script;
        var fragment = Parser().Parse(new StringReader(code.Definition), out var errors);
        var statement = (fragment as TSqlScript)?.Batches.SelectMany(b => b.Statements).FirstOrDefault();
        if (errors.Count > 0 || statement is null)
        {
            scope.Issues.Add($"could not parse the definition: {errors.FirstOrDefault()?.Message}");
            script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for PostgreSQL"));
        }
        else
        {
            try
            {
                script = statement switch
                {
                    CreateViewStatement v => View(v, target),
                    CreateFunctionStatement f => Function(f, target, code),
                    CreateProcedureStatement p => Procedure(p, target),
                    CreateTriggerStatement t => Trigger(t, code),
                    _ => Review.CommentedOut(code.Definition, Review.Comment($"{statement.GetType().Name} is not translated")),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scope.Issues.Add($"translation failed ({ex.Message}); the original is kept, commented out");
                script = Review.CommentedOut(code.Definition, Review.Comment("translation failed; rewrite it for PostgreSQL"));
            }
        }

        var issues = scope.Issues.Distinct().ToList();
        foreach (var issue in issues)
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        var header = new StringBuilder($"-- Converted by bsm convert from SQL Server {code.Kind.ToString().ToLowerInvariant()} {code.Name}.");
        header.Append(issues.Count == 0 ? " Check it before deploying.\n" : $" {issues.Count} item(s) to review (search for '{Review.Marker}').\n");
        foreach (var issue in issues)
        {
            header.Append("-- * ").Append(issue.Replace('\n', ' ')).Append('\n');
        }

        return new TranslatedCode(code, folder, fileName, header + script.TrimEnd() + "\n", issues);
    }

    // ------------------------------------------------------------------ objects

    private string View(CreateViewStatement v, string target)
    {
        var columns = v.Columns.Count == 0 ? string.Empty : " (" + string.Join(", ", v.Columns.Select(Id)) + ")";
        var query = SelectStatementText(v.SelectStatement);
        return $"CREATE OR REPLACE VIEW {target}{columns} AS\n{query}{(v.WithCheckOption ? "\nWITH CHECK OPTION" : string.Empty)};\n";
    }

    private string SelectStatementText(SelectStatement s) => With(s.WithCtesAndXmlNamespaces) + Query(s.QueryExpression);

    private List<string> Parameters(IList<ProcedureParameter> parameters, bool procedure)
    {
        var list = new List<string>();
        foreach (var p in parameters)
        {
            var name = VariableName(p.VariableName.Value, "p_");
            var type = Type(p.DataType);
            scope.Variables[p.VariableName.Value] = (ctx.Quote(name), type, KindOfTarget(type));
            var mode = p.Modifier == ParameterModifier.Output ? (procedure ? "INOUT " : "OUT ") : string.Empty;
            if (p.Modifier == ParameterModifier.ReadOnly)
            {
                scope.Issues.Add($"table-valued parameter {p.VariableName.Value} is not translated");
            }

            list.Add($"{mode}{ctx.Quote(name)} {type}{(p.Value is null ? (mode.Length > 0 && procedure ? " DEFAULT NULL" : string.Empty) : " DEFAULT " + Value(p.Value, type == "boolean"))}");
        }

        return list;
    }

    private static ConversionContext.ColumnKind KindOfTarget(string pgType) =>
        pgType == "boolean" ? ConversionContext.ColumnKind.Boolean
        : pgType == "text" || pgType.StartsWith("varchar", StringComparison.Ordinal) || pgType.StartsWith("char", StringComparison.Ordinal) ? ConversionContext.ColumnKind.Text
        : ConversionContext.ColumnKind.Other;

    private string Function(CreateFunctionStatement f, string target, PortableCode code)
    {
        var parameters = Parameters(f.Parameters, procedure: false);
        var body = new Body { Kind = "function" };
        string returns;
        switch (f.ReturnType)
        {
            case SelectFunctionReturnType inline:
            {
                // Inline table-valued function: a SQL function returning its query.
                var columns = code.ResultColumns.Count == 0
                    ? "SETOF record" + Note("the result columns are unknown; declare RETURNS TABLE (...)")
                    : "TABLE (" + string.Join(", ", code.ResultColumns.Select(c => $"{ctx.Ident(c.Name)} {TypeMapper.TSqlToPg(c.Type, ctx).Type}")) + ")";
                return $"CREATE OR REPLACE FUNCTION {target}({string.Join(", ", parameters)})\nRETURNS {columns}\nLANGUAGE sql\nAS $body$\n{SelectStatementText(inline.SelectStatement)};\n$body$;\n";
            }

            case TableValuedFunctionReturnType table:
                body.ResultVariable = table.DeclareTableVariableBody.VariableName.Value;
                body.Kind = "tvf";
                returns = "TABLE (" + string.Join(", ", table.DeclareTableVariableBody.Definition.ColumnDefinitions.Select(c => $"{Id(c.ColumnIdentifier)} {Type(c.DataType)}")) + ")";
                body.Declarations.Add("#variable_conflict use_column");
                break;
            case ScalarFunctionReturnType scalar:
                returns = Type(scalar.DataType);
                body.Kind = "scalar";
                break;
            default:
                return Review.CommentedOut(code.Definition, Review.Comment("this kind of function is not translated") + Track("CLR or unknown function kind"));
        }

        Statements(f.StatementList, body);
        return Routine("FUNCTION", target, parameters, $"RETURNS {returns}\n", body);
    }

    private string Procedure(CreateProcedureStatement p, string target)
    {
        var parameters = Parameters(p.Parameters, procedure: true);
        var body = new Body { Kind = "procedure" };
        Statements(p.StatementList, body);
        return Routine("PROCEDURE", target, parameters, string.Empty, body);
    }

    private string Routine(string kind, string target, List<string> parameters, string returns, Body body, string? prologue = null)
    {
        var sb = new StringBuilder($"CREATE OR REPLACE {kind} {target}({string.Join(", ", parameters)})\n{returns}LANGUAGE plpgsql\nAS $body$\n");
        var declarations = new List<string>(body.Declarations);
        if (scope.UsesRowCount)
        {
            declarations.Add("v_rowcount bigint := 0;");
        }

        var directives = declarations.Where(d => d.StartsWith('#')).ToList();
        foreach (var d in directives)
        {
            sb.Append(d).Append('\n');
        }

        var vars = declarations.Except(directives).ToList();
        if (vars.Count > 0)
        {
            sb.Append("DECLARE\n");
            foreach (var d in vars)
            {
                sb.Append("    ").Append(d).Append('\n');
            }
        }

        sb.Append("BEGIN\n");
        if (prologue is not null)
        {
            sb.Append("    ").Append(prologue).Append('\n');
        }

        sb.Append(body.Render(scope.UsesRowCount));
        sb.Append("END\n$body$;\n");
        return sb.ToString();
    }

    private string Trigger(CreateTriggerStatement t, PortableCode code)
    {
        var table = ObjectName(t.TriggerObject.Name);
        if (t.TriggerObject.Name.SchemaIdentifier is null && code.Table is { } owner)
        {
            table = ctx.Qualified(owner);
        }

        var schema = ctx.Quote(ctx.Names.Schema(code.Name.Schema));
        var name = ctx.Names.Name(code.Name.Name);
        if (t.TriggerType == TriggerType.InsteadOf)
        {
            scope.Issues.Add("INSTEAD OF trigger: PostgreSQL runs INSTEAD OF triggers per row (FOR EACH ROW, on views only); rewrite the body using NEW and OLD");
            return Review.CommentedOut(code.Definition, Review.Comment("INSTEAD OF trigger not translated"));
        }

        var events = t.TriggerActions.Where(a => a.TriggerActionType is TriggerActionType.Insert or TriggerActionType.Update or TriggerActionType.Delete).Select(a => a.TriggerActionType).Distinct().ToList();
        if (events.Count == 0)
        {
            scope.Issues.Add("database or DDL trigger is not translated; consider an event trigger");
            return Review.CommentedOut(code.Definition, Review.Comment("trigger not translated"));
        }

        var sb = new StringBuilder();
        foreach (var e in events)
        {
            var suffix = events.Count == 1 ? string.Empty : e switch { TriggerActionType.Insert => "_ins", TriggerActionType.Update => "_upd", _ => "_del" };
            var outer = scope;
            scope = new Scope(outer.Owner)
            {
                TriggerTable = table,
                HasInserted = e is TriggerActionType.Insert or TriggerActionType.Update,
                HasDeleted = e is TriggerActionType.Update or TriggerActionType.Delete,
            };
            foreach (var (k, v) in outer.Variables)
            {
                scope.Variables[k] = v;
            }

            var body = new Body { Kind = "trigger" };
            Statements(t.StatementList, body);
            body.Line("RETURN NULL;");
            var prologue = scope.UsesRowCount ? $"v_rowcount := (SELECT count(*) FROM {(scope.HasInserted ? "inserted" : "deleted")});" : null;
            var function = $"{schema}.{ctx.Quote(name + suffix + "_fn")}";
            sb.Append(Routine("FUNCTION", function, [], "RETURNS trigger\n", body, prologue)).Append('\n');
            var triggerName = ctx.Quote(name + suffix);
            var referencing = e switch
            {
                TriggerActionType.Insert => "REFERENCING NEW TABLE AS inserted",
                TriggerActionType.Delete => "REFERENCING OLD TABLE AS deleted",
                _ => "REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted",
            };
            sb.Append($"DROP TRIGGER IF EXISTS {triggerName} ON {table};\n");
            sb.Append($"CREATE TRIGGER {triggerName} AFTER {e.ToString().ToUpperInvariant()} ON {table}\n    {referencing}\n    FOR EACH STATEMENT EXECUTE FUNCTION {function}();\n\n");
            outer.Issues.AddRange(scope.Issues);
            outer.UsesRowCount |= scope.UsesRowCount;
            scope = outer;
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ statements

    private void Statements(StatementList? list, Body body)
    {
        if (list is null)
        {
            return;
        }

        var statements = list.Statements;
        for (var i = 0; i < statements.Count; i++)
        {
            // INSERT ...; SET @id = SCOPE_IDENTITY();  →  INSERT ... RETURNING <identity column> INTO v_id;
            if (statements[i] is InsertStatement insert && i + 1 < statements.Count && IdentityAssignment(statements[i + 1]) is { } variable
                && IdentityColumn(insert) is { } identity)
            {
                Insert(insert, body, $" RETURNING {ctx.Ident(identity)} INTO {Var(variable)}");
                i++;
                continue;
            }

            Statement(statements[i], body);
        }
    }

    /// <summary>SET @x = SCOPE_IDENTITY() / @@IDENTITY (also SELECT @x = ...): the variable assigned.</summary>
    private static string? IdentityAssignment(TSqlStatement statement)
    {
        static bool IsIdentity(ScalarExpression? e) => Unwrap(e!) switch
        {
            FunctionCall f => f.FunctionName.Value.Equals("SCOPE_IDENTITY", StringComparison.OrdinalIgnoreCase),
            GlobalVariableExpression g => g.Name.Equals("@@IDENTITY", StringComparison.OrdinalIgnoreCase),
            CastCall c => IsIdentity(c.Parameter),
            ConvertCall c => IsIdentity(c.Parameter),
            _ => false,
        };

        return statement switch
        {
            SetVariableStatement { Expression: { } e } s when IsIdentity(e) => s.Variable.Name,
            SelectStatement { QueryExpression: QuerySpecification { FromClause: null, SelectElements: [SelectSetVariable v] } } when IsIdentity(v.Expression) => v.Variable.Name,
            _ => null,
        };
    }

    /// <summary>The identity column of the table an INSERT writes to (source name), when the schema has it.</summary>
    private string? IdentityColumn(InsertStatement insert)
    {
        if (insert.InsertSpecification.Target is not NamedTableReference named || insert.InsertSpecification.OutputClause is not null)
        {
            return null;
        }

        var name = named.SchemaObject;
        var schema = name.SchemaIdentifier?.Value ?? ctx.Schema.DefaultSchema;
        return ctx.Schema.Tables.FirstOrDefault(t => t.Name.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) && t.Name.Name.Equals(name.BaseIdentifier.Value, StringComparison.OrdinalIgnoreCase))
            ?.Columns.FirstOrDefault(c => c.Identity is not null)?.Name;
    }

    private void Statement(TSqlStatement s, Body body)
    {
        switch (s)
        {
            case BeginEndBlockStatement block:
                Statements(block.StatementList, body);
                break;

            case DeclareVariableStatement d:
                foreach (var v in d.Declarations)
                {
                    var name = ctx.Quote(VariableName(v.VariableName.Value, "v_"));
                    var type = Type(v.DataType);
                    scope.Variables[v.VariableName.Value] = (name, type, KindOfTarget(type));
                    body.Declarations.Add($"{name} {type};");
                    if (v.Value is not null)
                    {
                        body.Line($"{name} := {Value(v.Value, type == "boolean")};");
                    }
                }

                break;

            case DeclareTableVariableStatement t:
            {
                var name = ctx.Quote(VariableName(t.Body.VariableName.Value, "t_"));
                body.Line($"DROP TABLE IF EXISTS {name};");
                body.Line($"CREATE TEMP TABLE {name} ({string.Join(", ", t.Body.Definition.ColumnDefinitions.Select(c => $"{Id(c.ColumnIdentifier)} {Type(c.DataType)}"))}) ON COMMIT DROP;");
                scope.Issues.Add($"table variable {t.Body.VariableName.Value} became the temporary table {name}");
                break;
            }

            case SetVariableStatement sv when sv.Expression is not null:
            {
                var target = Var(sv.Variable.Name);
                var boolean = IsBit(sv.Variable);
                var value = Value(sv.Expression, boolean);
                body.Line(sv.AssignmentKind switch
                {
                    AssignmentKind.AddEquals => $"{target} := {target} {(IsString(sv.Variable) || IsString(sv.Expression) ? "||" : "+")} {value};",
                    AssignmentKind.SubtractEquals => $"{target} := {target} - {value};",
                    AssignmentKind.MultiplyEquals => $"{target} := {target} * {value};",
                    AssignmentKind.DivideEquals => $"{target} := {target} / {value};",
                    _ => $"{target} := {value};",
                });
                break;
            }

            case SelectStatement sel:
                Select(sel, body);
                break;

            case InsertStatement i:
                Insert(i, body);
                break;

            case UpdateStatement u:
                Update(u, body);
                break;

            case DeleteStatement d:
                Delete(d, body);
                break;

            case IfStatement i:
                body.Line($"IF {Bool(i.Predicate)} THEN");
                Branch(i.ThenStatement, body);
                var next = i.ElseStatement;
                while (next is IfStatement elseIf)
                {
                    body.Line($"ELSIF {Bool(elseIf.Predicate)} THEN");
                    Branch(elseIf.ThenStatement, body);
                    next = elseIf.ElseStatement;
                }

                if (next is not null)
                {
                    body.Line("ELSE");
                    Branch(next, body);
                }

                body.Line("END IF;");
                break;

            case WhileStatement w:
                body.Line($"WHILE {Bool(w.Predicate)} LOOP");
                Branch(w.Statement, body);
                body.Line("END LOOP;");
                break;

            case BreakStatement:
                body.Line("EXIT;");
                break;

            case ContinueStatement:
                body.Line("CONTINUE;");
                break;

            case ReturnStatement r:
                body.Line(body.Kind switch
                {
                    "scalar" => $"RETURN {(r.Expression is null ? "NULL" : Expr(r.Expression))};",
                    "trigger" => "RETURN NULL;",
                    _ when r.Expression is not null => "RETURN;" + Note($"the return value {Text(r.Expression)} is dropped (PostgreSQL procedures return no status; use an INOUT parameter)"),
                    _ => "RETURN;",
                });
                break;

            case TryCatchStatement tc:
                body.Line("BEGIN");
                body.Indent();
                Statements(tc.TryStatements, body);
                body.Outdent();
                body.Line("EXCEPTION WHEN OTHERS THEN");
                body.Indent();
                Statements(tc.CatchStatements, body);
                body.Outdent();
                body.Line("END;");
                break;

            case ThrowStatement th:
                body.Line(th.Message is null
                    ? "RAISE;"
                    : $"RAISE EXCEPTION USING MESSAGE = {Expr(th.Message)}, DETAIL = 'error ' || {Expr(th.ErrorNumber)};");
                break;

            case RaiseErrorStatement re:
            {
                var severity = Unwrap(re.SecondParameter) is IntegerLiteral sev && int.TryParse(sev.Value, out var level) ? level : 16;
                var message = Unwrap(re.FirstParameter) is StringLiteral m
                    ? ctx.Literal(System.Text.RegularExpressions.Regex.Replace(m.Value, "%[-+ 0#]*[0-9]*(?:\\.[0-9]+)?(?:h|l|I64)?[diouxXs]", "%s"))
                    : Expr(re.FirstParameter);
                var args = re.OptionalParameters.Count == 0 ? message : $"format({message}, {string.Join(", ", re.OptionalParameters.Select(Expr))})";
                body.Line(severity >= 11 ? $"RAISE EXCEPTION '%', {args};" : $"RAISE NOTICE '%', {args};");
                break;
            }

            case PrintStatement p:
                body.Line($"RAISE NOTICE '%', {Expr(p.Expression)};");
                break;

            case ExecuteStatement e:
                Execute(e, body);
                break;

            case PredicateSetStatement or SetOnOffStatement or SetTransactionIsolationLevelStatement or SetCommandStatement or SetErrorLevelStatement:
                break;

            case BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement:
                body.Line(Review.Comment("transaction control removed: the routine runs in the caller's transaction, and an EXCEPTION block undoes its own work", Text(s)));
                scope.Issues.Add("transaction control (BEGIN TRAN / COMMIT / ROLLBACK) was removed");
                break;

            case TruncateTableStatement tr:
                body.Line($"TRUNCATE TABLE {ObjectName(tr.TableName)};");
                break;

            case CreateTableStatement ct when ct.SchemaObjectName.BaseIdentifier.Value.StartsWith('#'):
            {
                var name = ObjectName(ct.SchemaObjectName);
                scope.TempTables.Add(name);
                body.Line($"DROP TABLE IF EXISTS {name};");
                body.Line($"CREATE TEMP TABLE {name} ({string.Join(", ", ct.Definition.ColumnDefinitions.Select(c => $"{Id(c.ColumnIdentifier)} {Type(c.DataType)}"))}) ON COMMIT DROP;");
                break;
            }

            case DropTableStatement dt:
                body.Line($"DROP TABLE IF EXISTS {string.Join(", ", dt.Objects.Select(ObjectName))};");
                break;

            case WaitForStatement wf:
                body.Line(Review.Comment("WAITFOR not translated; use pg_sleep()", Text(wf)));
                scope.Issues.Add("WAITFOR was not translated");
                break;

            default:
                body.Line(Review.Comment($"{s.GetType().Name.Replace("Statement", string.Empty, StringComparison.Ordinal)} not translated", Text(s)));
                scope.Issues.Add($"not translated: {Collapse(Text(s))}");
                break;
        }
    }

    private void Branch(TSqlStatement statement, Body body)
    {
        body.Indent();
        Statement(statement, body);
        body.Outdent();
    }

    private void Select(SelectStatement sel, Body body)
    {
        var with = With(sel.WithCtesAndXmlNamespaces);
        if (sel.QueryExpression is QuerySpecification spec && spec.SelectElements.All(e => e is SelectSetVariable))
        {
            // SELECT @a = x, @b = y FROM ...  →  SELECT x, y INTO v_a, v_b FROM ...
            var sets = spec.SelectElements.Cast<SelectSetVariable>().ToList();
            var values = string.Join(", ", sets.Select(v => Value(v.Expression, IsBit(v.Variable))));
            var targets = string.Join(", ", sets.Select(v => Var(v.Variable.Name)));
            body.Line($"{with}{Query(spec, $"{values} INTO {targets}")};");
            body.Line(RowCountMarker);
            return;
        }

        var query = with + Query(sel.QueryExpression);
        if (sel.Into is { } into)
        {
            var name = ObjectName(into);
            var temp = into.BaseIdentifier.Value.StartsWith('#');
            body.Line(temp ? $"DROP TABLE IF EXISTS {name};" : string.Empty);
            body.Line($"CREATE {(temp ? "TEMP " : string.Empty)}TABLE {name} AS {query};");
            body.Line(RowCountMarker);
            return;
        }

        if (body.Kind == "tvf")
        {
            body.Line($"RETURN QUERY {query};");
            return;
        }

        scope.Issues.Add("returns a result set: PostgreSQL procedures cannot; make it a function RETURNS TABLE (...) or return a refcursor");
        body.Line(Review.Comment("result set not returned; make this a function RETURNS TABLE (...) or use a refcursor"));
        body.Line("-- " + query.Replace("\n", "\n-- ", StringComparison.Ordinal) + ";");
    }

    private string Output(OutputClause? output, OutputIntoClause? into)
    {
        if (into is not null)
        {
            scope.Issues.Add("OUTPUT ... INTO is not translated; use RETURNING ... INTO or a CTE");
            return " " + Review.Comment("OUTPUT INTO not translated", Text(into));
        }

        if (output is null)
        {
            return string.Empty;
        }

        // inserted.x / deleted.x → the new / old row; RETURNING sees the new row.
        var columns = output.SelectColumns.Select(c => c is SelectScalarExpression { Expression: ColumnReferenceExpression col } sc && col.MultiPartIdentifier?.Identifiers.Count == 2
                && col.MultiPartIdentifier.Identifiers[0].Value is { } q && (q.Equals("inserted", StringComparison.OrdinalIgnoreCase) || q.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            ? Id(col.MultiPartIdentifier.Identifiers[1]) + (sc.ColumnName is { } a ? " AS " + ctx.Ident(a.Value) : string.Empty)
            : SelectElement(c));
        if (output.SelectColumns.Any(c => Text(c).Contains("deleted.", StringComparison.OrdinalIgnoreCase)))
        {
            scope.Issues.Add("OUTPUT deleted.* returns the new values with RETURNING; select the old row first if you need it");
        }

        return " RETURNING " + string.Join(", ", columns);
    }

    private void Insert(InsertStatement i, Body body, string? returningInto = null)
    {
        var spec = i.InsertSpecification;
        var with = With(i.WithCtesAndXmlNamespaces);
        var columns = spec.Columns.Select(c => c.MultiPartIdentifier.Identifiers[^1].Value).ToList();
        var bits = columns.Select(c => ctx.ColumnKindOf(c) == ConversionContext.ColumnKind.Boolean).ToList();
        string source = spec.InsertSource switch
        {
            ValuesInsertSource { IsDefaultValues: true } => "DEFAULT VALUES",
            ValuesInsertSource v => "VALUES " + string.Join(", ", v.RowValues.Select(r => "(" + string.Join(", ", r.ColumnValues.Select((x, n) => Value(x, n < bits.Count && bits[n]))) + ")")),
            SelectInsertSource s => Query(s.Select),
            _ => Unsupported(spec.InsertSource, "INSERT ... EXEC is not translated; call a function that RETURNS TABLE", "SELECT NULL WHERE false"),
        };

        if (body.ResultVariable is { } result && spec.Target is VariableTableReference vt && vt.Variable.Name.Equals(result, StringComparison.OrdinalIgnoreCase))
        {
            body.Line($"RETURN QUERY {with}{(source.StartsWith("VALUES", StringComparison.Ordinal) ? source : source)};");
            return;
        }

        var target = spec.Target switch
        {
            NamedTableReference n => ObjectName(n.SchemaObject),
            VariableTableReference v => ctx.Quote(VariableName(v.Variable.Name, "t_")),
            _ => Unsupported(spec.Target, "INSERT target not translated", "untranslated"),
        };
        var list = columns.Count == 0 ? string.Empty : " (" + string.Join(", ", spec.Columns.Select(c => Id(c.MultiPartIdentifier.Identifiers[^1]))) + ")";
        body.Line($"{with}INSERT INTO {target}{list} {source}{returningInto ?? Output(spec.OutputClause, spec.OutputIntoClause)};");
        body.Line(RowCountMarker);
    }

    /// <summary>The real table and the name the statement uses for it (its alias in FROM, or the table name).</summary>
    private (string Table, string Reference) DmlTarget(TableReference target, FromClause? from)
    {
        if (target is NamedTableReference named)
        {
            var name = named.SchemaObject.BaseIdentifier.Value;
            if (named.SchemaObject.SchemaIdentifier is null && from is not null && FindAlias(from.TableReferences, name) is { } aliased)
            {
                return (ObjectName(aliased.SchemaObject), ctx.Ident(name));
            }

            var table = ObjectName(named.SchemaObject);
            return (table, named.Alias is null ? table : ctx.Ident(named.Alias.Value));
        }

        if (target is VariableTableReference v)
        {
            var t = ctx.Quote(VariableName(v.Variable.Name, "t_"));
            return (t, t);
        }

        return (Unsupported(target, "DML target not translated", "untranslated"), "untranslated");
    }

    private static NamedTableReference? FindAlias(IEnumerable<TableReference> references, string alias)
    {
        foreach (var reference in references)
        {
            switch (reference)
            {
                case NamedTableReference n when n.Alias?.Value.Equals(alias, StringComparison.OrdinalIgnoreCase) == true:
                    return n;
                case JoinTableReference j when FindAlias([j.FirstTableReference, j.SecondTableReference], alias) is { } found:
                    return found;
                case JoinParenthesisTableReference p when FindAlias([p.Join], alias) is { } inner:
                    return inner;
            }
        }

        return null;
    }

    private void Update(UpdateStatement u, Body body)
    {
        var spec = u.UpdateSpecification;
        var with = With(u.WithCtesAndXmlNamespaces);
        var (table, reference) = DmlTarget(spec.Target, spec.FromClause);
        var sets = new List<string>();
        foreach (var clause in spec.SetClauses)
        {
            if (clause is AssignmentSetClause { Column: { } column } a && a.Variable is null)
            {
                var name = column.MultiPartIdentifier.Identifiers[^1];
                var value = Value(a.NewValue, ctx.ColumnKindOf(name.Value) == ConversionContext.ColumnKind.Boolean);
                sets.Add(a.AssignmentKind switch
                {
                    AssignmentKind.AddEquals => $"{Id(name)} = {Id(name)} + {value}",
                    AssignmentKind.SubtractEquals => $"{Id(name)} = {Id(name)} - {value}",
                    _ => $"{Id(name)} = {value}",
                });
            }
            else
            {
                sets.Add(Unsupported(clause, "SET clause not translated", "/* */").Trim());
            }
        }

        string where;
        string fromSql;
        if (spec.FromClause is { } from)
        {
            // UPDATE alias SET ... FROM <joins>: update the table through its own row (ctid) in the joined set.
            fromSql = " FROM " + string.Join(", ", from.TableReferences.Select(TableRef));
            var self = $"bsm_target.ctid = {reference}.ctid";
            where = " WHERE " + self + (spec.WhereClause?.SearchCondition is { } c ? $" AND ({Bool(c)})" : string.Empty);
            body.Line($"{with}UPDATE {table} AS bsm_target SET {string.Join(", ", sets)}{fromSql}{where}{Output(spec.OutputClause, spec.OutputIntoClause)};");
        }
        else
        {
            where = spec.WhereClause?.SearchCondition is { } c ? " WHERE " + Bool(c) : string.Empty;
            body.Line($"{with}UPDATE {table}{(reference != table ? " AS " + reference : string.Empty)} SET {string.Join(", ", sets)}{where}{Output(spec.OutputClause, spec.OutputIntoClause)};");
        }

        if (spec.TopRowFilter is not null)
        {
            body.Line(Review.Comment("UPDATE TOP (n) is not translated: every matching row is updated"));
            scope.Issues.Add("UPDATE TOP (n) is not translated");
        }

        body.Line(RowCountMarker);
    }

    private void Delete(DeleteStatement d, Body body)
    {
        var spec = d.DeleteSpecification;
        var with = With(d.WithCtesAndXmlNamespaces);
        var (table, reference) = DmlTarget(spec.Target, spec.FromClause);
        if (spec.FromClause is { } from)
        {
            var where = $" WHERE bsm_target.ctid = {reference}.ctid" + (spec.WhereClause?.SearchCondition is { } c ? $" AND ({Bool(c)})" : string.Empty);
            body.Line($"{with}DELETE FROM {table} AS bsm_target USING {string.Join(", ", from.TableReferences.Select(TableRef))}{where}{Output(spec.OutputClause, spec.OutputIntoClause)};");
        }
        else
        {
            var where = spec.WhereClause?.SearchCondition is { } c ? " WHERE " + Bool(c) : string.Empty;
            body.Line($"{with}DELETE FROM {table}{(reference != table ? " AS " + reference : string.Empty)}{where}{Output(spec.OutputClause, spec.OutputIntoClause)};");
        }

        body.Line(RowCountMarker);
    }

    private void Execute(ExecuteStatement e, Body body)
    {
        switch (e.ExecuteSpecification.ExecutableEntity)
        {
            case ExecutableProcedureReference p when p.ProcedureReference?.ProcedureReference?.Name is { } name:
            {
                var procName = name.BaseIdentifier.Value;
                if (procName.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                {
                    body.Line($"EXECUTE {(p.Parameters.Count > 0 ? Expr(p.Parameters[0].ParameterValue) : "''")};" + Note("dynamic SQL: the statement text is still T-SQL, and its parameters are not passed"));
                    break;
                }

                if (procName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || procName.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))
                {
                    body.Line(Review.Comment($"system procedure {procName} has no PostgreSQL counterpart", Text(e)));
                    scope.Issues.Add($"system procedure {procName} is not translated");
                    break;
                }

                var args = p.Parameters.Select(a => (a.Variable is null ? string.Empty : ctx.Quote(VariableName(a.Variable.Name, "p_")) + " => ") + Expr(a.ParameterValue));
                if (e.ExecuteSpecification.Variable is not null)
                {
                    scope.Issues.Add($"the return status of {procName} is not captured");
                }

                body.Line($"CALL {ObjectName(name)}({string.Join(", ", args)});");
                break;
            }

            case ExecutableStringList s:
                body.Line($"EXECUTE {string.Join(" || ", s.Strings.Select(Expr))};" + Note("dynamic SQL: the statement text is still T-SQL"));
                break;
            default:
                body.Line(Review.Comment("EXEC not translated", Text(e)));
                scope.Issues.Add($"not translated: {Collapse(Text(e))}");
                break;
        }
    }
}
