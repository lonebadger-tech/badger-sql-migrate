using System.Reflection;
using System.Text;
using Badger.SqlMigrate.Connectivity;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Preprocessing;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;

namespace Badger.SqlMigrate.Cli;

public sealed record GlobalOptions(string ProjectDir, string? Environment, string? ConnectionString, string[] Variables, bool ClaimEnvironment, ConsoleOutput Out);

public static class Commands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NotUpToDate = 3;

    /// <summary>The only place providers are registered. Adding a vendor = one line here plus a package reference.</summary>
    public static readonly ProviderRegistry Providers = new ProviderRegistry()
        .Register(SqlServerProvider.ProviderId, () => new SqlServerProvider());

    public static string ToolVersion { get; } =
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static async Task<int> Run(GlobalOptions g, Func<GlobalOptions, Task<int>> action)
    {
        try
        {
            return await action(g);
        }
        catch (MigrationException ex)
        {
            g.Out.Error(ex.Message);
            return Failed;
        }
        catch (OperationCanceledException)
        {
            g.Out.Error("cancelled.");
            return Failed;
        }
    }

    public static Task<int> Run(GlobalOptions g, Func<GlobalOptions, int> action) => Run(g, x => Task.FromResult(action(x)));

    public static int Init(GlobalOptions g, string providerId)
    {
        var provider = Providers.Resolve(providerId);
        var dir = Path.GetFullPath(g.ProjectDir);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, MigrationProject.ChangesFolder));
        Directory.CreateDirectory(Path.Combine(dir, MigrationProject.RepeatableFolder));
        Directory.CreateDirectory(Path.Combine(dir, MigrationProject.EnvironmentsFolder));

        WriteIfMissing(Path.Combine(dir, BadgerConfig.FileName), $$"""
            # Badger SQL Migrate configuration
            provider: {{provider.Id}}
            defaultEnvironment: local

            # Script variables, referenced as $(Name) in scripts. Values are not part of change hashes.
            # Environments can override them under their own 'variables:'.
            variables: {}

            environments:
              # Names starting with '_' are templates: other environments inherit them with 'extends'.
              _azure:
                database: MyDatabase
                tls: { mode: strict }
                authentication: entraInteractive   # browser sign-in with MFA; token cached between runs
                entra:
                  tenantId: 00000000-0000-0000-0000-000000000000

              local:
                host: localhost
                port: 1433
                database: MyDatabase
                authentication: password
                user: sa
                password: ${env:BADGER_LOCAL_PASSWORD}
                tls:
                  mode: mandatory
                  trustServerCertificate: true   # local development only

            #  dev:
            #    extends: _azure
            #    host: myapp-dev.database.windows.net
            #
            #  prod:
            #    extends: _azure
            #    host: myapp-prod.database.windows.net
            #    protected: true                  # deploy/revert require typing the environment name (or --yes)
            #    allowRevert: false               # fix forward in production
            #    ordering: strict                 # exact plan order; prod history is certified
            #    authentication: entraWorkloadIdentity
            #    connectionProperties:            # any SqlClient connection-string keyword
            #      Connect Retry Count: 3
            #      Command Timeout: 120
            #    alwaysEncrypted:                 # per-environment keys; scripts use the same CEK names everywhere
            #      columnMasterKeys:
            #        CMK_Prod:
            #          keyStore: AZURE_KEY_VAULT
            #          keyPath: https://myapp-prod-kv.vault.azure.net/keys/cmk/0123456789abcdef
            #      columnEncryptionKeys:
            #        CEK_Customer: { columnMasterKey: CMK_Prod }
            """);

        WriteIfMissing(Path.Combine(dir, PlanFile.FileName), """
            # Badger SQL Migrate plan: one change per line, in deploy order.
            #   name [requires ...] !notx !irreversible @timestamp author  # note
            #   @tag                                                       (tags the change above)
            # Merged with git's union driver (see .gitattributes), so concurrent additions never conflict.
            # Run 'bsm plan check' after merging.


            """);

        var gitattributes = Path.Combine(dir, ".gitattributes");
        var rule = $"{PlanFile.FileName} merge=union";
        var existing = File.Exists(gitattributes) ? File.ReadAllText(gitattributes) : string.Empty;
        if (!existing.Contains(rule, StringComparison.Ordinal))
        {
            File.AppendAllText(gitattributes, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty) + rule + "\n*.sql text eol=lf\n");
        }

        g.Out.Success($"initialized {provider.DisplayName} project in {dir}");
        g.Out.Info("next: bsm add <change-name>");
        return Ok;
    }

    public static int Add(GlobalOptions g, string name, string[] requires, string? note, bool noTx, bool irreversible)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        var config = BadgerConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        if (!PlanFile.IsValidName(name))
        {
            throw new MigrationException($"Invalid change name '{name}'. Use letters, digits, '-', '_' or '.'.");
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

        Directory.CreateDirectory(changeDir);
        File.WriteAllText(Path.Combine(changeDir, MigrationProject.DeployFile), provider.FormatComment($"Deploy {name}") + "\n\n", Utf8NoBom);
        if (!irreversible)
        {
            File.WriteAllText(Path.Combine(changeDir, MigrationProject.RevertFile), provider.FormatComment($"Revert {name}: undo everything deploy.sql does") + "\n\n", Utf8NoBom);
        }

        File.WriteAllText(Path.Combine(changeDir, MigrationProject.VerifyFile), provider.FormatComment($"Verify {name}: raise an error if the deploy did not take effect (optional; delete if unused)") + "\n\n", Utf8NoBom);

        var flags = (noTx ? ChangeFlags.NoTransaction : ChangeFlags.None) | (irreversible ? ChangeFlags.Irreversible : ChangeFlags.None);
        var author = Environment.GetEnvironmentVariable("BADGER_AUTHOR") ?? Environment.UserName.Replace(' ', '_');
        var line = PlanFile.FormatChange(new PlanChange(name, requires, flags, DateTimeOffset.UtcNow, author, note, 0));
        AppendLine(planPath, line);

        g.Out.Success($"added {name}");
        g.Out.Info($"  {MigrationProject.ChangesFolder}/{name}/{MigrationProject.DeployFile}");
        if (!irreversible)
        {
            g.Out.Info($"  {MigrationProject.ChangesFolder}/{name}/{MigrationProject.RevertFile}");
        }

        g.Out.Info($"  {MigrationProject.ChangesFolder}/{name}/{MigrationProject.VerifyFile}");
        return Ok;
    }

    public static int Tag(GlobalOptions g, string name)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        if (!PlanFile.IsValidName(name))
        {
            throw new MigrationException($"Invalid tag name '{name}'.");
        }

        var planPath = Path.Combine(dir, PlanFile.FileName);
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
        g.Out.Success($"tagged {plan.Changes[^1].Name} as @{name}");
        return Ok;
    }

    public static int PlanCheck(GlobalOptions g)
    {
        var (project, provider) = LoadProject(g);
        var engine = new MigrationEngine(project, provider, new DeploymentEnvironment { Name = "check" }, OfflineSession.Instance, Actor.Current(ToolVersion), g.Out);
        var lint = engine.Lint();
        var errors = project.Problems.Concat(lint.Where(l => l.Diagnostic.Severity == LintSeverity.Error).Select(Format)).ToList();
        var warnings = lint.Where(l => l.Diagnostic.Severity == LintSeverity.Warning).Select(Format).ToList();
        CheckVariableReferences(g, project, errors, warnings);

        if (g.Out.Json)
        {
            g.Out.WriteJson(new { ok = errors.Count == 0, changes = project.Changes.Count, planHead = project.HeadChainId, errors, warnings });
        }
        else
        {
            foreach (var w in warnings)
            {
                g.Out.Warn(w);
            }

            foreach (var e in errors)
            {
                g.Out.Error(e);
            }

            if (errors.Count == 0)
            {
                g.Out.Success($"plan ok: {project.Changes.Count} changes, {project.Repeatables.Count} repeatables, head {Short(project.HeadChainId)}");
            }
        }

        return errors.Count == 0 ? Ok : Failed;

        static string Format((string Change, string Script, LintDiagnostic Diagnostic) l) =>
            $"{l.Change}/{l.Script}.sql line {l.Diagnostic.Line}: {l.Diagnostic.Message} [{l.Diagnostic.Code}]";
    }

    /// <summary>
    /// Every {name}/$(name) used by a script must be a built-in or defined for every deployable environment
    /// (or via --var). Defined only for some environments is an error naming the environments that lack it.
    /// </summary>
    private static void CheckVariableReferences(GlobalOptions g, MigrationProject project, List<string> errors, List<string> warnings)
    {
        var config = BadgerConfig.Load(Path.GetFullPath(g.ProjectDir));
        if (config.Preprocessor.Unknown == UnknownVariablePolicy.Keep)
        {
            return;
        }

        var cli = ParseVariables(g.Variables).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var environments = config.DeployableEnvironments.ToDictionary(e => e, config.VariableNames);
        var scripts = project.Changes.SelectMany(c => new[] { ($"{c.Name}/deploy.sql", c.DeployScript), ($"{c.Name}/revert.sql", c.RevertScript), ($"{c.Name}/verify.sql", c.VerifyScript) })
            .Concat(project.Repeatables.Select(r => ($"repeatable/{r.Name}", (string?)r.Script)));
        foreach (var (path, script) in scripts)
        {
            if (script is null)
            {
                continue;
            }

            foreach (var reference in ScriptPreprocessor.FindReferences(script, config.Preprocessor.SqlCmdSyntax))
            {
                var name = reference.Name.StartsWith("var.", StringComparison.OrdinalIgnoreCase) ? reference.Name[4..] : reference.Name;
                if (ScriptPreprocessor.IsBuiltInName(name) || cli.Contains(name))
                {
                    continue;
                }

                var missing = environments.Where(e => !e.Value.Contains(name)).Select(e => e.Key).ToList();
                if (missing.Count == environments.Count)
                {
                    errors.Add($"{path} line {reference.Line}: variable '{reference.Name}' is not defined in badger.yaml (or write {{{{{reference.Name}}}}} for a literal).");
                }
                else if (missing.Count > 0)
                {
                    errors.Add($"{path} line {reference.Line}: variable '{reference.Name}' is not defined for environment(s) {string.Join(", ", missing)}.");
                }
            }
        }
    }

    public static int Vars(GlobalOptions g)
    {
        var (engine, _) = OfflineEngine(g);
        var list = engine.Preprocessor.Describe().Select(v => new
        {
            v.Name,
            value = IsSensitive(v.Name) ? "********" : v.Source == VariableSource.BuiltIn ? v.Value : SafeResolve(engine, v.Name),
            raw = v.Source == VariableSource.BuiltIn || IsSensitive(v.Name) ? null : v.Value,
            source = v.Source.ToString().ToLowerInvariant(),
            v.Description,
            v.Volatile,
        }).ToList();

        if (g.Out.Json)
        {
            g.Out.WriteJson(new { environment = engine.Environment.Name, variables = list });
            return Ok;
        }

        g.Out.Line("environment", engine.Environment.Name);
        foreach (var group in list.GroupBy(v => v.source))
        {
            g.Out.Highlight(group.Key == "builtin" ? "built-in" : group.Key == "config" ? "badger.yaml" : "--var");
            foreach (var v in group)
            {
                var detail = v.raw is not null && v.raw != v.value ? $"  (= {v.raw})" : v.Description is not null ? $"  # {v.Description}" : string.Empty;
                Console.WriteLine($"  {{{v.Name}}}".PadRight(26) + $" {v.value}{g.Out.Paint(detail, "\u001b[90m")}");
            }
        }

        return Ok;

        static string SafeResolve(MigrationEngine engine, string name)
        {
            try
            {
                return engine.Preprocessor.Resolve(name);
            }
            catch (MigrationException ex)
            {
                return $"<error: {ex.Message}>";
            }
        }
    }

    public static int Render(GlobalOptions g, string name, string kind)
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
        Console.Write(rendered.Text);
        if (!rendered.Text.EndsWith('\n'))
        {
            Console.WriteLine();
        }

        if (rendered.UsesVolatile)
        {
            g.Out.Warn("uses per-run variables (deploy.*); the rendered text differs on every run and is not hash-tracked.");
        }

        return Ok;
    }

    private static bool IsSensitive(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.EndsWith("password", StringComparison.Ordinal) || lower.EndsWith("secret", StringComparison.Ordinal) || lower.EndsWith("token", StringComparison.Ordinal) || lower.EndsWith("key", StringComparison.Ordinal);
    }

    /// <summary>An engine for the selected environment without a database connection (vars, render).</summary>
    private static (MigrationEngine Engine, MigrationProject Project) OfflineEngine(GlobalOptions g)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        var config = BadgerConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        var environment = config.ResolveEnvironment(g.Environment, g.ConnectionString);
        var project = MigrationProject.Load(dir, provider.Id, environment.Name);
        var engine = new MigrationEngine(project, provider, environment, OfflineSession.Instance, Actor.Current(ToolVersion), g.Out, ParseVariables(g.Variables))
        {
            Preprocessing = config.Preprocessor,
        };
        return (engine, project);
    }

    public static int PlanShow(GlobalOptions g)
    {
        var (project, _) = LoadProject(g);
        if (g.Out.Json)
        {
            g.Out.WriteJson(new
            {
                planHead = project.HeadChainId,
                changes = project.Changes.Select(c => new { c.Name, c.Plan.Requires, flags = c.Plan.Flags.ToString(), c.Plan.Tags, c.ScriptHash, c.ChainId }),
                repeatables = project.Repeatables.Select(r => new { r.Name, r.Hash }),
            });
            return Ok;
        }

        foreach (var c in project.Changes)
        {
            var tags = c.Plan.Tags.Count > 0 ? "  " + string.Join(' ', c.Plan.Tags.Select(t => "@" + t)) : string.Empty;
            g.Out.Info($"{Short(c.ChainId)}  {c.Name}{tags}");
        }

        foreach (var r in project.Repeatables)
        {
            g.Out.Muted($"{Short(r.Hash)}  repeatable/{r.Name}");
        }

        g.Out.Highlight($"plan head {project.HeadChainId ?? "(empty)"}");
        return Ok;
    }

    public static async Task<int> Status(GlobalOptions g, bool exitCode, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var report = await ctx.Engine.StatusAsync(ct);

        if (g.Out.Json)
        {
            g.Out.WriteJson(new
            {
                target = ctx.Session.Description,
                environment = ctx.Environment.Name,
                recordedEnvironment = await ctx.Engine.GetRecordedEnvironmentAsync(ct),
                upToDate = report.UpToDate,
                certified = report.Certified,
                databaseHead = report.DatabaseHeadChainId,
                planHead = report.PlanHeadChainId,
                changes = report.Changes.Select(c => new
                {
                    c.Change.Name,
                    state = c.State.ToString().ToLowerInvariant(),
                    deployedAt = c.Applied?.DeployedAtUtc,
                    deployedBy = c.Applied?.DeployedBy,
                    outOfOrder = c.Applied?.AppliedOutOfOrder ?? false,
                }),
                orphans = report.Orphans.Select(o => o.Name),
            });
        }
        else
        {
            var recorded = await ctx.Engine.GetRecordedEnvironmentAsync(ct);
            g.Out.Line("environment", ctx.Environment.Name + (recorded is not null && recorded != ctx.Environment.Name ? $" (database belongs to '{recorded}')" : string.Empty));
            g.Out.Line("target", ctx.Session.Description);
            foreach (var c in report.Changes)
            {
                var (mark, ansi) = c.State switch
                {
                    ChangeState.Applied => ("+", "\u001b[32m"),
                    ChangeState.HashMismatch => ("!", "\u001b[31m"),
                    _ => ("-", "\u001b[33m"),
                };
                var detail = c.State switch
                {
                    ChangeState.Applied => $"{c.Applied!.DeployedAtUtc:yyyy-MM-dd HH:mm}Z {c.Applied.DeployedBy}{(c.Applied.AppliedOutOfOrder ? " (out of order)" : string.Empty)}{(c.Applied.Baselined ? " (baseline)" : string.Empty)}",
                    ChangeState.HashMismatch => "EDITED AFTER DEPLOY (hash mismatch)",
                    _ => "pending",
                };
                Console.WriteLine($"  {g.Out.Paint(mark, ansi)} {c.Change.Name,-40} {detail}");
            }

            foreach (var orphan in report.Orphans)
            {
                Console.WriteLine($"  {g.Out.Paint("?", "\u001b[35m")} {orphan.Name,-40} deployed but not in plan");
            }

            g.Out.Info(string.Empty);
            g.Out.Line("database head", Short(report.DatabaseHeadChainId));
            g.Out.Line("plan head", Short(report.PlanHeadChainId));
            if (report.UpToDate && report.Certified)
            {
                g.Out.Success("up to date and certified: database history is identical to the plan.");
            }
            else if (report.UpToDate)
            {
                g.Out.Warn("all changes applied, but not in plan order; contents match, history order differs (not certified).");
            }
            else
            {
                g.Out.Info($"{report.Pending.Count()} pending, {report.Mismatched.Count()} edited, {report.Orphans.Count} not in plan{(report.Certified ? string.Empty : "; history not certified")}.");
            }
        }

        return exitCode && !report.UpToDate ? NotUpToDate : Ok;
    }

    public static async Task<int> Deploy(GlobalOptions g, string? to, bool dryRun, string? scriptPath, OrderingPolicy? ordering, bool yes, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        if (scriptPath is not null)
        {
            var script = await ctx.Engine.GenerateScriptAsync(to, ct);
            await File.WriteAllTextAsync(scriptPath, script, Utf8NoBom, ct);
            g.Out.Success($"wrote {scriptPath}");
            return Ok;
        }

        if (!dryRun && ctx.Environment.Protected)
        {
            var pending = (await ctx.Engine.StatusAsync(ct)).Pending.Count();
            Confirm(g, yes, ctx.Environment, $"Deploy {pending} pending change(s) to protected environment '{ctx.Environment.Name}' ({ctx.Session.Description})?");
        }

        g.Out.Muted($"deploying to {ctx.Session.Description} (environment {ctx.Environment.Name}){(dryRun ? " [dry run]" : string.Empty)}");
        var result = await ctx.Engine.DeployAsync(new DeployOptions { To = to, DryRun = dryRun, Ordering = ordering }, ct);
        if (g.Out.Json)
        {
            g.Out.WriteJson(result);
        }
        else if (result.Deployed.Count == 0 && result.Repeatables.Count == 0)
        {
            g.Out.Success("nothing to deploy; database is up to date.");
        }
        else
        {
            g.Out.Success($"{(dryRun ? "would deploy" : "deployed")} {result.Deployed.Count} change(s), {result.Repeatables.Count} repeatable(s).");
        }

        return Ok;
    }

    public static async Task<int> Revert(GlobalOptions g, string? to, int? last, bool all, bool dryRun, bool yes, bool allowVariableDrift, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var options = new RevertOptions { To = to, Last = last, All = all, AllowVariableDrift = allowVariableDrift };
        if (!dryRun)
        {
            var preview = await ctx.Engine.RevertAsync(options with { DryRun = true }, ct);
            if (preview.Reverted.Count == 0)
            {
                g.Out.Success("nothing to revert.");
                return Ok;
            }

            Confirm(g, yes, ctx.Environment, $"Revert {preview.Reverted.Count} change(s) from '{ctx.Environment.Name}' ({ctx.Session.Description}): {string.Join(", ", preview.Reverted)}?");
        }

        var result = await ctx.Engine.RevertAsync(options with { DryRun = dryRun }, ct);
        if (g.Out.Json)
        {
            g.Out.WriteJson(result);
        }
        else
        {
            g.Out.Success($"{(dryRun ? "would revert" : "reverted")} {result.Reverted.Count} change(s).");
        }

        return Ok;
    }

    public static async Task<int> Rebase(GlobalOptions g, string to, bool yes, bool allowVariableDrift, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var preview = await ctx.Engine.RevertAsync(new RevertOptions { To = to, DryRun = true }, ct);
        if (preview.Reverted.Count > 0)
        {
            Confirm(g, yes, ctx.Environment, $"Revert {preview.Reverted.Count} change(s) from '{ctx.Environment.Name}' ({ctx.Session.Description}) and redeploy?");
            await ctx.Engine.RevertAsync(new RevertOptions { To = to, AllowVariableDrift = allowVariableDrift }, ct);
        }

        var result = await ctx.Engine.DeployAsync(new DeployOptions(), ct);
        g.Out.Success($"rebased: reverted {preview.Reverted.Count}, deployed {result.Deployed.Count}.");
        return Ok;
    }

    public static async Task<int> Verify(GlobalOptions g, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var result = await ctx.Engine.VerifyAsync(ct);
        if (g.Out.Json)
        {
            g.Out.WriteJson(new
            {
                ok = result.Ok,
                certified = result.Status.Certified,
                databaseHead = result.Status.DatabaseHeadChainId,
                recordedFingerprint = result.RecordedFingerprint,
                liveFingerprint = result.LiveFingerprint,
                problems = result.Problems,
            });
        }
        else
        {
            g.Out.Line("database head", Short(result.Status.DatabaseHeadChainId));
            g.Out.Line("certified", result.Status.Certified ? "yes" : "no");
            g.Out.Line("schema", result.LiveFingerprint is null ? "n/a" : $"{Short(result.LiveFingerprint)}{(result.Drifted ? $" (recorded {Short(result.RecordedFingerprint)})" : " (matches last deploy)")}");
            foreach (var p in result.Problems)
            {
                g.Out.Error(p);
            }

            if (result.Ok)
            {
                g.Out.Success("verified.");
            }
        }

        return result.Ok ? Ok : Failed;
    }

    public static async Task<int> Log(GlobalOptions g, int limit, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var events = await ctx.Engine.LogAsync(limit, ct);
        if (g.Out.Json)
        {
            g.Out.WriteJson(events);
            return Ok;
        }

        foreach (var e in events)
        {
            var ansi = e.Kind switch
            {
                MigrationEventKind.Fail => "\u001b[31m",
                MigrationEventKind.Revert => "\u001b[33m",
                _ => "\u001b[32m",
            };
            Console.WriteLine($"{e.AtUtc:yyyy-MM-dd HH:mm:ss}Z  {g.Out.Paint(e.Kind.ToString().ToLowerInvariant(), ansi),-12} {e.Name,-40} {e.Actor}@{e.Host}{(e.Message is null ? string.Empty : "  " + e.Message.ReplaceLineEndings(" "))}");
        }

        return Ok;
    }

    public static async Task<int> Baseline(GlobalOptions g, string to, bool yes, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        Confirm(g, yes, ctx.Environment, $"Record changes up to '{to}' as already applied on '{ctx.Environment.Name}' ({ctx.Session.Description}) without running them?");
        var names = await ctx.Engine.BaselineAsync(to, ct);
        g.Out.Success($"baselined {names.Count} change(s).");
        return Ok;
    }

    public static async Task<int> Doctor(GlobalOptions g, CancellationToken ct)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        var config = BadgerConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        var environment = config.ResolveEnvironment(g.Environment, g.ConnectionString);
        g.Out.Line("provider", provider.DisplayName);
        g.Out.Line("environment", environment.Name + (environment.Protected ? " (protected)" : string.Empty));
        g.Out.Line("auth", environment.Authentication?.ToString() ?? "(from connection string)");

        var problems = provider.ValidateEnvironment(environment);
        foreach (var p in problems)
        {
            g.Out.Error(p);
        }

        if (problems.Count > 0)
        {
            return Failed;
        }

        if (environment.Authentication == AuthenticationKind.Integrated && !OperatingSystem.IsWindows())
        {
            var cache = System.Environment.GetEnvironmentVariable("KRB5CCNAME");
            g.Out.Line("kerberos", cache is null ? "using default credential cache (run 'kinit user@REALM' first)" : $"credential cache {cache}");
        }

        // Acquire a token first so identity problems are reported separately from network/database problems.
        if (EntraTokenProvider.Create(environment.Authentication, environment.Entra, environment.Name, Console.Error.WriteLine) is { } tokens)
        {
            var token = await tokens.GetTokenAsync([tokens.DefaultScope], ct);
            g.Out.Line("entra token", $"ok, expires {token.ExpiresOn:yyyy-MM-dd HH:mm}Z");
        }

        await using var ctx = await Connect(g, ct);
        foreach (var (key, value) in await provider.DescribeAsync(ctx.Session, ct))
        {
            g.Out.Line(key, value);
        }

        var recorded = await ctx.Engine.GetRecordedEnvironmentAsync(ct);
        g.Out.Line("recorded env", recorded ?? "(not yet deployed)");
        if (recorded is not null && recorded != environment.Name)
        {
            g.Out.Warn($"this database belongs to environment '{recorded}'.");
        }

        var status = await ctx.Engine.StatusAsync(ct);
        g.Out.Line("journal", status.Applied.Count == 0 ? "no changes deployed yet" : $"{status.Applied.Count} change(s) deployed");
        PrintResources(g, await ctx.Engine.PlanResourcesAsync(ct));
        g.Out.Success("connection ok.");
        return Ok;
    }

    public static int EnvList(GlobalOptions g)
    {
        var config = BadgerConfig.Load(Path.GetFullPath(g.ProjectDir));
        var rows = config.Environments.Keys.Order(StringComparer.Ordinal).Select(name =>
        {
            var merged = config.Merge(name);
            return new
            {
                name,
                template = BadgerConfig.IsTemplate(name),
                isDefault = name == config.DefaultEnvironment,
                extends = config.Environments[name]["extends"]?.ToString(),
                description = merged["description"]?.ToString(),
                host = merged["host"]?.ToString() ?? (merged["connectionString"] is null ? null : "(connection string)"),
                database = merged["database"]?.ToString(),
                authentication = merged["authentication"]?.ToString(),
                @protected = merged["protected"]?.GetValue<bool>() ?? false,
                ordering = merged["ordering"]?.ToString() ?? "dependency",
            };
        }).ToList();

        if (g.Out.Json)
        {
            g.Out.WriteJson(rows);
            return Ok;
        }

        foreach (var r in rows)
        {
            var flags = string.Join(' ', new[] { r.isDefault ? "default" : null, r.template ? "template" : null, r.@protected ? "protected" : null, r.extends is null ? null : $"extends {r.extends}" }.Where(f => f is not null));
            Console.WriteLine($"  {(r.template ? g.Out.Paint(r.name, "\u001b[90m") : r.name),-20} {r.host ?? "-",-40} {r.database ?? "-",-16} {r.authentication ?? "-",-24} {flags}");
        }

        return Ok;
    }

    public static int EnvShow(GlobalOptions g)
    {
        var config = BadgerConfig.Load(Path.GetFullPath(g.ProjectDir));
        var environment = config.ResolveEnvironment(g.Environment, g.ConnectionString);
        var node = System.Text.Json.JsonSerializer.SerializeToNode(environment, ShowJsonOptions)!;
        Mask(node);
        Console.WriteLine(node.ToJsonString(ShowJsonOptions));
        var provider = Providers.Resolve(config.Provider);
        foreach (var problem in provider.ValidateEnvironment(environment))
        {
            g.Out.Error(problem);
        }

        return Ok;
    }

    public static async Task<int> EnvStatus(GlobalOptions g, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var recorded = await ctx.Engine.GetRecordedEnvironmentAsync(ct);
        var actions = await ctx.Engine.PlanResourcesAsync(ct);
        if (g.Out.Json)
        {
            g.Out.WriteJson(new { environment = ctx.Environment.Name, recordedEnvironment = recorded, resources = actions.Select(a => new { kind = a.Kind.ToString().ToLowerInvariant(), a.ResourceType, a.Name, a.Description }) });
            return Ok;
        }

        g.Out.Line("environment", ctx.Environment.Name);
        g.Out.Line("recorded env", recorded ?? "(not yet deployed)");
        g.Out.Line("target", ctx.Session.Description);
        PrintResources(g, actions);
        return actions.Any(a => a.Kind is Core.Providers.ResourceActionKind.Conflict) ? Failed : Ok;
    }

    public static async Task<int> EnvProvision(GlobalOptions g, bool dryRun, bool allowDestructive, bool yes, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var plan = await ctx.Engine.PlanResourcesAsync(ct);
        var pending = plan.Where(a => a.Kind == Core.Providers.ResourceActionKind.Apply || (allowDestructive && a.Kind == Core.Providers.ResourceActionKind.Destructive)).ToList();
        if (!dryRun && pending.Count > 0)
        {
            var destructive = pending.Count(a => a.Kind == Core.Providers.ResourceActionKind.Destructive);
            if (destructive > 0 || ctx.Environment.Protected)
            {
                Confirm(g, yes, ctx.Environment, $"Apply {pending.Count} resource change(s) to '{ctx.Environment.Name}'{(destructive > 0 ? $", {destructive} destructive" : string.Empty)}?", force: destructive > 0);
            }
        }

        var applied = await ctx.Engine.ProvisionAsync(dryRun, allowDestructive, ct);
        g.Out.Success(applied.Count == 0 ? "environment resources are up to date." : $"{(dryRun ? "would apply" : "applied")} {applied.Count} resource change(s).");
        return Ok;
    }

    public static int AuthClear(GlobalOptions g)
    {
        var count = EntraTokenProvider.ClearSavedSignIns();
        g.Out.Success($"forgot {count} saved Entra sign-in(s). Cached tokens are no longer used; the next run signs in again.");
        return Ok;
    }

    private static void PrintResources(GlobalOptions g, IReadOnlyList<Core.Providers.ResourceAction> actions)
    {
        foreach (var a in actions)
        {
            var (mark, ansi) = a.Kind switch
            {
                Core.Providers.ResourceActionKind.InSync => ("=", "\u001b[32m"),
                Core.Providers.ResourceActionKind.Apply => ("+", "\u001b[33m"),
                Core.Providers.ResourceActionKind.Destructive => ("-", "\u001b[31m"),
                Core.Providers.ResourceActionKind.Conflict => ("!", "\u001b[31m"),
                _ => ("i", "\u001b[90m"),
            };
            Console.WriteLine($"  {g.Out.Paint(mark, ansi)} {a.ResourceType} {a.Name}: {a.Description}");
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions ShowJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Masks secrets for display: password, secret, token and passphrase fields, and passwords inside connection strings.</summary>
    private static void Mask(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                var lower = key.ToLowerInvariant();
                if (obj[key] is System.Text.Json.Nodes.JsonValue && (lower.EndsWith("password", StringComparison.Ordinal) || lower.EndsWith("secret", StringComparison.Ordinal) || lower.EndsWith("accesstoken", StringComparison.Ordinal) || lower.EndsWith("passphrase", StringComparison.Ordinal)))
                {
                    obj[key] = "********";
                }
                else if (lower == "connectionstring" && obj[key]?.ToString() is { } cs)
                {
                    obj[key] = System.Text.RegularExpressions.Regex.Replace(cs, @"(?i)(password|pwd)\s*=\s*[^;]*", "$1=********");
                }
                else
                {
                    Mask(obj[key]);
                }
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var item in array)
            {
                Mask(item);
            }
        }
    }

    private static (MigrationProject Project, IDatabaseProvider Provider) LoadProject(GlobalOptions g)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        var config = BadgerConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        return (MigrationProject.Load(dir, provider.Id), provider);
    }

    private static async Task<ConnectedContext> Connect(GlobalOptions g, CancellationToken ct)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        var config = BadgerConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        var environment = config.ResolveEnvironment(g.Environment, g.ConnectionString);
        var problems = provider.ValidateEnvironment(environment);
        if (problems.Count > 0)
        {
            throw new MigrationException(string.Join("\n", problems));
        }

        var project = MigrationProject.Load(dir, provider.Id, environment.Name);
        SshTunnel? tunnel = null;
        if (environment.Ssh is not null)
        {
            tunnel = SshTunnel.Open(environment.Ssh, provider.ResolveEndpoint(environment), g.Out.Warn);
            g.Out.Detail($"ssh tunnel {environment.Ssh.User}@{environment.Ssh.Host} -> {tunnel.LocalEndpoint.Host}:{tunnel.LocalEndpoint.Port}");
        }

        try
        {
            Action<string> prompt = m => Console.Error.WriteLine(m);
            var context = new SessionContext
            {
                EndpointOverride = tunnel?.LocalEndpoint,
                Tokens = EntraTokenProvider.Create(environment.Authentication, environment.Entra, environment.Name, prompt),
                CreateTokenProvider = (kind, options) => EntraTokenProvider.Create(kind, options, environment.Name + ":keyvault", prompt),
                Warn = g.Out.Warn,
                BaseDirectory = dir,
            };
            var session = await provider.OpenSessionAsync(environment, context, ct);
            var engine = new MigrationEngine(project, provider, environment, session, Actor.Current(ToolVersion), g.Out, ParseVariables(g.Variables))
            {
                AllowEnvironmentMismatch = g.ClaimEnvironment,
                Preprocessing = config.Preprocessor,
            };
            return new ConnectedContext(environment, session, engine, tunnel);
        }
        catch
        {
            tunnel?.Dispose();
            throw;
        }
    }

    private static Dictionary<string, string> ParseVariables(string[] values)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in values)
        {
            var eq = v.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new MigrationException($"--var expects name=value, got '{v}'.");
            }

            result[v[..eq]] = v[(eq + 1)..];
        }

        return result;
    }

    /// <summary>
    /// Asks before changing a database. Protected environments (and destructive actions) require typing the
    /// environment name; --yes skips the prompt for automation. Non-interactive runs must pass --yes.
    /// </summary>
    private static void Confirm(GlobalOptions g, bool yes, DeploymentEnvironment environment, string question, bool force = false)
    {
        var strong = environment.Protected || force;
        if (yes || (!strong && question.StartsWith("Deploy", StringComparison.Ordinal)))
        {
            return;
        }

        if (Console.IsInputRedirected || g.Out.Json)
        {
            throw new MigrationException(question + " Pass --yes to confirm in non-interactive runs.");
        }

        if (strong)
        {
            Console.Write($"{question}\nType the environment name ({environment.Name}) to continue: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), environment.Name, StringComparison.Ordinal))
            {
                throw new MigrationException("aborted.");
            }

            return;
        }

        Console.Write($"{question} [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationException("aborted.");
        }
    }

    private static void AppendLine(string path, string line)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
        File.AppendAllText(path, prefix + line + "\n", Utf8NoBom);
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content.ReplaceLineEndings("\n"), Utf8NoBom);
        }
    }

    private static string Short(string? hash) => hash is null ? "(none)" : hash.Length > 12 ? hash[..12] : hash;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private sealed record ConnectedContext(DeploymentEnvironment Environment, IDatabaseSession Session, MigrationEngine Engine, SshTunnel? Tunnel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Tunnel?.Dispose();
        }
    }

    /// <summary>Placeholder session for commands that never touch the database (plan check).</summary>
    private sealed class OfflineSession : IDatabaseSession
    {
        public static readonly OfflineSession Instance = new();

        public event Action<string>? Message
        {
            add { }
            remove { }
        }

        public string Description => "offline";

        public bool InTransaction => false;

        public TimeSpan? CommandTimeout { get; set; }

        public Task BeginTransactionAsync(CancellationToken cancellationToken) => throw Offline();

        public Task CommitAsync(CancellationToken cancellationToken) => throw Offline();

        public Task RollbackAsync(CancellationToken cancellationToken) => throw Offline();

        public Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) => throw Offline();

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) => throw Offline();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static InvalidOperationException Offline() => new("No database connection in offline mode.");
    }
}
