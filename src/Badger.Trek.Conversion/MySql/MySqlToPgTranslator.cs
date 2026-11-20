using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.MySql;

/// <summary>
/// MySQL to PostgreSQL, and the first step of MySQL to SQL Server and SQLite. Placeholder: expressions are kept for
/// review and code objects are written commented out, until the MySQL parser mode exists.
/// </summary>
internal sealed class MySqlToPgTranslator(ConversionContext ctx) : IIntermediateTranslator
{
    public string Expression(string expression, ExpressionSite site) => Untranslated(expression, site, "NULL");

    public string Predicate(string predicate, ExpressionSite site) => Untranslated(predicate, site, "true");

    private string Untranslated(string text, ExpressionSite site, string placeholder)
    {
        ctx.Issue(site.Owner, $"MySQL expression not translated: {text}");
        return placeholder + " " + Review.Comment("not translated from MySQL", text);
    }

    public IReadOnlyList<PortableCode> Intermediate(PortableCode code, IReadOnlyList<PortableCode> all) => [];

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var target = ctx.TargetName(code.Name);
        var message = $"MySQL {code.Kind.ToString().ToLowerInvariant()} not translated; rewrite it";
        ctx.Issue(code.Name.ToString(), message);
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        var file = ctx.FlatTarget ? $"{target.Name}.sql" : $"{target.Schema}.{target.Name}.sql";
        return new TranslatedCode(code, folder, file, Review.CommentedOut(code.Definition, Review.Comment(message)) + "\n", [message]);
    }
}
