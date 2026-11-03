using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Import;

public sealed record SqitchImportOptions
{
    /// <summary>The Sqitch project folder (with sqitch.conf), or the folder holding sqitch.plan.</summary>
    public required string Path { get; init; }

    /// <summary>Which engine's settings to use from sqitch.conf; default: core.engine.</summary>
    public string? Engine { get; init; }
}

/// <summary>
/// Converts a Sqitch project. The model is nearly the same as bsm's: the plan's changes become changes in the same
/// order with the same dependencies, tags, planner and timestamp; deploy/revert/verify scripts are copied.
/// A reworked change (the same name later in the plan) becomes &lt;name&gt;-v2, -v3, ..., with the earlier
/// versions' scripts taken from &lt;name&gt;@&lt;tag&gt;.sql as Sqitch keeps them. Notes start with
/// "sqitch &lt;name&gt;" so each change can be traced back. Sqitch has no SQL Server engine, so scripts are
/// copied as written and usually need translating to T-SQL.
/// </summary>
public static partial class SqitchImporter
{
    public const string Tool = "Sqitch";

    public static ImportedProject Import(SqitchImportOptions options)
    {
        var root = System.IO.Path.GetFullPath(options.Path);
        if (!Directory.Exists(root))
        {
            throw new MigrationException($"'{options.Path}' is not a folder.");
        }

        var config = File.Exists(System.IO.Path.Combine(root, "sqitch.conf")) ? ParseConfig(File.ReadAllText(System.IO.Path.Combine(root, "sqitch.conf"))) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var engine = options.Engine ?? Get(config, "core.engine");
        string Setting(string key, string fallback) =>
            (engine is null ? null : Get(config, $"engine.{engine}.{key}")) ?? Get(config, $"core.{key}") ?? fallback;

        var planPath = System.IO.Path.Combine(root, Setting("plan_file", System.IO.Path.Combine(Setting("top_dir", "."), "sqitch.plan")));
        if (!File.Exists(planPath))
        {
            throw new MigrationException($"No Sqitch plan at '{planPath}'. Pass the folder holding sqitch.conf or sqitch.plan.");
        }

        var deployDir = System.IO.Path.Combine(root, Setting("deploy_dir", System.IO.Path.Combine(Setting("top_dir", "."), "deploy")));
        var revertDir = System.IO.Path.Combine(root, Setting("revert_dir", System.IO.Path.Combine(Setting("top_dir", "."), "revert")));
        var verifyDir = System.IO.Path.Combine(root, Setting("verify_dir", System.IO.Path.Combine(Setting("top_dir", "."), "verify")));
        var extension = Setting("extension", "sql");

        var result = new ImportedProject { Tool = Tool, CanBaselineFromJournal = false };
        var plan = ParsePlan(File.ReadAllText(planPath, Encoding.UTF8), planPath);
        if (plan.Entries.All(e => e.Tag is not null))
        {
            throw new MigrationException($"'{planPath}' has no changes.");
        }

        // Which bsm change each Sqitch occurrence becomes, and which tag names its older script files.
        var occurrences = new List<(PlanEntry Entry, string BsmName, int Occurrence, string? FileSuffix)>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Entries.Count; i++)
        {
            var entry = plan.Entries[i];
            if (entry.Tag is not null)
            {
                continue;
            }

            var n = counts[entry.Name!] = counts.GetValueOrDefault(entry.Name!) + 1;
            var reworkedLater = plan.Entries.Skip(i + 1).Any(e => e.Name == entry.Name);
            string? suffix = null;
            if (reworkedLater)
            {
                suffix = plan.Entries.Skip(i + 1).FirstOrDefault(e => e.Tag is not null)?.Tag
                    ?? throw new MigrationException($"{planPath} line {entry.Line}: '{entry.Name}' is reworked later but no tag follows it.");
            }

            var baseName = PlanFile.IsValidName(entry.Name!) ? entry.Name! : result.UniqueName(entry.Name!, "change", null);
            var bsmName = n == 1 ? baseName : $"{baseName}-v{n}";
            for (var k = n + 1; result.Changes.Any(c => c.Name == bsmName) || occurrences.Any(o => o.BsmName == bsmName); k++)
            {
                bsmName = $"{baseName}-v{k}";
            }

            occurrences.Add((entry, bsmName, n, suffix));
        }

