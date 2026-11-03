using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Preprocessing;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Import;

/// <param name="Name">bsm change name.</param>
/// <param name="Note">Plan note; carries the source tool's identity of the script (used by 'bsm baseline --from').</param>
public sealed record ImportedChange(string Name, string Deploy, string? Revert, ChangeFlags Flags, string Note, string Source)
{
    public string? Verify { get; init; }

    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Tags placed on this change (written as "@tag" lines after it).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public DateTimeOffset? PlannedAt { get; init; }

    public string? Author { get; init; }
}

/// <param name="Path">Path under repeatable/, forward slashes.</param>
public sealed record ImportedRepeatable(string Path, string Script, string Source);

/// <param name="Settings">bsm.yaml environment keys (host, database, authentication, tls, variables, ...).</param>
public sealed record ImportedEnvironment(string Name, JsonObject Settings);

/// <summary>
/// A project converted from another tool, ready to write: changes in plan order, repeatables, variables and
/// environments for bsm.yaml, and everything that could not be carried over.
/// </summary>
public sealed partial class ImportedProject
{
    /// <summary>A repeatable path starting with this ("@env:test/seed.sql") is written to environments/&lt;name&gt;/ instead.</summary>
    public const string EnvironmentScriptPrefix = "@env:";

    public required string Tool { get; init; }

    /// <summary>'bsm baseline --from &lt;tool&gt;' can adopt databases the source tool manages (false for Sqitch: no SQL Server registry).</summary>
    public bool CanBaselineFromJournal { get; init; } = true;

    public List<ImportedChange> Changes { get; } = [];

    public List<ImportedRepeatable> Repeatables { get; } = [];

    /// <summary>Shared variables (dotted names become groups in bsm.yaml).</summary>
    public SortedDictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Variables scripts use whose value the source did not define; bsm plan check reports them until defined.</summary>
    public SortedSet<string> UndefinedVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ImportedEnvironment> Environments { get; } = [];

    public string? DefaultEnvironment { get; set; }

    /// <summary>Some script contains literal $(name) text, so bsm.yaml must turn off sqlcmd-style variables.</summary>
    public bool DisableSqlCmdSyntax { get; set; }

    /// <summary>Notes for the user: what was mapped approximately, skipped or needs attention.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Writes changes/, repeatable/ and bsm.plan under <paramref name="root"/> (bsm.yaml is written by the caller).</summary>
    public void WriteScripts(string root)
    {
        var utf8 = new UTF8Encoding(false);
        var plan = new StringBuilder();
        plan.Append($"# Badger SQL Migrate plan, imported from {Tool}. One change per line, in deploy order.\n");
        plan.Append("#   name [requires ...] !notx !irreversible @timestamp author  # note\n");
        plan.Append(CanBaselineFromJournal
            ? $"# The '{Tool.ToLowerInvariant()} ...' notes link each change to the script it came from; 'bsm baseline --from {Tool.ToLowerInvariant()}' uses them.\n\n"
            : $"# The '{Tool.ToLowerInvariant()} ...' notes link each change to the {Tool} change it came from.\n\n");
        foreach (var change in Changes)
        {
            var dir = Path.Combine(root, MigrationProject.ChangesFolder, change.Name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MigrationProject.DeployFile), change.Deploy, utf8);
            if (change.Revert is not null)
            {
                File.WriteAllText(Path.Combine(dir, MigrationProject.RevertFile), change.Revert, utf8);
            }

            if (change.Verify is not null)
            {
                File.WriteAllText(Path.Combine(dir, MigrationProject.VerifyFile), change.Verify, utf8);
            }

            plan.Append(PlanFile.FormatChange(new PlanChange(change.Name, change.Requires, change.Flags, change.PlannedAt, change.Author, change.Note, 0))).Append('\n');
            foreach (var tag in change.Tags)
            {
                plan.Append('@').Append(tag).Append('\n');
            }
        }

        Directory.CreateDirectory(Path.Combine(root, MigrationProject.ChangesFolder));
        File.WriteAllText(Path.Combine(root, PlanFile.FileName), plan.ToString(), utf8);
        foreach (var repeatable in Repeatables)
        {
            // "@env:<name>/path" is an environment-only script: environments/<name>/path.
            var path = repeatable.Path.StartsWith(EnvironmentScriptPrefix, StringComparison.Ordinal)
                ? Path.Combine(root, MigrationProject.EnvironmentsFolder, repeatable.Path[EnvironmentScriptPrefix.Length..])
                : Path.Combine(root, MigrationProject.RepeatableFolder, repeatable.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, repeatable.Script, utf8);
        }
    }

    /// <summary>A unique change name from free text: "Add_customer table" -> "add-customer-table".</summary>
    public string UniqueName(string text, string fallback, string? collisionSuffix)
    {
        var slug = SlugRegex().Replace(text.ToLowerInvariant(), "-").Trim('-', '.', '_');
        if (slug.Length == 0)
        {
            slug = fallback;
        }

        if (!char.IsAsciiLetterOrDigit(slug[0]))
        {
            slug = "c" + slug;
        }

        var name = slug;
        if (Changes.Any(c => c.Name == name) && collisionSuffix is not null)
        {
            name = $"{slug}-{collisionSuffix}";
        }

        for (var n = 2; Changes.Any(c => c.Name == name); n++)
        {
            name = $"{slug}-{n}";
        }

        return name;
    }

    /// <summary>
    /// Makes text that the source tool passed through literally stay literal under bsm's preprocessor:
    /// {name} becomes {{name}}, and literal $(name) turns sqlcmd-style variables off for the project.
    /// </summary>
    public string EscapeLiteral(string text, string source)
    {
        if (DoubleBraceRegex().IsMatch(text))
        {
            Warnings.Add($"{source}: contains '{{{{name}}}}' text, which bsm renders as '{{name}}'. Check it, or start the script with '-- bsm:preprocess off'.");
        }

        if (SqlCmdRegex().IsMatch(text))
        {
            DisableSqlCmdSyntax = true;
        }

        return BraceRegex().Replace(text, m => "{" + m.Value + "}");
    }

    /// <summary>A valid bsm variable name for a source variable; names bsm reserves or cannot use go under <paramref name="group"/>.</summary>
    public static string VariableName(string name, string group)
    {
        var clean = Regex.Replace(name, @"[^A-Za-z0-9_.]", "_").Trim('.');
        clean = Regex.Replace(clean, @"\.{2,}", ".");
        if (clean.Length == 0 || !char.IsAsciiLetter(clean[0]) && clean[0] != '_')
        {
            clean = "_" + clean;
        }

        clean = string.Join('.', clean.Split('.').Select(p => p.Length > 0 && (char.IsAsciiLetter(p[0]) || p[0] == '_') ? p : "_" + p));
        return ScriptPreprocessor.IsBuiltInName(clean) ? $"{group}.{clean}" : clean;
    }

    [GeneratedRegex(@"[^a-z0-9.]+")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"(?<!\{)\{[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\}(?!\})")]
    private static partial Regex BraceRegex();

    [GeneratedRegex(@"\{\{[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\}\}")]
    private static partial Regex DoubleBraceRegex();

    [GeneratedRegex(@"\$\([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\)")]
    private static partial Regex SqlCmdRegex();
}
