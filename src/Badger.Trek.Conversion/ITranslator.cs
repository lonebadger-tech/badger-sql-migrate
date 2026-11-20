using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion;

/// <summary>Where an expression appears, so the translator can pick the right form (e.g. true instead of 1 for a boolean default).</summary>
/// <param name="Owner">The object it belongs to (for issues), e.g. "dbo.Customer".</param>
/// <param name="TargetType">The type the expression's value lands in, in the target dialect, when known.</param>
public sealed record ExpressionSite(string Owner, string? TargetType = null);

/// <param name="Folder">Repeatable folder: views, functions, procedures, triggers.</param>
/// <param name="Script">The complete, re-runnable script in the target dialect.</param>
/// <param name="Issues">What a person has to check; each also appears as a REVIEW comment in the script.</param>
public sealed record TranslatedCode(PortableCode Source, string Folder, string FileName, string Script, IReadOnlyList<string> Issues);

/// <summary>Rewrites one dialect's SQL in another's. One implementation per (source, target) pair.</summary>
public interface ITranslator
{
    string Expression(string expression, ExpressionSite site);

    /// <summary>A boolean expression (CHECK constraint, filtered-index predicate).</summary>
    string Predicate(string predicate, ExpressionSite site);

    TranslatedCode Code(PortableCode code, IReadOnlyList<PortableCode> all);
}

internal static class Review
{
    public const string Marker = "REVIEW trek convert";

    /// <summary>A comment that flags something for a person, safe to embed in SQL.</summary>
    public static string Comment(string message, string? original = null) =>
        original is null
            ? $"/* {Marker}: {Safe(message)} */"
            : $"/* {Marker}: {Safe(message)}: {Safe(original.Trim())} */";

    private static string Safe(string text) => text.Replace("*/", "* /", StringComparison.Ordinal).Replace("/*", "/ *", StringComparison.Ordinal);

    /// <summary>A body that could not be translated at all: the original, commented out, after a REVIEW line.</summary>
    public static string CommentedOut(string original, string comment) =>
        $"{comment}\n" + string.Join('\n', original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => "-- " + l));
}
