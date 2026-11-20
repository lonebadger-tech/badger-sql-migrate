using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.MySql;

/// <summary>
/// PostgreSQL to MySQL. Placeholder: expressions are kept for review and code objects are written commented out, until
/// the PostgreSQL-to-MySQL printer exists.
/// </summary>
internal sealed class PgToMySqlTranslator(ConversionContext ctx) : ITranslator
{
    public string Expression(string expression, ExpressionSite site) => Untranslated(expression, site, "NULL");

    public string Predicate(string predicate, ExpressionSite site) => Untranslated(predicate, site, "TRUE");

    private string Untranslated(string text, ExpressionSite site, string placeholder)
    {
        ctx.Issue(site.Owner, $"expression not translated to MySQL: {text}");
        return placeholder + " " + Review.Comment("not translated to MySQL", text);
    }

    public TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all)
    {
        var target = ctx.TargetName(code.Name);
        var message = $"{code.Kind.ToString().ToLowerInvariant()} not translated to MySQL; rewrite it";
        ctx.Issue(code.Name.ToString(), message);
        var folder = code.Kind switch
        {
            PortableCodeKind.View => "views",
            PortableCodeKind.Procedure => "procedures",
            PortableCodeKind.Trigger => "triggers",
            _ => "functions",
        };
        return new TranslatedCode(code, folder, $"{target.Name}.sql", Review.CommentedOut(code.Definition, Review.Comment(message)) + "\n", [message]);
    }
}