        string? Resolve(string dependency, int beforeLine, string where)
        {
            var name = dependency;
            string? tag = null;
            var at = dependency.IndexOf('@', StringComparison.Ordinal);
            if (at > 0)
            {
                tag = dependency[(at + 1)..];
                name = dependency[..at];
            }

            var limit = beforeLine;
            if (tag is not null && tag is not "HEAD")
            {
                var tagEntry = plan.Entries.FirstOrDefault(e => e.Tag == tag);
                if (tagEntry is null)
                {
                    result.Warnings.Add($"{where}: dependency '{dependency}' names unknown tag @{tag}; dropped.");
                    return null;
                }

                limit = tagEntry.Line;
            }

            var match = occurrences.LastOrDefault(o => o.Entry.Name == name && o.Entry.Line < limit);
            if (match.BsmName is null)
            {
                result.Warnings.Add($"{where}: dependency '{dependency}' is not an earlier change in the plan; dropped.");
                return null;
            }

            return match.BsmName;
        }

        foreach (var (entry, bsmName, occurrence, suffix) in occurrences)
        {
            var where = $"{System.IO.Path.GetFileName(planPath)} line {entry.Line}";
            var file = entry.Name + (suffix is null ? string.Empty : "@" + suffix) + "." + extension;
            var deployPath = System.IO.Path.Combine(deployDir, file);
            if (!File.Exists(deployPath))
            {
                throw new MigrationException($"{where}: '{entry.Name}' has no deploy script ({System.IO.Path.GetRelativePath(root, deployPath)}).");
            }

            var requires = new List<string>();
            foreach (var dependency in entry.Requires)
            {
                if (dependency.StartsWith('!'))
                {
                    result.Warnings.Add($"{where}: '{entry.Name}' conflicts with '{dependency[1..]}'; bsm has no conflicts, so this was dropped.");
                }
                else if (dependency.Contains(':', StringComparison.Ordinal) && !dependency.StartsWith(plan.Project + ":", StringComparison.Ordinal))
                {
                    result.Warnings.Add($"{where}: '{entry.Name}' requires '{dependency}' from another Sqitch project; bsm projects cannot depend on each other, so this was dropped.");
                }
                else if (Resolve(dependency.Contains(':', StringComparison.Ordinal) ? dependency[(dependency.IndexOf(':') + 1)..] : dependency, entry.Line, where) is { } resolved && !requires.Contains(resolved))
                {
                    requires.Add(resolved);
                }
            }

            var deploy = Read(deployPath, result);
            var revertPath = System.IO.Path.Combine(revertDir, file);
            var verifyPath = System.IO.Path.Combine(verifyDir, file);
            var revert = File.Exists(revertPath) ? Read(revertPath, result) : null;
            var flags = (revert is null ? ChangeFlags.Irreversible : ChangeFlags.None) | (OwnsTransaction(deploy) ? ChangeFlags.NoTransaction : ChangeFlags.None);
            var tags = new List<string>();
            var index = plan.Entries.ToList().IndexOf(entry);
            for (var j = index + 1; j < plan.Entries.Count && plan.Entries[j].Tag is { } tag; j++)
            {
                if (PlanFile.IsValidName(tag))
                {
                    tags.Add(tag);
                }
                else
                {
                    result.Warnings.Add($"{where}: tag @{tag} is not a valid bsm tag name; dropped.");
                }
            }

            var note = $"sqitch {entry.Name}{(occurrence > 1 ? "#" + occurrence.ToString(CultureInfo.InvariantCulture) : string.Empty)}" + (entry.Note is { Length: > 0 } n ? ": " + n.Replace('#', '-') : string.Empty);
            result.Changes.Add(new ImportedChange(bsmName, deploy, revert, flags, note, System.IO.Path.GetRelativePath(root, deployPath).Replace('\\', '/'))
            {
                Verify = File.Exists(verifyPath) ? Read(verifyPath, result) : null,
                Requires = requires,
                Tags = tags,
                PlannedAt = entry.PlannedAt,
                Author = entry.Planner is { Length: > 0 } p ? AuthorRegex().Replace(p, "_") : null,
            });
        }

