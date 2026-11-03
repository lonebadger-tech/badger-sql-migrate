using System.Globalization;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Import;

/// <summary>The latest grate/RoundhousE ScriptsRun row for a script.</summary>
public sealed record GrateScriptRun(string ScriptName, string? TextHash, bool OneTime);

/// <summary>A change in Sqitch's registry (sqitch.changes): currently deployed, in deploy order.</summary>
public sealed record SqitchChangeRow(string Change, string Project, string? ScriptHash);

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
    public const string GrateDefaultSchema = "grate";
    public const string SqitchDefaultSchema = "sqitch";
    public const string SqitchDefaultTable = "changes";
    public const string GrateDefaultTable = "ScriptsRun";

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

    public static async Task<IReadOnlyList<GrateScriptRun>> ReadGrateAsync(IDatabaseSession session, IDatabaseProvider provider, string schema, string table, CancellationToken cancellationToken)
    {
        var name = $"{provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)}";
        var rows = await Query(session, $"SELECT script_name, text_hash, one_time_script FROM {name} sr WHERE id = (SELECT MAX(id) FROM {name} sr2 WHERE sr2.script_name = sr.script_name)", schema, table, "grate", cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new GrateScriptRun((string)r["script_name"]!, r["text_hash"] as string, System.Convert.ToBoolean(r["one_time_script"] ?? false, CultureInfo.InvariantCulture))).ToList();
    }

    /// <summary>
    /// The last imported change grate ran (null when none). One-time (up/) scripts match by name among one-time rows;
    /// runAfterCreateDatabase/alterDatabase changes among the others. With <paramref name="render"/>, each matched
    /// change's deploy script, rendered for this environment, must hash to grate's recorded text_hash.
    /// </summary>
    public static string? GrateTarget(MigrationProject project, IReadOnlyList<GrateScriptRun> runs, Func<MigrationChange, string>? render)
    {
        var problems = new List<string>();
        var changes = project.Changes.Select(c => (Change: c, Source: c.Plan.Note is null ? null : GrateNote().Match(c.Plan.Note) is { Success: true } m ? (Folder: m.Groups["folder"].Value, Script: m.Groups["script"].Value) : ((string Folder, string Script)?)null)).ToList();
        var ran = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (change, source) in changes)
        {
            if (source is not { } s)
            {
                continue;
            }

            var oneTime = s.Folder.Equals("up", StringComparison.OrdinalIgnoreCase);
            var run = runs.FirstOrDefault(r => r.OneTime == oneTime && string.Equals(r.ScriptName, s.Script, StringComparison.Ordinal));
            if (run is null)
            {
                continue;
            }

            ran.Add(change.Name);
            if (render is not null && !GrateImporter.HashMatches(render(change), run.TextHash))
            {
                problems.Add($"'{change.Name}' ({s.Folder}/{s.Script}): grate recorded a different script (text_hash {run.TextHash}). The database ran another version of it, or a token now has a different value.");
            }
        }

        var known = changes.Where(c => c.Source is { Folder: var f } && f.Equals("up", StringComparison.OrdinalIgnoreCase)).Select(c => c.Source!.Value.Script).ToHashSet(StringComparer.Ordinal);
        foreach (var run in runs.Where(r => r.OneTime && !known.Contains(r.ScriptName) && !r.ScriptName.Contains(".ENV.", StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"grate ran the one-time script '{run.ScriptName}', but no change in the plan has the note 'grate up/{run.ScriptName}'.");
        }

        var last = changes.FindLastIndex(c => ran.Contains(c.Change.Name));
        for (var i = 0; i < last; i++)
        {
            var (change, source) = changes[i];
            if (ran.Contains(change.Name))
            {
                continue;
            }

            if (source is null)
            {
                problems.Add($"'{change.Name}' comes before scripts grate ran but did not come from grate; move it after them in the plan.");
            }
            else if (!source.Value.Folder.Equals("runAfterCreateDatabase", StringComparison.OrdinalIgnoreCase))
            {
                // grate runs runAfterCreateDatabase/ only for databases it created itself, so those may be missing.
                problems.Add($"'{change.Name}' ({source.Value.Folder}/{source.Value.Script}) was not run by grate, but scripts after it were. Run it with grate first, or move it later in the plan.");
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationException("This database's grate journal does not match the plan:\n  " + string.Join("\n  ", problems));
        }

        return last < 0 ? null : changes[last].Change.Name;
    }

    /// <summary>
    /// Repeatables (and environment scripts) grate already ran with exactly this text: the rendered script hashes to
    /// grate's text_hash for the same script name. They are recorded as applied so the first deploy does not rerun them.
    /// </summary>
    public static IReadOnlyList<string> GrateRepeatables(MigrationProject project, IReadOnlyList<GrateScriptRun> runs, Func<RepeatableScript, string> render) =>
        project.Repeatables.Where(r =>
        {
            var name = r.Name.StartsWith('@') ? r.Name[(r.Name.IndexOf('/', StringComparison.Ordinal) + 1)..] : r.Name;
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            var scriptName = slash < 0 ? name : name[(slash + 1)..];
            return runs.Any(run => string.Equals(run.ScriptName, scriptName, StringComparison.Ordinal) && GrateImporter.HashMatches(render(r), run.TextHash));
        }).Select(r => r.Name).ToList();

    /// <summary>Repeatables whose rendered text has the checksum of Flyway's latest successful run of the same R__ script.</summary>
    public static IReadOnlyList<string> FlywayRepeatables(MigrationProject project, IReadOnlyList<FlywayHistoryRow> history, Func<RepeatableScript, string> render)
    {
        var latest = history.Where(h => h.Version is null && h.Success).GroupBy(h => h.Script, StringComparer.Ordinal).Select(g => g.OrderBy(h => h.Rank).Last()).ToList();
        return project.Repeatables.Where(r => latest.Any(h => h.Script.EndsWith("__" + r.Name, StringComparison.Ordinal) && h.Checksum == Hashing.Crc32.Flyway(render(r)))).Select(r => r.Name).ToList();
    }

    public static async Task<IReadOnlyList<SqitchChangeRow>> ReadSqitchAsync(IDatabaseSession session, IDatabaseProvider provider, string schema, string table, CancellationToken cancellationToken)
    {
        var rows = await Query(session, $"SELECT change, project, script_hash FROM {provider.QuoteIdentifier(schema)}.{provider.QuoteIdentifier(table)} ORDER BY committed_at, change_id", schema, table, "Sqitch", cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new SqitchChangeRow((string)r["change"]!, (string)r["project"]!, r["script_hash"] as string)).ToList();
    }

    /// <summary>
    /// The last imported change Sqitch deployed (null when none). Registry rows match notes
    /// "sqitch &lt;project&gt;:&lt;name&gt;[#n] sha1 &lt;hash&gt;" by name and occurrence (reworked changes appear once per
    /// version); with <paramref name="checkHashes"/>, the recorded script_hash must equal the imported script's SHA-1.
    /// </summary>
    public static string? SqitchTarget(MigrationProject project, IReadOnlyList<SqitchChangeRow> registry, bool checkHashes)
    {
        var changes = project.Changes.Select(c => (c.Name, Note: c.Plan.Note is null ? null : SqitchNote().Match(c.Plan.Note) is { Success: true } m
            ? (Project: m.Groups["project"].Success ? m.Groups["project"].Value : null, Change: m.Groups["change"].Value, Occurrence: m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1, Sha1: m.Groups["sha1"].Success ? m.Groups["sha1"].Value : null)
            : ((string? Project, string Change, int Occurrence, string? Sha1)?)null)).ToList();
        var projects = changes.Where(c => c.Note?.Project is not null).Select(c => c.Note!.Value.Project!).Distinct(StringComparer.Ordinal).ToList();
        var rows = registry.Where(r => projects.Count == 0 || projects.Contains(r.Project, StringComparer.Ordinal)).ToList();

        var problems = new List<string>();
        var ran = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var occurrence = seen[row.Change] = seen.GetValueOrDefault(row.Change) + 1;
            var match = changes.FirstOrDefault(c => c.Note is { } n && n.Change == row.Change && n.Occurrence == occurrence);
            if (match.Name is null)
            {
                problems.Add($"Sqitch deployed '{row.Project}:{row.Change}'{(occurrence > 1 ? $" (version {occurrence})" : string.Empty)}, but no change in the plan has a matching 'sqitch ...' note.");
                continue;
            }

            ran.Add(match.Name);
            if (checkHashes && match.Note!.Value.Sha1 is { } sha1 && row.ScriptHash is { } recorded && !string.Equals(sha1, recorded, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"'{match.Name}': Sqitch recorded script hash {recorded}, but the imported deploy script has {sha1}. The database ran a different version of it.");
            }
        }

        var last = changes.FindLastIndex(c => ran.Contains(c.Name));
        for (var i = 0; i < last; i++)
        {
            if (!ran.Contains(changes[i].Name))
            {
                problems.Add(changes[i].Note is null
                    ? $"'{changes[i].Name}' comes before changes Sqitch deployed but did not come from Sqitch; move it after them in the plan."
                    : $"'{changes[i].Name}' was not deployed by Sqitch, but changes after it were. Deploy it with Sqitch first, or move it later in the plan.");
            }
        }

        if (problems.Count > 0)
        {
            throw new MigrationException("This database's Sqitch registry does not match the plan:\n  " + string.Join("\n  ", problems));
        }

        return last < 0 ? null : changes[last].Name;
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

    [GeneratedRegex(@"^\s*sqitch (?:(?<project>[^:\s]+):)?(?<change>[^#\s:]+)(?:#(?<n>\d+))?(?: sha1 (?<sha1>[0-9a-f]{40}))?")]
    private static partial Regex SqitchNote();

    [GeneratedRegex(@"^\s*grate (?<folder>[^/]+)/(?<script>.+?)\s*$")]
    private static partial Regex GrateNote();

    [GeneratedRegex(@"^\s*dbup (?<s>.+?)\s*$")]
    private static partial Regex DbUpNote();
}
