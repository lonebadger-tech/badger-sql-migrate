using System.ComponentModel;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Project;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Badger.SqlMigrate.Cli.Mcp;

/// <summary>
/// The tools 'bsm mcp' offers. They read and write project files only: none of them opens a database
/// connection, so an assistant can write changes but a person deploys them.
/// </summary>
public sealed class ProjectTools(string projectDir)
{
    private static readonly string[] ScriptKinds = ["deploy", "revert", "verify"];

    private readonly string root = Path.GetFullPath(projectDir);

    // Commands' cores print nothing with json on; warnings go to stderr, which MCP clients log.
    private GlobalOptions Globals(string? environment = null) => new(root, environment, null, [], false, new ConsoleOutput(json: true, verbose: false, noColor: true));

    [McpServerTool(Name = "project_info", ReadOnly = true, Idempotent = true)]
    [Description("The project's database provider (sqlserver = T-SQL, postgresql = PostgreSQL), environment and variable names, folder layout, and plan size. Call this first.")]
    public object ProjectInfo() => Run(() =>
    {
        var config = BsmConfig.Load(root);
        var provider = Commands.Providers.Resolve(config.Provider);
        var project = MigrationProject.Load(root, provider.Id, includes: provider.Includes);
        return new
        {
            provider = provider.Id,
            dialect = provider.DisplayName,
            projectDir = root,
            defaultEnvironment = config.DefaultEnvironment,
            environments = config.DeployableEnvironments.ToList(),
            variables = config.Variables.Keys.Order(StringComparer.Ordinal).ToList(),
            changes = project.Changes.Count,
            repeatables = project.Repeatables.Count,
            lastChange = project.Changes.Count > 0 ? project.Changes[^1].Name : null,
            planHead = project.HeadChainId,
            problems = project.Problems,
            layout = new
            {
                plan = PlanFile.FileName,
                changes = $"{MigrationProject.ChangesFolder}/<name>/{{deploy,revert,verify}}.sql",
                repeatables = $"{MigrationProject.RepeatableFolder}/<path>.sql",
                environmentScripts = $"{MigrationProject.EnvironmentsFolder}/<environment>/<path>.sql",
            },
            guide = "Read the 'guide' tool (or resource bsm://guide) before writing changes.",
        };
    });

    [McpServerTool(Name = "plan_show", ReadOnly = true, Idempotent = true)]
    [Description("Every change in the plan, in deploy order, with its dependencies, flags (NoTransaction, Irreversible), tags and note, plus the repeatables. Shows nothing about what any database has deployed.")]
    public object PlanShow() => Run(() =>
    {
        var config = BsmConfig.Load(root);
        var provider = Commands.Providers.Resolve(config.Provider);
        var project = MigrationProject.Load(root, provider.Id, includes: provider.Includes);
        return new
        {
            changes = project.Changes.Select(c => new { c.Name, c.Plan.Requires, flags = c.Plan.Flags.ToString(), c.Plan.Tags, c.Plan.Note }),
            repeatables = project.Repeatables.Select(r => r.Name),
        };
    });

    [McpServerTool(Name = "read_change", ReadOnly = true, Idempotent = true)]
    [Description("The deploy, revert and verify scripts of a change, as written in the project (variables not rendered).")]
    public object ReadChange([Description("Change name from the plan.")] string name) => Run(() =>
    {
        var dir = ChangeDir(name, mustExist: true);
        return new
        {
            name,
            deploy = ReadIfExists(Path.Combine(dir, MigrationProject.DeployFile)),
            revert = ReadIfExists(Path.Combine(dir, MigrationProject.RevertFile)),
            verify = ReadIfExists(Path.Combine(dir, MigrationProject.VerifyFile)),
        };
    });

