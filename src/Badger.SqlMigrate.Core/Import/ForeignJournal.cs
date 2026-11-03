using System.Globalization;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Import;

/// <summary>A row of Flyway's flyway_schema_history, oldest first.</summary>
public sealed record FlywayHistoryRow(int Rank, string? Version, string Type, int? Checksum, bool Success, string Script);

/// <summary>
/// Reads the journal of the tool a project was imported from and works out which imported changes a database
/// already has, so 'bsm baseline' can record them without running anything.
/// </summary>
public static partial class ForeignJournal
{
    public const string FlywayDefaultTable = "flyway_schema_history";
    public const string DbUpDefaultTable = "SchemaVersions";

    public static async Task<IReadOnlyList<FlywayHistoryRow>> ReadFlywayAsync(IDatabaseSession session, IDatabaseProvider provider, string schema, string table, CancellationToken cancellationToken)
    {
        var rows = await Query(session, $"SELECT installed_rank, version, type, checksum, success, script FROM {provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)} ORDER BY installed_rank", schema, table, "Flyway", cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new FlywayHistoryRow(
            System.Convert.ToInt32(r["installed_rank"], CultureInfo.InvariantCulture),
            r["version"] as string,
            (string)r["type"]!,
            r["checksum"] is null ? null : System.Convert.ToInt32(r["checksum"], CultureInfo.InvariantCulture),
            System.Convert.ToBoolean(r["success"], CultureInfo.InvariantCulture),
            (string)r["script"]!)).ToList();
    }

    public static async Task<IReadOnlyList<string>> ReadDbUpAsync(IDatabaseSession session, IDatabaseProvider provider, string schema, string table, CancellationToken cancellationToken)
    {
        var rows = await Query(session, $"SELECT ScriptName FROM {provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)}", schema, table, "DbUp", cancellationToken).ConfigureAwait(false);
        return rows.Select(r => (string)r["ScriptName"]!).ToList();
    }

    /// <summary>
    /// The last imported change Flyway applied (null when none). Everything before it in the plan must be applied
    /// too, Flyway's history must contain no versions the plan lacks, and checksums must match the imported scripts.
    /// </summary>
    public static string? FlywayTarget(MigrationProject project, IReadOnlyList<FlywayHistoryRow> history, bool checkChecksums)
    {
        var applied = new Dictionary<FlywayImporter.Version, FlywayHistoryRow>();
        FlywayImporter.Version? baseline = null;
        foreach (var row in history.Where(r => r.Success && r.Version is not null).OrderBy(r => r.Rank))
        {
            var version = FlywayImporter.Version.Parse(row.Version!);
            switch (row.Type.ToUpperInvariant())
            {
                case "BASELINE" or "SQL_BASELINE" or "JDBC_BASELINE":
                    baseline = version;
                    applied[version] = row;
                    break;
                case var t when t.StartsWith("UNDO", StringComparison.Ordinal) || t == "DELETE":
                    applied.Remove(version);
                    break;
                case "SQL" or "JDBC" or "SPRING_JDBC" or "SCRIPT":
                    applied[version] = row;
                    break;
            }
        }

        var problems = new List<string>();
        var changes = new List<(string Name, FlywayImporter.Version? Version, int? Checksum)>();
        foreach (var change in project.Changes)
        {
            var m = change.Plan.Note is null ? null : FlywayNote().Match(change.Plan.Note);
            changes.Add(m is { Success: true }
                ? (change.Name, FlywayImporter.Version.Parse(m.Groups["v"].Value), m.Groups["c"].Success ? int.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture) : null)
                : (change.Name, null, null));
        }

        var known = changes.Where(c => c.Version is not null).Select(c => c.Version!.Value).ToHashSet();
        foreach (var (version, row) in applied.Where(kv => !known.Contains(kv.Key) && !IsBaselineRow(kv.Value)))
        {
            problems.Add($"Flyway applied V{version} ({row.Script}), but no change in the plan has the note 'flyway V{version}'.");
        }

