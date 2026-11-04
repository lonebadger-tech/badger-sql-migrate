using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.PostgreSql;

namespace Badger.SqlMigrate.Cli;

public sealed record AddedChange(string Name, IReadOnlyList<string> Files, string PlanLine);

public sealed record ReworkedChange(string Name, string Of, IReadOnlyList<string> Files, string PlanLine);

public sealed record TaggedChange(string Tag, string Change);

public sealed record LintFinding(string Path, int Line, string Code, string Severity, string Message);

public sealed record CheckReport(bool Ok, int Changes, int Repeatables, string? PlanHead, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<LintFinding> Findings);

public sealed record RenderedScript(string Name, string Kind, string Text, bool UsesVolatile);

/// <summary>
/// The parts of add, rework, tag, plan check and render that touch project files, returning results instead of
/// printing them, so the CLI and 'bsm mcp' share one implementation.
/// </summary>
public static partial class Commands
{
    public static AddedChange AddChange(string projectDir, string name, string[] requires, string? note, bool noTx, bool irreversible, string? deploy = null, string? revert = null, string? verify = null)
    {
        var dir = Path.GetFullPath(projectDir);
        var config = BsmConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        if (!PlanFile.IsValidName(name))
        {
            throw new MigrationException($"Invalid change name '{name}'. Use letters, digits, '-', '_' or '.'.");
        }

        if (irreversible && revert is not null)
        {
            throw new MigrationException($"'{name}' is irreversible, so it has no revert script.");
        }

        var planPath = Path.Combine(dir, PlanFile.FileName);
        var plan = PlanFile.Load(planPath);
        if (plan.IndexOf(name) >= 0)
        {
            throw new MigrationException($"Change '{name}' already exists in the plan.");
        }

        foreach (var dep in requires)
        {
            if (plan.IndexOf(dep) < 0)
            {
                throw new MigrationException($"Required change '{dep}' is not in the plan.");
            }
        }

        var changeDir = Path.Combine(dir, MigrationProject.ChangesFolder, name);
        if (Directory.Exists(changeDir))
        {
            throw new MigrationException($"Folder {MigrationProject.ChangesFolder}/{name} already exists.");
        }

        // Given scripts are written as they are; without any, the CLI's commented templates are written.
        var scaffold = deploy is null && revert is null && verify is null;
        var files = new List<string>();
        Directory.CreateDirectory(changeDir);
        Write(MigrationProject.DeployFile, deploy ?? provider.FormatComment($"Deploy {name}") + "\n\n");
        if (!irreversible && (scaffold || revert is not null))
        {
            Write(MigrationProject.RevertFile, revert ?? provider.FormatComment($"Revert {name}: undo everything deploy.sql does") + "\n\n");
        }

        if (scaffold || verify is not null)
        {
            Write(MigrationProject.VerifyFile, verify ?? provider.FormatComment($"Verify {name}: raise an error if the deploy did not take effect (optional; delete if unused)") + "\n\n");
        }

        var flags = (noTx ? ChangeFlags.NoTransaction : ChangeFlags.None) | (irreversible ? ChangeFlags.Irreversible : ChangeFlags.None);
        var line = PlanFile.FormatChange(new PlanChange(name, requires, flags, DateTimeOffset.UtcNow, Author(), note, 0));
        AppendLine(planPath, line);
        return new AddedChange(name, files, line);

        void Write(string file, string text)
        {
            File.WriteAllText(Path.Combine(changeDir, file), NormalizeScript(text), Utf8NoBom);
            files.Add($"{MigrationProject.ChangesFolder}/{name}/{file}");
        }
    }