    [McpServerTool(Name = "add_change")]
    [Description("Add a change: appends it to bsm.plan and writes changes/<name>/ with the given scripts. revert must undo exactly what deploy does, in reverse order; verify (optional) must raise an error when the change is missing. Run 'check' afterwards.")]
    public object AddChange(
        [Description("Lower-case, hyphenated name describing the change, e.g. add-customer-email. Letters, digits, '-', '_' and '.' only.")] string name,
        [Description("The deploy script.")] string deploy,
        [Description("The revert script. Omit only when irreversible is true.")] string? revert = null,
        [Description("Optional verify script that raises an error if the change is not in effect.")] string? verify = null,
        [Description("Changes that must be deployed first (they must already be in the plan).")] string[]? requires = null,
        [Description("Short note stored in the plan.")] string? note = null,
        [Description("Run outside a transaction, for statements that cannot run in one (lint BSM001). Scripts must then be safe to re-run.")] bool notx = false,
        [Description("The change cannot be undone; no revert script.")] bool irreversible = false) => Run(() =>
    {
        if (!irreversible && string.IsNullOrWhiteSpace(revert))
        {
            throw new MigrationException("A revert script is required (or set irreversible: true when the change really cannot be undone).");
        }

        var added = Commands.AddChange(root, name, requires ?? [], note, notx, irreversible, deploy, irreversible ? null : revert, verify);
        return new { added.Name, added.Files, added.PlanLine, lint = Commands.CheckProject(Globals()).Findings.Where(f => f.Path.StartsWith($"{MigrationProject.ChangesFolder}/{name}/", StringComparison.Ordinal)) };
    });

    [McpServerTool(Name = "write_change_script", Destructive = true, Idempotent = true)]
    [Description("Replace the deploy, revert or verify script of an existing change. Only for changes that have not been deployed anywhere: editing a deployed change breaks its hash and stops every deploy. To change a deployed change, use rework_change.")]
    public object WriteChangeScript(
        [Description("Change name from the plan.")] string name,
        [Description("deploy, revert or verify.")] string kind,
        [Description("The complete new script.")] string sql) => Run(() =>
    {
        if (!ScriptKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new MigrationException("kind must be deploy, revert or verify.");
        }

        var dir = ChangeDir(name, mustExist: true);
        var plan = PlanFile.Load(Path.Combine(root, PlanFile.FileName));
        if (kind == "revert" && plan.Changes[plan.IndexOf(name)].Flags.HasFlag(ChangeFlags.Irreversible))
        {
            throw new MigrationException($"'{name}' is marked !irreversible in bsm.plan, so it has no revert script.");
        }

        var file = $"{kind}.sql";
        File.WriteAllText(Path.Combine(dir, file), Commands.NormalizeScript(sql), new System.Text.UTF8Encoding(false));
        return new
        {
            path = $"{MigrationProject.ChangesFolder}/{name}/{file}",
            warning = "If this change is already deployed in any database, this edit breaks its hash: restore the file and use rework_change instead. The user can check with 'bsm status'.",
            lint = Commands.CheckProject(Globals()).Findings.Where(f => f.Path.StartsWith($"{MigrationProject.ChangesFolder}/{name}/", StringComparison.Ordinal)),
        };
    });

    [McpServerTool(Name = "update_change", Destructive = true, Idempotent = true)]
    [Description("Change a change's plan options: notx (run outside a transaction, e.g. after lint BSM001), irreversible, requires, note. Only for changes not deployed anywhere yet: these options are part of the change's hash.")]
    public object UpdateChange(
        [Description("Change name from the plan.")] string name,
        [Description("Run outside a transaction.")] bool? notx = null,
        [Description("true: the change cannot be undone; its revert.sql is removed (its text is returned). false: reversible again; write revert.sql with write_change_script.")] bool? irreversible = null,
        [Description("Replace the changes this one requires (each must come earlier in the plan).")] string[]? requires = null,
        [Description("Replace the note.")] string? note = null) => Run(() =>
    {
        var line = Commands.UpdateChange(root, name, notx, irreversible, requires, note);
        string? removedRevert = null;
        var revertPath = Path.Combine(ChangeDir(name, mustExist: true), MigrationProject.RevertFile);
        if (irreversible == true && File.Exists(revertPath))
        {
            removedRevert = File.ReadAllText(revertPath);
            File.Delete(revertPath);
        }

        return new
        {
            planLine = line,
            removedRevert,
            warning = "If this change is already deployed in any database, this edit breaks its hash: undo it and use rework_change instead.",
            lint = Commands.CheckProject(Globals()).Findings.Where(f => f.Path.StartsWith($"{MigrationProject.ChangesFolder}/{name}/", StringComparison.Ordinal)),
        };
    });