        if (result.Changes.Any(c => c.Flags.HasFlag(ChangeFlags.NoTransaction)))
        {
            result.Warnings.Add("changes whose deploy script manages its own transaction (BEGIN ... COMMIT, as Sqitch scripts do) are !notx, so bsm does not wrap them in a second one.");
        }

        result.Warnings.Add($"Sqitch has no SQL Server engine: the scripts were copied as written{(engine is null ? string.Empty : $" for {engine}")} and usually need translating to T-SQL. 'bsm plan check' flags what does not parse.");
        if (config.Keys.Any(k => k.Contains("variables", StringComparison.OrdinalIgnoreCase)))
        {
            result.Warnings.Add("sqitch.conf defines variables, which Sqitch hands to the database client; scripts that use them need {name} variables in bsm.yaml instead.");
        }

        return result;
    }

    private static string Read(string path, ImportedProject result) => result.EscapeLiteral(File.ReadAllText(path, Encoding.UTF8), System.IO.Path.GetFileName(path));

    private static bool OwnsTransaction(string sql) => OwnTransactionRegex().IsMatch(sql) && CommitRegex().IsMatch(sql);

    private static string? Get(Dictionary<string, string> config, string key) => config.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    /// <summary>sqitch.conf is git-config style: [section] or [section "sub"], then key = value.</summary>
    public static Dictionary<string, string> ParseConfig(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var m = SectionRegex().Match(line);
                section = m.Groups["sub"].Success ? $"{m.Groups["name"].Value}.{m.Groups["sub"].Value}" : m.Groups["name"].Value;
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? line : line[..eq].Trim();
            var value = eq < 0 ? "true" : line[(eq + 1)..].Trim().Trim('"');
            values[$"{section}.{key}"] = value;
        }

        return values;
    }

    /// <param name="Name">Change name, or null for a tag line.</param>
    public sealed record PlanEntry(int Line, string? Name, string? Tag, IReadOnlyList<string> Requires, DateTimeOffset? PlannedAt, string? Planner, string? Note);

    public sealed record SqitchPlan(string? Project, IReadOnlyList<PlanEntry> Entries);

    public static SqitchPlan ParsePlan(string text, string source = "sqitch.plan")
    {
        string? project = null;
        var entries = new List<PlanEntry>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('%'))
            {
                if (line.StartsWith("%project=", StringComparison.Ordinal))
                {
                    project = line["%project=".Length..].Trim();
                }

                continue;
            }

            var m = EntryRegex().Match(line);
            if (!m.Success)
            {
                throw new MigrationException($"{source} line {i + 1}: cannot read '{line}'.");
            }

            if (m.Groups["op"].Value == "-")
            {
                throw new MigrationException($"{source} line {i + 1}: '-' (revert) entries are not supported; clean up the plan with Sqitch first.");
            }

            var requires = m.Groups["deps"].Success ? m.Groups["deps"].Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries) : [];
            DateTimeOffset? at = DateTimeOffset.TryParse(m.Groups["at"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
            var note = m.Groups["note"].Success ? m.Groups["note"].Value.Trim() : null;
            entries.Add(m.Groups["tag"].Success
                ? new PlanEntry(i + 1, null, m.Groups["name"].Value, [], at, m.Groups["planner"].Value.Trim(), note)
                : new PlanEntry(i + 1, m.Groups["name"].Value, null, requires, at, m.Groups["planner"].Value.Trim(), note));
        }

        return new SqitchPlan(project, entries);
    }

    [GeneratedRegex(@"^(?<op>[+-])?(?<tag>@)?(?<name>[^\s\[\]#@:][^\s\[\]#]*?)\s*(?:\[(?<deps>[^\]]*)\])?\s+(?<at>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)\s+(?<planner>[^<#]*?)\s*(?:<[^>]*>)?\s*(?:#(?<note>.*))?$")]
    private static partial Regex EntryRegex();

    [GeneratedRegex(@"^\[\s*(?<name>[^\s""\]]+)(?:\s+""(?<sub>[^""]*)"")?\s*\]$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"[^A-Za-z0-9_.\-]+")]
    private static partial Regex AuthorRegex();

    [GeneratedRegex(@"^\s*BEGIN(\s+TRAN(SACTION)?)?\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OwnTransactionRegex();

    [GeneratedRegex(@"^\s*COMMIT\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CommitRegex();
}
