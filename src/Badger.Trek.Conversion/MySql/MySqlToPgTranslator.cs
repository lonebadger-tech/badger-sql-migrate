using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Conversion.Sqlite;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.MySql;

/// <summary>
/// MySQL to PostgreSQL, and the first step of MySQL to SQL Server and SQLite. Expressions (column defaults, checks,
/// generated columns as information_schema reports them) and views (SHOW CREATE VIEW) are parsed in MySQL mode and
/// printed as PostgreSQL. Stored routines and triggers are written commented out for now: MySQL's procedural SQL is
/// not translated yet.
/// </summary>
internal sealed class MySqlToPgTranslator(ConversionContext ctx) : IIntermediateTranslator
{
    public string Expression(string expression, ExpressionSite site) => Translate(expression, site, predicate: false);

    public string Predicate(string predicate, ExpressionSite site) => Translate(predicate, site, predicate: true);

    private string Translate(string text, ExpressionSite site, bool predicate)
    {
        var issues = new List<string>();
        string result;
        try
        {
            var e = new PgParser(text, ParseMode.MySql).ExpressionOnly();
            result = new SqlPrinter(ctx, issues).Expr(e);

            // MySQL booleans are tinyint(1): a default of 1 or 0 lands in a boolean column.
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
        var result = new List<PortableCode>();
        if (code.Kind == PortableCodeKind.View && View(code, issues) is { } view)
        {
            if (view.CheckOption is not null)
            {
                issues.Add("WITH CHECK OPTION was dropped");
            }

            result.Add(new PortableCode(PortableCodeKind.View, ctx.TargetName(code.Name), $"CREATE VIEW {view.Name}{view.Columns} AS\n{view.Body};") { Language = "sql" });
        }
        else if (code.Kind == PortableCodeKind.View)
        {
            // Unparsable: passed on commented out, so the next step writes it for review too.
            result.Add(code with { Name = ctx.TargetName(code.Name), Definition = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for PostgreSQL")) });
        }
        else
        {
            issues.Add(NotTranslated(code));
        }

        foreach (var issue in issues.Distinct())
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        return result;
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var issues = new List<string>();
        string script;
        if (code.Kind == PortableCodeKind.View && View(code, issues) is { } view)
        {
            var check = view.CheckOption is null ? string.Empty : $"\nWITH {view.CheckOption} CHECK OPTION";
            script = $"CREATE OR REPLACE VIEW {view.Name}{view.Columns} AS\n{view.Body}{check};\n";
        }
        else
        {
            if (code.Kind != PortableCodeKind.View)
            {
                issues.Add(NotTranslated(code));
            }

            script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for PostgreSQL"));
        }

        issues = issues.Distinct().ToList();
        foreach (var issue in issues)
        {
            ctx.Issue(code.Name.ToString(), issue);
        }

        var target = ctx.TargetName(code.Name);
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        return new TranslatedCode(code, folder, $"{target.Schema}.{target.Name}.sql", SqliteToPgTranslator.Header(ctx.SourceDisplay, code, issues) + script.TrimEnd() + "\n", issues);
    }

    private string NotTranslated(PortableCode code) => $"{ctx.SourceDisplay} {code.Kind.ToString().ToLowerInvariant()} not translated; rewrite it for PostgreSQL";

    /// <summary>The view's PostgreSQL name, column list and query, or null (with an issue) when it cannot be parsed.</summary>
    private (string Name, string Columns, string Body, string? CheckOption)? View(PortableCode code, List<string> issues)
    {
        try
        {
            var view = new PgParser(code.Definition, ParseMode.MySql).MySqlView();
            var columns = view.Columns.Count == 0 ? string.Empty : " (" + string.Join(", ", view.Columns.Select(ctx.Ident)) + ")";
            return (ctx.Qualified(code.Name), columns, new SqlPrinter(ctx, issues).Query(view.Query), view.CheckOption);
        }
        catch (PgParseException ex)
        {
            issues.Add($"could not parse: {ex.Message}");
            return null;
        }
    }
}
