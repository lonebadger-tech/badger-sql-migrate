using System.Globalization;
using System.Text.Json.Nodes;
using Badger.SqlMigrate.Core;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>
/// The project's 'sqlite:' block in bsm.yaml. bsm itself always runs the SQLite engine it ships with, but other programs
/// open the same database file with whatever SQLite they were built with, often years older (3.26 on RHEL 8, 3.34 on
/// RHEL 9). minVersion is the oldest of those: generated scripts use only SQL it understands, and lint warns about SQL
/// that needs a newer engine.
/// </summary>
public sealed record SqliteOptions
{
    public const string SectionName = "sqlite";

    /// <summary>The oldest SQLite bsm supports: what RHEL 8 ships.</summary>
    public static readonly Version Oldest = new(3, 26);

    /// <summary>ALTER TABLE ... DROP COLUMN and RETURNING.</summary>
    public static readonly Version DropColumn = new(3, 35);

    public static SqliteOptions Default { get; } = new();

    public Version MinVersion { get; init; } = Oldest;

    public bool Supports(Version feature) => MinVersion >= feature;

    public static SqliteOptions Parse(JsonObject? section)
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
                throw new MigrationException($"bsm.yaml: unknown setting 'sqlite.{key}'. Known: minVersion.");
            }

            var text = value is JsonValue v ? Convert.ToString(v.GetValue<object>(), CultureInfo.InvariantCulture)?.Trim() : null;

            // Unquoted, YAML reads 3.30 as the number 3.3: a one-digit minor version from a number is ambiguous.
            if (value is JsonValue n && n.TryGetValue<double>(out _) && text is { } number && number.Split('.') is [_, { Length: 1 }])
            {
                throw new MigrationException($"bsm.yaml: write sqlite.minVersion in quotes (\"{number}0\" or \"{number}\"): unquoted, YAML reads 3.30 as 3.3.");
            }

            if (!Version.TryParse(text, out var version) || version.Major != 3)
            {
                throw new MigrationException($"bsm.yaml: sqlite.minVersion '{text}' is not a SQLite 3 version such as \"3.35\" (quote it so YAML keeps it as text).");
            }

            if (version < Oldest)
            {
                throw new MigrationException($"bsm.yaml: sqlite.minVersion {text} is older than the oldest SQLite bsm supports ({Oldest}).");
            }

            options = options with { MinVersion = new Version(version.Major, version.Minor) };
        }

        return options;
    }
}
