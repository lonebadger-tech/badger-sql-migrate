using System.Text;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion;

/// <summary>
/// Translates through PostgreSQL: SQL Server → PostgreSQL → SQLite, SQLite → PostgreSQL → SQL Server, and to or from MySQL. The first step
/// maps every name the way the whole conversion does; the second keeps names and only changes dialect, working from a
/// PostgreSQL view of the schema (tables with mapped names and PostgreSQL types) and PostgreSQL definitions of the code.
/// </summary>
internal sealed class ChainTranslator : ITranslator
{
    private readonly ConversionContext outer;
    private readonly ConversionContext first;
    private readonly ConversionContext second;
    private readonly IIntermediateTranslator stage1;
    private readonly ITranslator stage2;
    private Dictionary<PortableCode, IReadOnlyList<PortableCode>>? intermediates;
    private int firstIssues;
    private int secondIssues;

    public ChainTranslator(ConversionContext outer)
    {
        this.outer = outer;
        first = new ConversionContext(outer.Schema, new ConversionOptions
        {
            TargetProvider = "postgresql",
            Names = outer.Names.Style,
            CaseSensitive = !outer.CaseInsensitiveCollation,
        });
        stage1 = outer.Source switch
        {
            Dialect.SqlServer => new TSqlToPg.TSqlToPgTranslator(first),
            Dialect.Sqlite => new Sqlite.SqliteToPgTranslator(first),
            Dialect.MySql => new MySql.MySqlToPgTranslator(first),
            _ => throw new InvalidOperationException($"No chain from {outer.Source}."),
        };
        second = new ConversionContext(IntermediateSchema(first), new ConversionOptions
        {
            TargetProvider = ConversionContext.ProviderOf(outer.Target),
            Names = NameStyle.Keep,
            CaseSensitive = !outer.CaseInsensitiveCollation,
        });
        stage2 = outer.Target switch
        {
            Dialect.Sqlite => new Sqlite.PgToSqliteTranslator(second),
            Dialect.SqlServer => new PgToTSqlTranslator(second),
            Dialect.MySql => new MySql.PgToMySqlTranslator(second),
            _ => throw new InvalidOperationException($"No chain to {outer.Target}."),
        };
    }

    /// <summary>The source schema as PostgreSQL: names mapped, types translated.</summary>
    private static PortableSchema IntermediateSchema(ConversionContext first)
    {
        var schema = new PortableSchema
        {
            SourceProvider = "postgresql",
            DefaultSchema = "public",
            CaseInsensitive = first.Schema.CaseInsensitive,
            Collation = first.Schema.Collation,
        };
        schema.Schemas.AddRange(first.Schema.Schemas.Select(first.Names.Schema).Where(s => s != "public").Distinct(StringComparer.Ordinal));
        foreach (var t in first.Schema.Tables)
        {
            PortableName N(PortableName n) => first.TargetName(n);
            schema.Tables.Add(new PortableTable(
                N(t.Name),
                t.Columns.Select(c => new PortableColumn(first.Names.Name(c.Name), TypeMapper.Map(c.Type, first).Type, c.Nullable) { Identity = c.Identity }).ToList(),
                t.PrimaryKey is { } pk ? pk with { Columns = pk.Columns.Select(first.Names.Name).ToList() } : null,
                t.Uniques.Select(u => u with { Columns = u.Columns.Select(first.Names.Name).ToList() }).ToList(),
                [],
                t.ForeignKeys.Select(f => f with { Columns = f.Columns.Select(first.Names.Name).ToList(), Referenced = N(f.Referenced), ReferencedColumns = f.ReferencedColumns.Select(first.Names.Name).ToList() }).ToList(),
                []));
        }

        schema.Domains.AddRange(first.Schema.Domains.Select(d => d with { Name = first.TargetName(d.Name), BaseType = TypeMapper.Map(d.BaseType, first).Type }));
        return schema;
    }

    /// <summary>Copies the issues the inner steps recorded since the last call to the conversion's list.</summary>
    private void Merge(string owner)
    {
        foreach (var issue in first.Issues.Skip(firstIssues).Concat(second.Issues.Skip(secondIssues)))
        {
            outer.Issue(owner, issue.Message);
        }

        firstIssues = first.Issues.Count;
        secondIssues = second.Issues.Count;
    }

    public string Expression(string expression, ExpressionSite site)
    {
        var pg = stage1.Expression(expression, site);
        var result = stage2.Expression(pg, site);
        Merge(site.Owner);
        return result;
    }

    public string Predicate(string predicate, ExpressionSite site)
    {
        var pg = stage1.Predicate(predicate, site);
        var result = stage2.Predicate(pg, site);
        Merge(site.Owner);
        return result;
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        if (intermediates is null)
        {
            intermediates = new Dictionary<PortableCode, IReadOnlyList<PortableCode>>(ReferenceEqualityComparer.Instance);
            foreach (var c in all)
            {
                var before = first.Issues.Count;
                intermediates[c] = stage1.Intermediate(c, all);
                foreach (var issue in first.Issues.Skip(before))
                {
                    outer.Issue(c.Name.ToString(), issue.Message);
                }
            }

            firstIssues = first.Issues.Count;
            second.Schema.Code.AddRange(intermediates.Values.SelectMany(v => v));
        }

        var parts = intermediates.GetValueOrDefault(code) ?? stage1.Intermediate(code, all);
        var everything = second.Schema.Code;
        var results = parts.Select(p => stage2.Code(p, everything)).Where(r => r.Script.Length > 0).ToList();
        var stage1Issues = outer.Issues.Where(i => i.Object == code.Name.ToString()).Select(i => i.Message);
        Merge(code.Name.ToString());
        if (results.Count == 0)
        {
            return new TranslatedCode(code, string.Empty, string.Empty, string.Empty, []);
        }

        var issues = stage1Issues.Concat(results.SelectMany(r => r.Issues)).Distinct().ToList();
        var body = string.Join("\n", results.Select(r => string.Join('\n', r.Script.Split('\n').SkipWhile(l => l.StartsWith("--", StringComparison.Ordinal)))));
        var source = outer.Source == Dialect.SqlServer ? "SQL Server" : "SQLite";
        var header = new StringBuilder($"-- Converted by trek convert from {source} {code.Kind.ToString().ToLowerInvariant()} {code.Name} (by way of PostgreSQL).");
        header.Append(issues.Count == 0 ? " Check it before deploying.\n" : $" {issues.Count} item(s) to review (search for '{Review.Marker}').\n");
        foreach (var issue in issues)
        {
            header.Append("-- * ").Append(issue.Replace('\n', ' ')).Append('\n');
        }

        var target = outer.TargetName(code.Name);
        var fileName = outer.Target == Dialect.Sqlite ? $"{target.Name}.sql" : $"{target.Schema}.{target.Name}.sql";
        return new TranslatedCode(code, results[0].Folder, fileName, header + body.TrimEnd() + "\n", issues);
    }
}
