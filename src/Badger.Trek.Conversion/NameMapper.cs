using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core;

namespace Badger.Trek.Conversion;

/// <summary>How identifiers are spelled in the target.</summary>
public enum NameStyle
{
    /// <summary>Exactly as the source spells them (PostgreSQL then needs them double-quoted when they have capitals).</summary>
    Keep,

    /// <summary>Lowercased: unquoted references in existing queries keep working on PostgreSQL, which lowercases them too.</summary>
    Lower,

    /// <summary>snake_case: CustomerOrder becomes customer_order, HTTPCode becomes http_code.</summary>
    Snake,
}

/// <summary>
/// Maps every identifier from the source to the target. The default schemas map to each other (dbo and public); the
/// style applies to everything else. All renames are recorded for the conversion report.
/// </summary>
public sealed partial class NameMapper(string sourceDefaultSchema, string targetDefaultSchema, NameStyle style)
{
    private readonly SortedDictionary<string, string> renamed = new(StringComparer.Ordinal);

    public NameStyle Style => style;

    /// <summary>Source name → target name for every identifier that changed.</summary>
    public IReadOnlyDictionary<string, string> Renamed => renamed;

    public string Schema(string name) =>
        string.Equals(name, sourceDefaultSchema, StringComparison.OrdinalIgnoreCase) ? targetDefaultSchema : Name(name);

    /// <summary>Any other identifier: table, column, index, constraint, routine, parameter or variable.</summary>
    public string Name(string name)
    {
        var mapped = Apply(name, style);
        if (!string.Equals(mapped, name, StringComparison.Ordinal))
        {
            renamed[name] = mapped;
        }

        return mapped;
    }

    public static string Apply(string name, NameStyle style) => style switch
    {
        NameStyle.Lower => name.ToLowerInvariant(),
        NameStyle.Snake => Snake(name),
        _ => name,
    };

    public static string Snake(string name)
    {
        var spaced = BoundaryRegex().Replace(name, "_");
        var sb = new StringBuilder(spaced.Length);
        foreach (var c in spaced)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '$' ? char.ToLower(c, CultureInfo.InvariantCulture) : '_');
        }

        return UnderscoresRegex().Replace(sb.ToString(), "_").Trim('_') is { Length: > 0 } s ? s : name.ToLowerInvariant();
    }

    /// <summary>Throws when two source names in one scope map to the same target name (e.g. Order and ORDER, or OrderID and order_id).</summary>
    public void CheckUnique(string scope, IEnumerable<string> sourceNames, Func<string, string> map)
    {
        var clashes = sourceNames.GroupBy(map, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        if (clashes.Count > 0)
        {
            throw new MigrationException($"{scope}: {string.Join("; ", clashes.Select(g => $"{string.Join(", ", g)} would all become '{g.Key}'"))}. Use --names keep, or rename one of them first.");
        }
    }

    // Between a lowercase letter or digit and a capital (orderId), and between capitals before a capitalized word (HTTPCode).
    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex BoundaryRegex();

    [GeneratedRegex("_{2,}")]
    private static partial Regex UnderscoresRegex();
}