    public static ReworkedChange ReworkChange(string projectDir, string name, string? newName, string? note)
    {
        var dir = Path.GetFullPath(projectDir);
        var config = BsmConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        var planPath = Path.Combine(dir, PlanFile.FileName);
        var plan = PlanFile.Load(planPath);
        var index = plan.IndexOf(name);
        if (index < 0)
        {
            throw new MigrationException($"Change '{name}' is not in the plan.");
        }

        var oldDir = Path.Combine(dir, MigrationProject.ChangesFolder, name);
        var oldDeploy = Path.Combine(oldDir, MigrationProject.DeployFile);
        if (!File.Exists(oldDeploy))
        {
            throw new MigrationException($"{MigrationProject.ChangesFolder}/{name}/{MigrationProject.DeployFile} does not exist.");
        }

        newName ??= NextRevisionName(name, n => plan.IndexOf(n) >= 0 || Directory.Exists(Path.Combine(dir, MigrationProject.ChangesFolder, n)));
        if (!PlanFile.IsValidName(newName))
        {
            throw new MigrationException($"Invalid change name '{newName}'. Use letters, digits, '-', '_' or '.'.");
        }

        if (plan.IndexOf(newName) >= 0 || Directory.Exists(Path.Combine(dir, MigrationProject.ChangesFolder, newName)))
        {
            throw new MigrationException($"Change '{newName}' already exists.");
        }

        var deploy = ContentHasher.ReadNormalized(oldDeploy);
        var newDir = Path.Combine(dir, MigrationProject.ChangesFolder, newName);
        var files = new List<string>();
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, MigrationProject.DeployFile), deploy, Utf8NoBom);
        files.Add($"{MigrationProject.ChangesFolder}/{newName}/{MigrationProject.DeployFile}");
        File.WriteAllText(
            Path.Combine(newDir, MigrationProject.RevertFile),
            provider.FormatComment($"Revert {newName}: restore what {name} deployed.") + "\n" +
            provider.FormatComment($"Copied from {MigrationProject.ChangesFolder}/{name}/{MigrationProject.DeployFile}; edit it if that script cannot simply be run again.") + "\n\n" + deploy,
            Utf8NoBom);
        files.Add($"{MigrationProject.ChangesFolder}/{newName}/{MigrationProject.RevertFile}");
        var oldVerify = Path.Combine(oldDir, MigrationProject.VerifyFile);
        if (File.Exists(oldVerify))
        {
            File.Copy(oldVerify, Path.Combine(newDir, MigrationProject.VerifyFile));
            files.Add($"{MigrationProject.ChangesFolder}/{newName}/{MigrationProject.VerifyFile}");
        }

        var old = plan.Changes[index];
        var line = PlanFile.FormatChange(new PlanChange(newName, [name], old.Flags & ChangeFlags.NoTransaction, DateTimeOffset.UtcNow, Author(), note ?? $"rework of {name}", 0));
        AppendLine(planPath, line);
        return new ReworkedChange(newName, name, files, line);
    }

    public static TaggedChange TagPlan(string projectDir, string name)
    {
        if (!PlanFile.IsValidName(name))
        {
            throw new MigrationException($"Invalid tag name '{name}'.");
        }

        var planPath = Path.Combine(Path.GetFullPath(projectDir), PlanFile.FileName);
        var plan = PlanFile.Load(planPath);
        if (plan.Changes.Count == 0)
        {
            throw new MigrationException("The plan has no changes to tag.");
        }

        if (plan.Changes.Any(c => c.Tags.Contains(name, StringComparer.Ordinal)))
        {
            throw new MigrationException($"Tag '@{name}' already exists.");
        }

        AppendLine(planPath, "@" + name);
        return new TaggedChange(name, plan.Changes[^1].Name);
    }

    /// <summary>
    /// Rewrites a change's plan line with new flags, dependencies or note, keeping its timestamp and author.
    /// Name, dependencies and flags are hashed, so this is only for changes not deployed anywhere yet.
    /// </summary>
    public static string UpdateChange(string projectDir, string name, bool? noTx, bool? irreversible, string[]? requires, string? note)
    {
        var planPath = Path.Combine(Path.GetFullPath(projectDir), PlanFile.FileName);
        var text = File.ReadAllText(planPath);
        var plan = PlanFile.Parse(text);
        var index = plan.IndexOf(name);
        if (index < 0)
        {
            throw new MigrationException($"Change '{name}' is not in the plan.");
        }

        var change = plan.Changes[index];
        foreach (var dep in requires ?? [])
        {
            var at = plan.IndexOf(dep);
            if (at < 0 || at >= index)
            {
                throw new MigrationException($"Required change '{dep}' must be in the plan before '{name}'.");
            }
        }

        var flags = change.Flags;
        if (noTx is { } nt)
        {
            flags = nt ? flags | ChangeFlags.NoTransaction : flags & ~ChangeFlags.NoTransaction;
        }

        if (irreversible is { } irr)
        {
            flags = irr ? flags | ChangeFlags.Irreversible : flags & ~ChangeFlags.Irreversible;
        }

        var line = PlanFile.FormatChange(change with { Requires = requires ?? change.Requires, Flags = flags, Note = note ?? change.Note });
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (change.LineNumber < 1 || change.LineNumber > lines.Length || !lines[change.LineNumber - 1].TrimStart().StartsWith(name, StringComparison.Ordinal))
        {
            throw new MigrationException($"Could not find the plan line for '{name}'.");
        }

        lines[change.LineNumber - 1] = line;
        File.WriteAllText(planPath, string.Join('\n', lines), Utf8NoBom);
        return line;
    }

    /// <summary>Plan problems, lint findings and variable references, as 'bsm plan check' reports them.</summary>
    public static CheckReport CheckProject(GlobalOptions g)
    {
        var (project, provider) = LoadProject(g);
        var engine = new MigrationEngine(project, provider, new DeploymentEnvironment { Name = "check" }, OfflineSession.Instance, Actor.Current(ToolVersion), g.Out);
        var lint = engine.Lint();
        var errors = project.Problems.Concat(lint.Where(l => l.Diagnostic.Severity == LintSeverity.Error).Select(Format)).ToList();
        var warnings = lint.Where(l => l.Diagnostic.Severity == LintSeverity.Warning).Select(Format).ToList();
        CheckVariableReferences(g, project, errors, warnings);
        var findings = lint.Select(l => new LintFinding(
            $"{MigrationProject.ChangesFolder}/{l.Change}/{l.Script}.sql", l.Diagnostic.Line, l.Diagnostic.Code, l.Diagnostic.Severity.ToString().ToLowerInvariant(), l.Diagnostic.Message)).ToList();
        return new CheckReport(errors.Count == 0, project.Changes.Count, project.Repeatables.Count, project.HeadChainId, errors, warnings, findings);

        static string Format((string Change, string Script, LintDiagnostic Diagnostic) l) =>
            $"{l.Change}/{l.Script}.sql line {l.Diagnostic.Line}: {l.Diagnostic.Message} [{l.Diagnostic.Code}]";
    }

    /// <summary>Lints SQL that is not (yet) in the project, as a script of a change with the given flags.</summary>
    public static IReadOnlyList<LintFinding> LintSql(string projectDir, string sql, string kind, bool noTx)
    {
        var config = BsmConfig.Load(Path.GetFullPath(projectDir));
        var provider = Providers.Resolve(config.Provider);
        var flags = noTx ? ChangeFlags.NoTransaction : ChangeFlags.None;
        return provider.Linter.Lint(NormalizeScript(sql), new LintContext("snippet", kind, flags))
            .Select(d => new LintFinding("snippet", d.Line, d.Code, d.Severity.ToString().ToLowerInvariant(), d.Message)).ToList();
    }

    public static RenderedScript RenderScript(GlobalOptions g, string name, string kind)
    {
        var (engine, project) = OfflineEngine(g);
        string? script;
        string? hash = null;
        if (project.Find(name) is { } change)
        {
            script = kind switch
            {
                "deploy" => change.DeployScript,
                "revert" => change.RevertScript,
                "verify" => change.VerifyScript,
                _ => throw new MigrationException("--kind must be deploy, revert or verify."),
            };
            hash = change.ScriptHash;
        }
        else if (project.Repeatables.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal)) is { } repeatable)
        {
            script = repeatable.Script;
            kind = "repeatable";
        }
        else
        {
            throw new MigrationException($"'{name}' is not a change or repeatable (repeatables are named like views/vCustomer.sql or @dev/seed.sql).");
        }

        if (script is null)
        {
            throw new MigrationException($"{name} has no {kind}.sql.");
        }

        var rendered = engine.Render(name, kind, script, hash);
        return new RenderedScript(name, kind, rendered.Text, rendered.UsesVolatile);
    }

    /// <summary>The guide for AI tools (docs/ai-guide.md, embedded so it matches this version).</summary>
    public static string AiGuide => ReadResource("ai-guide.md");

    /// <summary>Writes AGENTS.md for a new project unless one exists. Returns whether it was written.</summary>
    public static bool WriteAgentsFile(string projectDir, IDatabaseProvider provider)
    {
        var path = Path.Combine(Path.GetFullPath(projectDir), "AGENTS.md");
        if (File.Exists(path))
        {
            return false;
        }

        var postgres = provider.Id == PostgreSqlProvider.ProviderId;
        var text = ReadResource("AGENTS.md")
            .Replace("{{provider}}", provider.DisplayName, StringComparison.Ordinal)
            .Replace("{{repeatable}}", postgres ? "`CREATE OR REPLACE`" : "`CREATE OR ALTER`", StringComparison.Ordinal)
            .Replace("{{dialect}}", postgres
                ? "PostgreSQL: statements end with `;`, bodies use `$$` quoting, psql's `\\i`, `\\set` and `\\if` work; `\\connect` doesn't."
                : "T-SQL: separate batches with a `GO` line; `CREATE VIEW/PROCEDURE/FUNCTION/TRIGGER` start their own batch; no `USE`.", StringComparison.Ordinal);
        File.WriteAllText(path, text, Utf8NoBom);
        return true;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(Commands).Assembly.GetManifestResourceStream("Badger.SqlMigrate.Cli." + name)
            ?? throw new InvalidOperationException($"Missing embedded resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>LF line endings and a final newline, like the scripts bsm writes itself.</summary>
    internal static string NormalizeScript(string text)
    {
        text = text.ReplaceLineEndings("\n");
        return text.EndsWith('\n') ? text : text + "\n";
    }

    private static string Author() => Environment.GetEnvironmentVariable("BSM_AUTHOR") ?? Environment.UserName.Replace(' ', '_');
}