    [McpServerTool(Name = "rework_change")]
    [Description("Supersede a deployed change: adds <name>-v2 (or the given name) whose deploy script starts as a copy of the original to edit, and whose revert restores the original. Use for definitions that can be run again (procedures, views, functions, permissions).")]
    public object ReworkChange(
        [Description("The deployed change to supersede.")] string name,
        [Description("Name for the new change (default: <name>-v2, -v3, ...).")] string? newName = null,
        [Description("Note stored in the plan.")] string? note = null) =>
        Run(() => Commands.ReworkChange(root, name, newName, note));

    [McpServerTool(Name = "write_repeatable", Destructive = true, Idempotent = true)]
    [Description("Create or replace a repeatable: a view, procedure, function or trigger that bsm re-applies whenever its text changes. Write it with CREATE OR ALTER (SQL Server) or CREATE OR REPLACE (PostgreSQL). Declare other repeatables it uses on the first line: -- bsm:requires views/a.sql")]
    public object WriteRepeatable(
        [Description("Path under repeatable/, e.g. views/active_customer.sql. Prefix with @<environment>/ for a script that runs only in that environment, e.g. @dev/seed.sql.")] string path,
        [Description("The complete script.")] string sql) => Run(() =>
    {
        string folder;
        var relative = path.Replace('\\', '/');
        if (relative.StartsWith('@'))
        {
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            if (slash < 2)
            {
                throw new MigrationException("Environment scripts are named @<environment>/<path>.sql.");
            }

            folder = Path.Combine(root, MigrationProject.EnvironmentsFolder, relative[1..slash]);
            relative = relative[(slash + 1)..];
        }
        else
        {
            folder = Path.Combine(root, MigrationProject.RepeatableFolder);
        }

        if (!relative.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationException("Repeatable paths end in .sql.");
        }

        var target = Path.GetFullPath(Path.Combine(folder, relative));
        if (Path.IsPathRooted(relative) || !target.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new MigrationException($"'{path}' is outside the project's repeatable folders.");
        }

        var existed = File.Exists(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, Commands.NormalizeScript(sql), new System.Text.UTF8Encoding(false));
        return new { path = Path.GetRelativePath(root, target).Replace('\\', '/'), created = !existed };
    });

    [McpServerTool(Name = "check", ReadOnly = true, Idempotent = true)]
    [Description("Validate the whole project like 'bsm plan check': plan problems, undefined variables, and lint findings with code, file and line. Fix every error; fix warnings unless the user says otherwise.")]
    public object Check() => Run(() => Commands.CheckProject(Globals()));

    [McpServerTool(Name = "lint_sql", ReadOnly = true, Idempotent = true)]
    [Description("Lint SQL before writing it, with the project's provider rules (e.g. statements that cannot run in a transaction, USE, BEGIN/COMMIT, unsupported psql commands).")]
    public object LintSql(
        [Description("The SQL to check.")] string sql,
        [Description("deploy, revert or verify.")] string kind = "deploy",
        [Description("Lint as a change marked notx.")] bool notx = false) =>
        Run(() => Commands.LintSql(root, sql, kind, notx));

    [McpServerTool(Name = "render", ReadOnly = true, Idempotent = true)]
    [Description("A script exactly as an environment would receive it, with {variables} and $(variables) replaced. Connects to nothing.")]
    public object Render(
        [Description("Change name, or repeatable path (views/v.sql, @dev/seed.sql).")] string name,
        [Description("deploy, revert or verify (changes only).")] string kind = "deploy",
        [Description("Environment name from bsm.yaml (default: the default environment).")] string? environment = null) =>
        Run(() => Commands.RenderScript(Globals(environment), name, kind));

    [McpServerTool(Name = "guide", ReadOnly = true, Idempotent = true)]
    [Description("The full guide to writing Badger SQL Migrate changes: rules for deploy/revert/verify scripts, transactions, repeatables, variables, SQL Server and PostgreSQL specifics, and lint codes.")]
    public static string Guide() => Commands.AiGuide;

    private string ChangeDir(string name, bool mustExist)
    {
        if (!PlanFile.IsValidName(name))
        {
            throw new MigrationException($"Invalid change name '{name}'.");
        }

        var plan = PlanFile.Load(Path.Combine(root, PlanFile.FileName));
        if (mustExist && plan.IndexOf(name) < 0)
        {
            throw new MigrationException($"Change '{name}' is not in the plan.");
        }

        return Path.Combine(root, MigrationProject.ChangesFolder, name);
    }

    private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>bsm's own errors reach the assistant as tool errors with their message; anything else stays generic.</summary>
    private static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (MigrationException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
