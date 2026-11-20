using System.Text;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Sqlite;

/// <summary>
/// SQLite to PostgreSQL. Expressions and views are parsed in SQLite mode and printed as PostgreSQL; a SQLite trigger
/// (one event, row level, a list of statements) becomes a PL/pgSQL trigger function and a CREATE TRIGGER, with
/// SELECT RAISE(ABORT, ...) turned into RAISE EXCEPTION and RAISE(IGNORE) into RETURN NULL.
/// </summary>
internal sealed class SqliteToPgTranslator(ConversionContext ctx) : IIntermediateTranslator
{
    public string Expression(string expression, ExpressionSite site) => Translate(expression, site, predicate: false);

    public string Predicate(string predicate, ExpressionSite site) => Translate(predicate, site, predicate: true);

    private string Translate(string text, ExpressionSite site, bool predicate)
    {
        var issues = new List<string>();
        string result;
        try
        {
            var e = new PgParser(text, sqlite: true).ExpressionOnly();
            result = new SqlPrinter(ctx, issues).Expr(e);
            if (!predicate && site.TargetType is { } type && (type.Equals("boolean", StringComparison.OrdinalIgnoreCase) || type.Equals("bit", StringComparison.OrdinalIgnoreCase)) && result is "0" or "1")
            {
                result = result == "1" ? "true" : "false";
            }
        }
        catch (PgParseException ex)
        {
            issues.Add($"could not parse {text}: {ex.Message}");
            result = (predicate ? "true " : "NULL ") + Review.Comment("could not translate", text);
        }

        foreach (var issue in issues.Distinct())
        {
            ctx.Issue(site.Owner, issue);
        }

        return result;
    }

    public IReadOnlyList<PortableCode> Intermediate(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var issues = new List<string>();
        var result = Translate(code, issues).Select(p => p.Code).ToList();
        foreach (var issue in issues.Distinct())
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        return result;
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var issues = new List<string>();
        var parts = Translate(code, issues);
        var script = string.Join("\n", parts.Select(p => p.Script));
        issues = issues.Distinct().ToList();
        foreach (var issue in issues)
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        var target = ctx.TargetName(code.Name);
        var folder = code.Kind == PortableCodeKind.View ? "views" : "triggers";
        return new TranslatedCode(code, folder, $"{target.Schema}.{target.Name}.sql", Header("SQLite", code, issues) + script.TrimEnd() + "\n", issues);
    }

    internal static string Header(string source, PortableCode code, IReadOnlyList<string> issues)
    {
        var header = new StringBuilder($"-- Converted by trek convert from {source} {code.Kind.ToString().ToLowerInvariant()} {code.Name}.");
        header.Append(issues.Count == 0 ? " Check it before deploying.\n" : $" {issues.Count} item(s) to review (search for '{Review.Marker}').\n");
        foreach (var issue in issues)
        {
            header.Append("-- * ").Append(issue.Replace('\n', ' ')).Append('\n');
        }

        return header.ToString();
    }

    /// <summary>Intermediate definitions (CREATE VIEW / function + CREATE TRIGGER) and the re-runnable script for each.</summary>
    private List<(PortableCode Code, string Script)> Translate(PortableCode code, List<string> issues)
    {
        var printer = new SqlPrinter(ctx, issues);
        var target = ctx.TargetName(code.Name);
        var qualified = ctx.Qualified(code.Name);
        try
        {
            if (code.Kind == PortableCodeKind.View)
            {
                var (_, columns, query) = new PgParser(code.Definition, sqlite: true).SqliteView();
                var columnList = columns.Count == 0 ? string.Empty : " (" + string.Join(", ", columns.Select(ctx.Ident)) + ")";
                var body = printer.Query(query);
                return [(new PortableCode(PortableCodeKind.View, target, $"CREATE VIEW {qualified}{columnList} AS\n{body};") { Language = "sql" },
                    $"CREATE OR REPLACE VIEW {qualified}{columnList} AS\n{body};\n")];
            }

            if (code.Kind == PortableCodeKind.Trigger)
            {
                return Trigger(code, printer, issues);
            }
        }
        catch (PgParseException ex)
        {
            issues.Add($"could not parse: {ex.Message}");
        }

        var commented = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for PostgreSQL"));
        return [(code with { Definition = commented }, commented)];
    }