        bool IsApplied(FlywayImporter.Version v) => applied.ContainsKey(v) || (baseline is { } b && v.CompareTo(b) <= 0);

        var lastApplied = changes.FindLastIndex(c => c.Version is { } v && IsApplied(v));
        for (var i = 0; i <= lastApplied; i++)
        {
            var (name, version, checksum) = changes[i];
            if (version is null)
            {
                problems.Add($"'{name}' comes before changes Flyway applied but did not come from Flyway; move it after them in the plan.");
            }
            else if (!IsApplied(version.Value))
            {
                problems.Add($"'{name}' (V{version}) was not applied by Flyway, but later versions were (outOfOrder). Apply it with Flyway first, or move it after V{changes[lastApplied].Version} in the plan.");
            }
            else if (checkChecksums && checksum is { } expected && applied.TryGetValue(version.Value, out var row) && !IsBaselineRow(row) && row.Checksum != expected)
            {
                problems.Add($"'{name}' (V{version}): Flyway recorded checksum {row.Checksum} but the imported script has {expected}. The database ran a different version of {row.Script}.");
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationException("This database's Flyway history does not match the plan:\n  " + string.Join("\n  ", problems));
        }

        return lastApplied < 0 ? null : changes[lastApplied].Name;
    }

    /// <summary>The last imported change DbUp ran (null when none); every earlier change must have run too.</summary>
    public static string? DbUpTarget(MigrationProject project, IReadOnlyList<string> journal)
    {
        var changes = project.Changes.Select(c => (c.Name, Script: c.Plan.Note is null ? null : DbUpNote().Match(c.Plan.Note) is { Success: true } m ? m.Groups["s"].Value : null)).ToList();
        var problems = new List<string>();
        var ran = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in journal)
        {
            var match = changes.Where(c => c.Script is not null && (entry == c.Script || entry.EndsWith("." + c.Script, StringComparison.Ordinal)))
                .OrderByDescending(c => c.Script!.Length).FirstOrDefault();
            if (match.Name is null)
            {
                // Run-always scripts became repeatables; DbUp journals them too.
                if (!project.Repeatables.Any(r => entry == r.Name || entry.EndsWith(r.Name.Replace('/', '.'), StringComparison.Ordinal) || entry.EndsWith(Path.GetFileName(r.Name), StringComparison.Ordinal)))
                {
                    problems.Add($"DbUp ran '{entry}', but no change in the plan has a matching 'dbup ...' note.");
                }
            }
            else
            {
                ran.Add(match.Name);
            }
        }

        var lastRan = changes.FindLastIndex(c => ran.Contains(c.Name));
        for (var i = 0; i < lastRan; i++)
        {
            if (!ran.Contains(changes[i].Name))
            {
                problems.Add(changes[i].Script is null
                    ? $"'{changes[i].Name}' comes before scripts DbUp ran but did not come from DbUp; move it after them in the plan."
                    : $"'{changes[i].Name}' ({changes[i].Script}) was not run by DbUp, but scripts after it were. Run it with DbUp first, or move it later in the plan.");
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationException("This database's DbUp journal does not match the plan:\n  " + string.Join("\n  ", problems));
        }

        return lastRan < 0 ? null : changes[lastRan].Name;
    }

    private static bool IsBaselineRow(FlywayHistoryRow row) => row.Type.Contains("BASELINE", StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Query(IDatabaseSession session, string sql, string schema, string table, string tool, CancellationToken cancellationToken)
    {
        try
        {
            return await session.QueryAsync(sql, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MigrationException)
        {
            throw new MigrationException($"Could not read {tool}'s journal {schema}.{table}: {ex.Message}", ex);
        }
    }

    [GeneratedRegex(@"\bflyway V(?<v>[0-9][0-9._]*)(?: checksum (?<c>-?[0-9]+))?")]
    private static partial Regex FlywayNote();

    [GeneratedRegex(@"^\s*dbup (?<s>.+?)\s*$")]
    private static partial Regex DbUpNote();
}
