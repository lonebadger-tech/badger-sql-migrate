using System.Globalization;
using System.Text.Json.Nodes;
using Badger.Trek.Core;

namespace Badger.Trek.Providers.Oracle;

/// <summary>
/// The project's 'oracle:' block in trek.yaml. minVersion is the oldest Oracle Database the project's scripts must run
/// on: 19 (19c, the default) or 26 (26ai, which reports itself as version 23). Lint warns about SQL that only 26ai runs
/// when it is 19.
/// </summary>
public sealed record OracleOptions
{
    public const string SectionName = "oracle";

    public static OracleOptions Default { get; } = new();

    /// <summary>19 for 19c, 23 for 26ai (its version number is 23.26).</summary>
    public int MinVersion { get; init; } = 19;

    /// <summary>Syntax that 26ai added (IF [NOT] EXISTS, BOOLEAN columns, ...) may be used.</summary>
    public bool Supports26ai => MinVersion >= 23;

    public static OracleOptions Parse(JsonObject? section)
    {
        if (section is null)
        {
            return Default;
        }

        var options = Default;
        foreach (var (key, value) in section)
        {
            if (!string.Equals(key, "minVersion", StringComparison.OrdinalIgnoreCase))
            {
                throw new MigrationException($"trek.yaml: unknown setting 'oracle.{key}'. Known: minVersion.");
            }

            var text = value is JsonValue v ? Convert.ToString(v.GetValue<object>(), CultureInfo.InvariantCulture)?.Trim().TrimEnd('c', 'C') : null;
            text = text?.Replace("ai", string.Empty, StringComparison.OrdinalIgnoreCase);
            options = text switch
            {
                "19" => options with { MinVersion = 19 },
                "23" or "26" => options with { MinVersion = 23 },
                _ => throw new MigrationException($"trek.yaml: oracle.minVersion '{value}' is not a supported Oracle Database release: use 19 (19c) or 26 (26ai)."),
            };
        }

        return options;
    }
}
