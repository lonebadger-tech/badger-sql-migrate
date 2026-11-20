using System.Text.RegularExpressions;

namespace Badger.Trek.Conversion.PgToTSql;

/// <summary>A PostgreSQL CREATE TRIGGER statement, as pg_get_triggerdef prints it.</summary>
internal sealed partial record PgTriggerDefinition(string Name, string When, IReadOnlyList<string> Events, IReadOnlyList<string> UpdateOf, bool Row, string? Condition, string Rest, string Arguments)
{
    /// <summary>REFERENCING NEW TABLE AS x / OLD TABLE AS y: alias → "new" or "old".</summary>
    public Dictionary<string, string> TransitionTables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static PgTriggerDefinition? Parse(string definition)
    {
        var m = TriggerRegex().Match(definition.Trim());
        if (!m.Success)
        {
            return null;
        }

        var events = Regex.Split(m.Groups["events"].Value, @"\s+OR\s+", RegexOptions.IgnoreCase).Select(e => e.Trim()).ToList();
        var updateOf = events.FirstOrDefault(e => e.StartsWith("UPDATE OF", StringComparison.OrdinalIgnoreCase)) is { } uo
            ? uo[9..].Split(',').Select(c => c.Trim().Trim('"')).ToList()
            : [];
        var result = new PgTriggerDefinition(
            m.Groups["name"].Value.Trim('"'),
            Regex.Replace(m.Groups["when"].Value.ToUpperInvariant(), @"\s+", " "),
            events.Select(e => e.Split(' ')[0].ToUpperInvariant()).Distinct().ToList(),
            updateOf,
            m.Groups["level"].Value.Equals("ROW", StringComparison.OrdinalIgnoreCase),
            m.Groups["cond"].Success ? m.Groups["cond"].Value : null,
            m.Groups["rest"].Value,
            m.Groups["args"].Value.Trim());
        foreach (Match r in Regex.Matches(result.Rest, @"(?<which>NEW|OLD)\s+TABLE\s+AS\s+(?<alias>\S+)", RegexOptions.IgnoreCase))
        {
            result.TransitionTables[r.Groups["alias"].Value.Trim('"')] = r.Groups["which"].Value.ToLowerInvariant();
        }

        return result;
    }

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\s+(?<name>\S+)\s+(?<when>BEFORE|AFTER|INSTEAD\s+OF)\s+(?<events>.+?)\s+ON\s+(?<table>\S+)(?<rest>.*?)\s+FOR\s+EACH\s+(?<level>ROW|STATEMENT)(?:\s+WHEN\s+\((?<cond>.*)\))?\s+EXECUTE\s+(?:FUNCTION|PROCEDURE)\s+(?<fn>[^(]+)\((?<args>.*)\)\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TriggerRegex();
}

/// <summary>A translator whose output can feed another translator (the middle of a chain), as source-like objects.</summary>
internal interface IIntermediateTranslator : ITranslator
{
    /// <summary>The object as PostgreSQL definitions (views: CREATE VIEW; triggers: their function and CREATE TRIGGER), with target names.</summary>
    IReadOnlyList<Core.Conversion.PortableCode> Intermediate(Core.Conversion.PortableCode code, IReadOnlyList<Core.Conversion.PortableCode> all);
}
