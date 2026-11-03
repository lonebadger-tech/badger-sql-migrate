using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Project;

/// <summary>
/// A migration project on disk:
/// <code>
/// bsm.yaml
/// bsm.plan
/// changes/&lt;name&gt;/deploy.sql | revert.sql | verify.sql
/// repeatable/**/*.sql
/// </code>
/// </summary>
public sealed partial class MigrationProject
{
    public const string ChangesFolder = "changes";
    public const string RepeatableFolder = "repeatable";

    /// <summary>environments/&lt;name&gt;/**/*.sql: repeatable scripts that run only in that environment (seed data, grants).</summary>
    public const string EnvironmentsFolder = "environments";
    public const string DeployFile = "deploy.sql";
    public const string RevertFile = "revert.sql";
    public const string VerifyFile = "verify.sql";

    private MigrationProject(string root, string providerId, PlanFile plan, IReadOnlyList<MigrationChange> changes, IReadOnlyList<RepeatableScript> repeatables, IReadOnlyList<string> problems)
    {
        Root = root;
        ProviderId = providerId;
        Plan = plan;
        Changes = changes;
        Repeatables = repeatables;
        Problems = problems;
    }

    public string Root { get; }

    public string ProviderId { get; }

    public PlanFile Plan { get; }

    /// <summary>Changes in plan order (only those whose deploy.sql exists).</summary>
    public IReadOnlyList<MigrationChange> Changes { get; }

    public IReadOnlyList<RepeatableScript> Repeatables { get; }

    /// <summary>Plan and folder consistency problems. A project with problems must not be deployed.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The chain id of the last change in the plan: the certificate of the whole plan.</summary>
    public string? HeadChainId => Changes.Count == 0 ? null : Changes[^1].ChainId;

    public MigrationChange? Find(string name) => Changes.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    /// <param name="environmentName">When set, scripts under environments/&lt;name&gt;/ are added after the shared repeatables.</param>
    public static MigrationProject Load(string root, string providerId, string? environmentName = null)
    {
        root = Path.GetFullPath(root);
        var planPath = Path.Combine(root, PlanFile.FileName);
        if (!File.Exists(planPath))
        {
            throw new MigrationException($"No {PlanFile.FileName} found in '{root}'. Run 'bsm init' first.");
        }

        var plan = PlanFile.Load(planPath);
        var problems = PlanValidator.Validate(plan).ToList();
        var changes = new List<MigrationChange>();
        string? parent = null;

        foreach (var planned in plan.Changes)
        {
            var dir = Path.Combine(root, ChangesFolder, planned.Name);
            var deployPath = Path.Combine(dir, DeployFile);
            if (!File.Exists(deployPath))
            {
                problems.Add($"change '{planned.Name}' is in the plan but {ChangesFolder}/{planned.Name}/{DeployFile} does not exist.");
                continue;
            }

            var revert = ReadOptional(Path.Combine(dir, RevertFile));
            if (revert is null && !planned.Flags.HasFlag(ChangeFlags.Irreversible))
            {
                problems.Add($"change '{planned.Name}' has no {RevertFile}. Add one or mark the change !irreversible in the plan.");
            }

            var change = new MigrationChange(planned, ContentHasher.ReadNormalized(deployPath), revert, ReadOptional(Path.Combine(dir, VerifyFile)));
            change.ChainId = MigrationChange.ComputeChainId(providerId, change.Name, change.ScriptHash, parent);
            parent = change.ChainId;
            changes.Add(change);
        }

        var changesDir = Path.Combine(root, ChangesFolder);
        if (Directory.Exists(changesDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(changesDir))
            {
                var name = Path.GetFileName(dir);
                if (plan.IndexOf(name) < 0)
                {
                    problems.Add($"folder {ChangesFolder}/{name} is not listed in {PlanFile.FileName}.");
                }
            }
        }

        var repeatables = LoadRepeatables(Path.Combine(root, RepeatableFolder), string.Empty, problems);
        if (environmentName is not null)
        {
            var shared = repeatables.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
            repeatables.AddRange(LoadRepeatables(Path.Combine(root, EnvironmentsFolder, environmentName), $"@{environmentName}/", problems, shared));
        }

        return new MigrationProject(root, providerId, plan, changes, repeatables, problems);
    }

    private static List<RepeatableScript> LoadRepeatables(string dir, string prefix, List<string> problems, IReadOnlySet<string>? alreadyApplied = null)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var scripts = Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories)
            .Select(path =>
            {
                var name = prefix + Path.GetRelativePath(dir, path).Replace('\\', '/');
                var text = ContentHasher.ReadNormalized(path);
                var requires = RequiresHeader().Matches(text)
                    .SelectMany(m => m.Groups[1].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
                    .ToList();
                return new RepeatableScript(name, text, ContentHasher.Hash(text), requires);
            })
            .ToList();

        return OrderRepeatables(scripts, prefix, problems, alreadyApplied ?? new HashSet<string>());
    }

    /// <summary>
    /// Orders repeatables by declared "-- bsm:requires a.sql b.sql" headers, then by path (ordinal).
    /// Environment scripts may also require shared repeatables, which always run first.
    /// </summary>
    private static List<RepeatableScript> OrderRepeatables(List<RepeatableScript> scripts, string prefix, List<string> problems, IReadOnlySet<string> runEarlier)
    {
        var byName = scripts.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var ordered = new List<RepeatableScript>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        void Visit(RepeatableScript script)
        {
            if (state.TryGetValue(script.Name, out var s))
            {
                if (s == 1)
                {
                    problems.Add($"repeatable '{script.Name}' is part of a dependency cycle.");
                }

                return;
            }

            state[script.Name] = 1;
            foreach (var dep in script.Requires)
            {
                if (byName.TryGetValue(prefix + dep, out var target))
                {
                    Visit(target);
                }
                else if (!runEarlier.Contains(dep))
                {
                    problems.Add($"repeatable '{script.Name}' requires unknown repeatable '{dep}'.");
                }
            }

            state[script.Name] = 2;
            ordered.Add(script);
        }

        foreach (var script in scripts.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            Visit(script);
        }

        return ordered;
    }

    private static string? ReadOptional(string path) => File.Exists(path) ? ContentHasher.ReadNormalized(path) : null;

    [GeneratedRegex(@"^\s*--\s*bsm:requires\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex RequiresHeader();
}
