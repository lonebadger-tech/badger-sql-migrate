using System.Text.RegularExpressions;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Conversion.Sqlite;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.MySql;

/// <summary>
/// PostgreSQL to MySQL, and the last step of SQL Server and SQLite to MySQL. Expressions (defaults, checks, generated
/// columns) and views are printed as MySQL by <see cref="SqlPrinter"/>. Functions, procedures and triggers are written
/// commented out for now: PL/pgSQL is not translated to MySQL's procedural SQL yet.
/// </summary>
internal sealed class PgToMySqlTranslator(ConversionContext ctx) : ITranslator
{
    public string Expression(string expression, ExpressionSite site)
    {
        var result = Translate(expression, site.Owner, "NULL");

        // A DATETIME(6) default must say CURRENT_TIMESTAMP(6): MySQL rejects a precision that differs from the column's.
        if (result == "CURRENT_TIMESTAMP" && site.TargetType is { } type && Regex.Match(type, @"^\s*(?:datetime|timestamp)\s*\(\s*(?<p>[1-6])\s*\)", RegexOptions.IgnoreCase) is { Success: true } m)
        {
            result = $"CURRENT_TIMESTAMP({m.Groups["p"].Value})";
        }

        return result;
    }

    public string Predicate(string predicate, ExpressionSite site) => Translate(predicate, site.Owner, "TRUE");

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
        if (code.Kind == PortableCodeKind.View)
        {
            try
            {
                script = View(code, issues);
            }
            catch (PgParseException ex)
            {
                issues.Add($"could not parse: {ex.Message}");
                script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for MySQL"));
            }
        }
        else
        {
            // PL/pgSQL to MySQL's procedural SQL is not translated yet.
            issues.Add($"{code.Kind.ToString().ToLowerInvariant()} not translated; rewrite it for MySQL");
            script = Review.CommentedOut(code.Definition, Review.Comment("not translated; rewrite it for MySQL"));
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
            issues.Add("materialized view became a plain view (MySQL has no materialized views)");
        }

        var name = ctx.Qualified(code.Name);
        var columns = m.Success && m.Groups["cols"].Success
            ? " (" + string.Join(", ", m.Groups["cols"].Value.Split(',').Select(c => ctx.Ident(c.Trim().Trim('"')))) + ")"
            : string.Empty;
        return $"CREATE OR REPLACE VIEW {name}{columns} AS\n{new SqlPrinter(ctx, issues).Query(query)};\n";
    }
}