    private List<(PortableCode, string)> Trigger(PortableCode code, SqlPrinter printer, List<string> issues)
    {
        var t = new PgParser(code.Definition, sqlite: true).SqliteTrigger();
        var schema = ctx.Names.Schema(code.Table?.Schema ?? ctx.Schema.DefaultSchema);
        var table = printer.Table(t.Table);
        var name = ctx.Names.Name(t.Name);
        var function = new PortableName(schema, name + "_fn");
        var functionName = $"{ctx.Quote(schema)}.{ctx.Quote(function.Name)}";

        var body = new StringBuilder();
        foreach (var statement in t.Body)
        {
            body.Append(Statement(statement, printer, issues, t.Timing)).Append('\n');
        }

        var returns = t.Timing == "AFTER" ? "NULL" : t.Event == "DELETE" ? "OLD" : "NEW";
        var functionDef = $"CREATE OR REPLACE FUNCTION {functionName}()\nRETURNS trigger\nLANGUAGE plpgsql\nAS $body$\nBEGIN\n{body}    RETURN {returns};\nEND\n$body$;";
        var events = t.Event + (t.Columns.Count > 0 ? " OF " + string.Join(", ", t.Columns.Select(ctx.Ident)) : string.Empty);
        var when = t.When is null ? string.Empty : $" WHEN ({printer.Expr(t.When)})";
        var triggerDef = $"CREATE TRIGGER {ctx.Quote(name)} {t.Timing} {events} ON {table} FOR EACH ROW{when} EXECUTE FUNCTION {functionName}();";
        var owner = code.Table is { } tbl ? ctx.TargetName(tbl) : new PortableName(schema, t.Table.Last);
        return
        [
            (new PortableCode(PortableCodeKind.Function, function, functionDef) { Language = "plpgsql" }, functionDef + "\n"),
            (new PortableCode(PortableCodeKind.Trigger, new PortableName(schema, name), triggerDef) { Language = "plpgsql", Table = owner, TriggerFunction = function },
                $"DROP TRIGGER IF EXISTS {ctx.Quote(name)} ON {table};\n{triggerDef}\n"),
        ];
    }

    /// <summary>One statement of a SQLite trigger body as PL/pgSQL.</summary>
    private static string Statement(PgStatement statement, SqlPrinter printer, List<string> issues, string timing)
    {
        const string Indent = "    ";
        if (statement is PgSelectStatement { Query: { Body: PgSelectCore core } query } && Raise(core) is { } raise)
        {
            // SELECT RAISE(ABORT, 'message') [FROM ...] [WHERE condition], or SELECT CASE WHEN c THEN RAISE(...) END.
            var (kind, message, condition) = raise;
            var action = kind == "ignore"
                ? "RETURN NULL;"
                : $"RAISE EXCEPTION '%', {(message is null ? "'error'" : printer.Expr(message))};";
            string? guard = condition is null ? null : printer.Expr(condition);
            if (core.From.Count > 0)
            {
                var from = string.Join(", ", core.From.Select(printer.From));
                guard = $"EXISTS (SELECT 1 FROM {from}{(core.Where is null ? string.Empty : " WHERE " + printer.Expr(core.Where))})";
            }
            else if (core.Where is not null)
            {
                guard = guard is null ? printer.Expr(core.Where) : $"({printer.Expr(core.Where)}) AND ({guard})";
            }

            _ = query;
            return guard is null ? Indent + action : $"{Indent}IF {guard} THEN\n{Indent}    {action}\n{Indent}END IF;";
        }

        return statement switch
        {
            PgSelectStatement sel => Indent + "PERFORM " + printer.Query(sel.Query)["SELECT ".Length..] + ";",
            PgInsert or PgUpdate or PgDelete => Indent + printer.Statement(statement),
            _ => Indent + Review.Comment("statement not translated", statement.Source) + Track(issues, $"not translated: {statement.Source}"),
        };
    }

    private static string Track(List<string> issues, string issue)
    {
        issues.Add(issue);
        return string.Empty;
    }

    /// <summary>A SELECT whose only target is RAISE(...), directly or as CASE WHEN c THEN RAISE(...) END.</summary>
    private static (string Kind, PgExpr? Message, PgExpr? Condition)? Raise(PgSelectCore core)
    {
        if (core.Targets is not [var target])
        {
            return null;
        }

        static (string, PgExpr?)? Call(PgExpr e) =>
            e is PgCall { Function.Last: var n, Args: var args } && n.Equals("raise", StringComparison.OrdinalIgnoreCase) && args.Count >= 1
                ? (args[0] is PgName k ? k.Last.ToLowerInvariant() : "abort", args.Count > 1 ? args[1] : null)
                : null;

        if (Call(target.Expr) is { } direct)
        {
            return (direct.Item1, direct.Item2, null);
        }

        if (target.Expr is PgCase { Input: null, Whens: [var when], Else: null or PgLiteral { Kind: LiteralKind.Null } } && Call(when.Then) is { } inCase)
        {
            return (inCase.Item1, inCase.Item2, when.When);
        }

        return null;
    }
}
