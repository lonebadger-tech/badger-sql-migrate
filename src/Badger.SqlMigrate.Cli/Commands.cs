using System.Reflection;
using System.Text;
using Badger.SqlMigrate.Connectivity;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Bundles;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Import;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Preprocessing;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.PostgreSql;
using Badger.SqlMigrate.Providers.SqlServer;

namespace Badger.SqlMigrate.Cli;

public sealed record GlobalOptions(string ProjectDir, string? Environment, string? ConnectionString, string[] Variables, bool ClaimEnvironment, ConsoleOutput Out, string? Bundle = null);

/// <summary>
/// Where a command reads its project from. Normally both are --project; with --bundle the scripts come from the
/// verified, extracted bundle, and so does bsm.yaml when the bundle includes it.
/// </summary>
public sealed record Workspace(string ProjectDir, string ConfigDir, string BaseDir, BundleManifest? Bundle);

public static class Commands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int NotUpToDate = 3;

    /// <summary>The only place providers are registered. Adding a vendor = one line here plus a package reference.</summary>
    public static readonly ProviderRegistry Providers = new ProviderRegistry()
        .Register(SqlServerProvider.ProviderId, () => new SqlServerProvider())
        .Register(PostgreSqlProvider.ProviderId, () => new PostgreSqlProvider())
        .Register("postgres", () => new PostgreSqlProvider());

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
        RequireWorkingCopy(g, "init");
        var provider = Providers.Resolve(providerId);
        var dir = Path.GetFullPath(g.ProjectDir);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, MigrationProject.ChangesFolder));
        Directory.CreateDirectory(Path.Combine(dir, MigrationProject.RepeatableFolder));
        Directory.CreateDirectory(Path.Combine(dir, MigrationProject.EnvironmentsFolder));

        WriteIfMissing(Path.Combine(dir, BsmConfig.FileName), provider.Id == PostgreSqlProvider.ProviderId ? PostgreSqlConfigTemplate : $$"""
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
                password: ${env:BSM_LOCAL_PASSWORD}
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

        WriteGitAttributes(dir);
        g.Out.Success($"initialized {provider.DisplayName} project in {dir}");
        g.Out.Info("next: bsm add <change-name>");
        return Ok;
    }

    private const string PostgreSqlConfigTemplate = """
        # Badger SQL Migrate configuration
        provider: postgresql
        defaultEnvironment: local

        # Script variables, referenced as {Name} in scripts. Values are not part of change hashes.
        # Environments can override them under their own 'variables:'.
        variables: {}

        environments:
          # Names starting with '_' are templates: other environments inherit them with 'extends'.
          _azure:
            database: mydatabase
            tls: { mode: strict }
            authentication: entraDefault       # Azure Database for PostgreSQL: Entra token as the password
            user: myapp-deployers              # the Entra user, group or managed identity the server knows

          local:
            host: localhost
            port: 5432
            database: mydatabase
            authentication: password
            user: postgres
            password: ${env:BSM_LOCAL_PASSWORD}
            tls:
              mode: optional                   # local development only

        #  prod:
        #    extends: _azure
        #    host: myapp-prod.postgres.database.azure.com
        #    protected: true                  # deploy/revert require typing the environment name (or --yes)
        #    allowRevert: false               # fix forward in production
        #    ordering: strict                 # exact plan order; prod history is certified
        #    connectionProperties:            # any Npgsql connection-string keyword
        #      Search Path: app,public
        #      Command Timeout: 120

        """;

    private static void WriteGitAttributes(string dir)
    {
        var gitattributes = Path.Combine(dir, ".gitattributes");
        var rule = $"{PlanFile.FileName} merge=union";
        var existing = File.Exists(gitattributes) ? File.ReadAllText(gitattributes) : string.Empty;
        if (!existing.Contains(rule, StringComparison.Ordinal))
        {
            File.AppendAllText(gitattributes, (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty) + rule + "\n*.sql text eol=lf\n");
        }
    }

    public static int ImportFlyway(GlobalOptions g, string path, string? configFile, string[] locations, bool dryRun, string providerId = SqlServerProvider.ProviderId)
    {
        RequireWorkingCopy(g, "import");
        var provider = Providers.Resolve(providerId);
        var imported = FlywayImporter.Import(new FlywayImportOptions
        {
            Path = path,
            ConfigFile = configFile,
            Locations = locations,
            MapConnection = provider.Id == PostgreSqlProvider.ProviderId ? JdbcPostgreSql.Map : JdbcSqlServer.Map,
        });
        return WriteImport(g, imported, dryRun, provider);
    }

    public static int ImportDbUp(GlobalOptions g, string path, bool fileNames, string[] runAlways, bool noVariables, bool dryRun, string providerId = SqlServerProvider.ProviderId)
    {
        RequireWorkingCopy(g, "import");
        var imported = DbUpImporter.Import(new DbUpImportOptions
        {
            Path = path,
            FileNamesOnly = fileNames,
            RunAlways = runAlways,
            Variables = ParseVariables(g.Variables),
            VariablesDisabled = noVariables,
        });
        return WriteImport(g, imported, dryRun, Providers.Resolve(providerId));
    }

    public static int ImportGrate(GlobalOptions g, string path, string[] folders, string[] environments, bool ignoreDirectoryNames, bool noTokens, bool dryRun, string providerId = SqlServerProvider.ProviderId)
    {
        RequireWorkingCopy(g, "import");
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var eq = folder.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new MigrationException($"--folder expects key=name (e.g. up=ddl), got '{folder}'.");
            }

            renamed[folder[..eq].Trim()] = folder[(eq + 1)..].Trim();
        }

        var imported = GrateImporter.Import(new GrateImportOptions
        {
            Path = path,
            Folders = renamed,
            Tokens = ParseVariables(g.Variables),
            Environments = environments.SelectMany(e => e.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
            IgnoreDirectoryNames = ignoreDirectoryNames,
            TokenReplacementDisabled = noTokens,
        });
        return WriteImport(g, imported, dryRun, Providers.Resolve(providerId));
    }

    public static int ImportSqitch(GlobalOptions g, string path, string? engine, bool dryRun, string? providerId = null)
    {
        RequireWorkingCopy(g, "import");
        var imported = SqitchImporter.Import(new SqitchImportOptions { Path = path, Engine = engine });
        // Sqitch projects for PostgreSQL ("pg") move to the PostgreSQL provider unless told otherwise.
        providerId ??= SqitchImporter.IsPostgres(imported.SourceEngine) ? PostgreSqlProvider.ProviderId : SqlServerProvider.ProviderId;
        return WriteImport(g, imported, dryRun, Providers.Resolve(providerId));
    }

    /// <summary>Writes an imported project into the (new) project folder: scripts, plan, bsm.yaml and .gitattributes.</summary>
    private static int WriteImport(GlobalOptions g, ImportedProject imported, bool dryRun, IDatabaseProvider provider)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        foreach (var existing in new[] { BsmConfig.FileName, PlanFile.FileName })
        {
            if (File.Exists(Path.Combine(dir, existing)))
            {
                throw new MigrationException($"{Path.Combine(dir, existing)} already exists. Import into a new folder: bsm import ... -C <new folder>.");
            }
        }

        var changesDir = Path.Combine(dir, MigrationProject.ChangesFolder);
        if (Directory.Exists(changesDir) && Directory.EnumerateFileSystemEntries(changesDir).Any())
        {
            throw new MigrationException($"{changesDir} is not empty. Import into a new folder.");
        }

        MarkNonTransactional(imported, provider);
        var tool = imported.Tool.ToLowerInvariant();
        if (g.Out.Json)
        {
            if (!dryRun)
            {
                Write();
            }

            g.Out.WriteJson(new
            {
                tool,
                dryRun,
                changes = imported.Changes.Select(c => new { c.Name, c.Source, flags = c.Flags.ToString(), c.Note }),
                repeatables = imported.Repeatables.Select(r => new { r.Path, r.Source }),
                variables = imported.Variables,
                undefinedVariables = imported.UndefinedVariables,
                environments = imported.Environments.Select(e => e.Name),
                warnings = imported.Warnings.Distinct(),
            });
            return Ok;
        }

        foreach (var change in imported.Changes)
        {
            var flags = string.Join(' ', new[] { change.Flags.HasFlag(ChangeFlags.NoTransaction) ? "!notx" : null, change.Flags.HasFlag(ChangeFlags.Irreversible) ? "!irreversible" : null, change.Revert is not null ? "(revert)" : null }.Where(f => f is not null));
            Console.WriteLine($"  {change.Source,-50} -> {MigrationProject.ChangesFolder}/{change.Name} {g.Out.Paint(flags, "\u001b[90m")}");
        }

        foreach (var repeatable in imported.Repeatables)
        {
            var target = repeatable.Path.StartsWith(ImportedProject.EnvironmentScriptPrefix, StringComparison.Ordinal)
                ? $"{MigrationProject.EnvironmentsFolder}/{repeatable.Path[ImportedProject.EnvironmentScriptPrefix.Length..]}"
                : $"{MigrationProject.RepeatableFolder}/{repeatable.Path}";
            Console.WriteLine($"  {repeatable.Source,-50} -> {target}");
        }

        foreach (var (name, value) in imported.Variables)
        {
            g.Out.Line($"{{{name}}}", value);
        }

        foreach (var name in imported.UndefinedVariables)
        {
            g.Out.Line($"{{{name}}}", "(no value: define it in bsm.yaml)");
        }

        foreach (var environment in imported.Environments)
        {
            g.Out.Line("environment", environment.Name + (environment.Name == imported.DefaultEnvironment ? " (default)" : string.Empty));
        }

        foreach (var warning in imported.Warnings.Distinct())
        {
            g.Out.Warn(warning);
        }

        if (dryRun)
        {
            g.Out.Success($"would import {imported.Changes.Count} change(s) and {imported.Repeatables.Count} repeatable(s) from {imported.Tool} into {dir}.");
            return Ok;
        }

        Write();
        var project = MigrationProject.Load(dir, provider.Id, includes: provider.Includes);
        foreach (var problem in project.Problems)
        {
            g.Out.Error(problem);
        }

        g.Out.Success($"imported {imported.Changes.Count} change(s) and {imported.Repeatables.Count} repeatable(s) from {imported.Tool} into {dir}; plan head {Short(project.HeadChainId)}.");
        g.Out.Info("next: review bsm.yaml, then 'bsm plan check'. New databases: 'bsm deploy'.");
        g.Out.Info(imported.CanBaselineFromJournal
            ? $"databases {imported.Tool} already manages: 'bsm baseline --from {tool} -e <environment>' records what they have without running it."
            : $"existing databases: 'bsm baseline --to <change> -e <environment>' records the changes they already have without running them.");
        return project.Problems.Count == 0 ? Ok : Failed;

        void Write()
        {
            Directory.CreateDirectory(dir);
            imported.WriteScripts(dir);
            Directory.CreateDirectory(Path.Combine(dir, MigrationProject.EnvironmentsFolder));
            File.WriteAllText(Path.Combine(dir, BsmConfig.FileName), ImportYaml.Write(imported, provider.Id), Utf8NoBom);
            WriteGitAttributes(dir);
        }
    }

    /// <summary>
    /// The source tools often run scripts without a transaction; bsm wraps each change in one. Changes whose scripts
    /// contain statements that cannot run in a transaction (per the provider's linter) are marked !notx.
    /// </summary>
    private static void MarkNonTransactional(ImportedProject imported, IDatabaseProvider provider)
    {
        var marked = new List<string>();
        for (var i = 0; i < imported.Changes.Count; i++)
        {
            var change = imported.Changes[i];
            if (change.Flags.HasFlag(ChangeFlags.NoTransaction))
            {
                continue;
            }

            var scripts = new[] { ("deploy", change.Deploy), ("revert", change.Revert), ("verify", change.Verify) };
            if (scripts.Any(s => s.Item2 is not null && provider.Linter.Lint(s.Item2, new LintContext(change.Name, s.Item1, change.Flags)).Any(d => d.Code == "BSM001")))
            {
                imported.Changes[i] = change with { Flags = change.Flags | ChangeFlags.NoTransaction };
                marked.Add(change.Name);
            }
        }

        if (marked.Count > 0)
        {
            imported.Warnings.Add($"marked !notx because they contain statements that cannot run in a transaction: {string.Join(", ", marked)}. A failure part way leaves partial work, so make them safe to re-run.");
        }
    }

    public static int Add(GlobalOptions g, string name, string[] requires, string? note, bool noTx, bool irreversible)
    {
        RequireWorkingCopy(g, "add");
        var dir = Path.GetFullPath(g.ProjectDir);
        var config = BsmConfig.Load(dir);
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
        var author = Environment.GetEnvironmentVariable("BSM_AUTHOR") ?? Environment.UserName.Replace(' ', '_');
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

    /// <summary>Other tools' journal tables, never part of a generated change.</summary>
    private static readonly string[] DefaultScriptExclusions = ["*.flyway_schema_history", "*.SchemaVersions", "grate.*", "RoundhousE.*", "sqitch.*", "*.__EFMigrationsHistory"];

    /// <summary>
    /// Captures schema edits made directly in a database as a new change: builds a shadow database from the plan
    /// (as far as the database has deployed it), compares the two, and writes deploy.sql and revert.sql. The
    /// database already has the change, so it is recorded there as applied.
    /// </summary>
    public static async Task<int> AddFromDiff(GlobalOptions g, string name, string[] requires, string? note, string? shadowEnvironment, bool keepShadow, string[] exclude, bool dryRun, bool record, CancellationToken ct)
    {
        RequireWorkingCopy(g, "add");
        var dir = Path.GetFullPath(g.ProjectDir);
        if (!PlanFile.IsValidName(name))
        {
            throw new MigrationException($"Invalid change name '{name}'. Use letters, digits, '-', '_' or '.'.");
        }

        var planPath = Path.Combine(dir, PlanFile.FileName);
        var plan = PlanFile.Load(planPath);
        if (plan.IndexOf(name) >= 0 || Directory.Exists(Path.Combine(dir, MigrationProject.ChangesFolder, name)))
        {
            throw new MigrationException($"Change '{name}' already exists.");
        }

        foreach (var dependency in requires.Where(d => plan.IndexOf(d) < 0))
        {
            throw new MigrationException($"Required change '{dependency}' is not in the plan.");
        }

        await using var ctx = await Connect(g, ct);
        if (ctx.Provider.Scripter is not { } scripter || ctx.Provider.ScratchDatabases is not { } scratch)
        {
            throw new MigrationException($"{ctx.Provider.DisplayName} cannot script schema differences.");
        }

        var status = await ctx.Engine.StatusAsync(ct);
        if (status.Mismatched.Any() || status.Orphans.Count > 0 || !status.Certified)
        {
            throw new MigrationException($"'{ctx.Environment.Name}' must have exactly a prefix of the plan deployed, in order (check 'bsm status'); otherwise the comparison would include other differences. Deploy or rebase it first.");
        }

        var lastApplied = status.Applied.Count == 0 ? null : status.Applied[^1].Name;
        g.Out.Muted($"comparing {ctx.Session.Description} with a shadow database built from the plan{(lastApplied is null ? " (repeatables only)" : $" up to '{lastApplied}'")}");

        // The shadow uses this environment's settings (variables, keys) on the shadow server.
        var server = shadowEnvironment is null ? ctx.Environment : ctx.Config.ResolveEnvironment(shadowEnvironment, null);
        var (shadowContext, shadowTunnel) = shadowEnvironment is null ? (ctx.Context, null) : Prepare(g, ctx.Provider, server, Open(g).BaseDir);
        SchemaScript script;
        string[] repeatables;
        try
        {
            await using var shadow = await scratch.CreateAsync(server, shadowContext, keepShadow, ct);
            var shadowEnvironmentSettings = ctx.Environment with
            {
                Host = shadow.Environment.Host,
                Port = shadow.Environment.Port,
                ConnectionString = shadow.Environment.ConnectionString,
                Database = shadow.Environment.Database,
                Authentication = shadow.Environment.Authentication,
                User = shadow.Environment.User,
                Password = shadow.Environment.Password,
                Entra = shadow.Environment.Entra,
                Tls = shadow.Environment.Tls,
                Ssh = shadow.Environment.Ssh,
                ConnectionProperties = shadow.Environment.ConnectionProperties,
            };
            await using var shadowSession = await ctx.Provider.OpenSessionAsync(shadowEnvironmentSettings, shadowContext, ct);
            var project = MigrationProject.Load(Open(g).ProjectDir, ctx.Provider.Id, ctx.Environment.Name, ctx.Provider.Includes);
            var shadowEngine = new MigrationEngine(project, ctx.Provider, shadowEnvironmentSettings, shadowSession, Actor.Current(ToolVersion), NullMigrationLog.Instance, ParseVariables(g.Variables))
            {
                Preprocessing = ctx.Config.Preprocessor,
                AllowEnvironmentMismatch = true,
            };
            await shadowEngine.DeployAsync(new DeployOptions { To = lastApplied, OnlyRepeatables = lastApplied is null }, ct);
            if (keepShadow)
            {
                g.Out.Info($"shadow database kept: {shadow.Environment.Database}");
            }

            repeatables = project.Repeatables.Select(r => shadowEngine.Render(r.Name, "repeatable", r.Script).Text).ToArray();
            script = await scripter.ScriptAsync(shadowSession, ctx.Session, new SchemaScriptOptions
            {
                StateSchema = ctx.Environment.StateSchema,
                Exclude = [.. DefaultScriptExclusions, .. exclude],
                RepeatableScripts = repeatables,
            }, ct);
        }
        finally
        {
            shadowTunnel?.Dispose();
        }

        foreach (var line in script.Summary)
        {
            Console.WriteLine("  " + line);
        }

        var warnings = script.Warnings.ToList();
        warnings.AddRange(EnvironmentValues(ctx.Engine, script.Deploy));
        foreach (var warning in warnings)
        {
            g.Out.Warn(warning);
        }

        if (script.Deploy.Length == 0)
        {
            g.Out.Success(warnings.Count == 0 ? $"no schema differences: '{ctx.Environment.Name}' matches the plan." : "nothing to script.");
            return Ok;
        }

        var deploy = GeneratedScript(ctx, $"Deploy {name}", script.Summary, warnings, script.Deploy);
        var revert = GeneratedScript(ctx, $"Revert {name}", [], [], script.Revert);
        if (dryRun)
        {
            Console.WriteLine(deploy);
            g.Out.Success($"would add {name} ({script.Summary.Count} difference(s)); nothing written.");
            return Ok;
        }

        var changeDir = Path.Combine(dir, MigrationProject.ChangesFolder, name);
        Directory.CreateDirectory(changeDir);
        File.WriteAllText(Path.Combine(changeDir, MigrationProject.DeployFile), deploy, Utf8NoBom);
        File.WriteAllText(Path.Combine(changeDir, MigrationProject.RevertFile), revert, Utf8NoBom);
        var notx = new[] { deploy, revert }.Any(sql => ctx.Provider.Linter.Lint(sql, new LintContext(name, "deploy", ChangeFlags.None)).Any(d => d.Code == "BSM001"));
        var author = Environment.GetEnvironmentVariable("BSM_AUTHOR") ?? Environment.UserName.Replace(' ', '_');
        AppendLine(planPath, PlanFile.FormatChange(new PlanChange(name, requires, notx ? ChangeFlags.NoTransaction : ChangeFlags.None, DateTimeOffset.UtcNow, author, note ?? $"generated from {ctx.Environment.Name}", 0)));

        g.Out.Success($"added {name}: {script.Summary.Count} difference(s){(script.DataLoss ? ", WITH DATA LOSS" : string.Empty)}. Review the scripts before committing.");
        g.Out.Info($"  {MigrationProject.ChangesFolder}/{name}/{MigrationProject.DeployFile}");
        g.Out.Info($"  {MigrationProject.ChangesFolder}/{name}/{MigrationProject.RevertFile}");
        if (record)
        {
            var project = MigrationProject.Load(Open(g).ProjectDir, ctx.Provider.Id, ctx.Environment.Name, ctx.Provider.Includes);
            var engine = new MigrationEngine(project, ctx.Provider, ctx.Environment, ctx.Session, Actor.Current(ToolVersion), g.Out, ParseVariables(g.Variables))
            {
                Preprocessing = ctx.Config.Preprocessor,
                AllowEnvironmentMismatch = g.ClaimEnvironment,
            };
            await engine.RecordAppliedAsync(name, "generated from this database by bsm add --from-diff", ct);
            g.Out.Info($"recorded {name} as applied in '{ctx.Environment.Name}' (it already has these changes). If you edit the scripts, use 'bsm repair {name}' here or 'bsm rebase'.");
        }

        return Ok;
    }

    /// <summary>The generated SQL with a header, escaped so bsm's preprocessor leaves it as written.</summary>
    private static string GeneratedScript(ConnectedContext ctx, string title, IReadOnlyList<string> summary, IReadOnlyList<string> warnings, string sql)
    {
        var header = new StringBuilder();
        header.Append(ctx.Provider.FormatComment($"{title}: generated by Badger SQL Migrate {ToolVersion} from '{ctx.Environment.Name}' on {DateTimeOffset.UtcNow:yyyy-MM-dd}. Review before committing.")).Append('\n');
        foreach (var line in summary)
        {
            header.Append(ctx.Provider.FormatComment("  " + line)).Append('\n');
        }

        foreach (var warning in warnings)
        {
            header.Append(ctx.Provider.FormatComment("WARNING: " + warning)).Append('\n');
        }

        var body = ScriptPreprocessor.EscapeLiteral(sql, ctx.Config.Preprocessor.SqlCmdSyntax);
        return body.StartsWith("-- bsm:preprocess off", StringComparison.Ordinal)
            ? body[..(body.IndexOf('\n') + 1)] + header + "\n" + body[(body.IndexOf('\n') + 1)..]
            : header + "\n" + body;
    }

    /// <summary>
    /// Rendered values of this environment's variables (and {env}, {db}) that appear in generated SQL: written into
    /// the change, they would be the same in every environment.
    /// </summary>
    private static IEnumerable<string> EnvironmentValues(MigrationEngine engine, string sql)
    {
        var environment = engine.Environment;
        var names = environment.Variables.Keys.Select(k => k).Concat(["env", "db"]);
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string value;
            try
            {
                value = engine.Preprocessor.Resolve(name);
            }
            catch (MigrationException)
            {
                continue;
            }

            if (value.Length >= 4 && !string.Equals(value, "dbo", StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(sql, $@"(?<![A-Za-z0-9_]){System.Text.RegularExpressions.Regex.Escape(value)}(?![A-Za-z0-9_])", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                yield return $"the script contains '{value}', the value of {{{name}}} in '{environment.Name}'. If it differs per environment, replace it with {{{name}}}.";
            }
        }
    }

    public static int Tag(GlobalOptions g, string name)
    {
        RequireWorkingCopy(g, "tag");
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
        var config = BsmConfig.Load(Open(g).ConfigDir);
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
                    errors.Add($"{path} line {reference.Line}: variable '{reference.Name}' is not defined in bsm.yaml (or write {{{{{reference.Name}}}}} for a literal).");
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
            g.Out.Highlight(group.Key == "builtin" ? "built-in" : group.Key == "config" ? "bsm.yaml" : "--var");
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
        var ws = Open(g);
        var config = BsmConfig.Load(ws.ConfigDir);
        var provider = Providers.Resolve(config.Provider);
        var environment = config.ResolveEnvironment(g.Environment, g.ConnectionString);
        var project = MigrationProject.Load(ws.ProjectDir, provider.Id, environment.Name, provider.Includes);
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

    public static async Task<int> Revert(GlobalOptions g, string? to, int? last, bool all, string[] repeatables, bool dryRun, bool yes, bool allowVariableDrift, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var options = new RevertOptions { To = to, Last = last, All = all, Repeatables = repeatables, AllowVariableDrift = allowVariableDrift };
        if (!dryRun)
        {
            var preview = await ctx.Engine.RevertAsync(options with { DryRun = true }, ct);
            var names = preview.Reverted.Concat(preview.Repeatables).ToList();
            if (names.Count == 0)
            {
                g.Out.Success("nothing to revert.");
                return Ok;
            }

            var what = repeatables.Length > 0 ? $"Restore the previous definition of {names.Count} repeatable(s) in" : $"Revert {names.Count} change(s) from";
            Confirm(g, yes, ctx.Environment, $"{what} '{ctx.Environment.Name}' ({ctx.Session.Description}): {string.Join(", ", names)}?");
        }

        var result = await ctx.Engine.RevertAsync(options with { DryRun = dryRun }, ct);
        if (g.Out.Json)
        {
            g.Out.WriteJson(result);
        }
        else if (repeatables.Length > 0)
        {
            g.Out.Success($"{(dryRun ? "would restore" : "restored")} {result.Repeatables.Count} repeatable(s) to their previous definition.");
            g.Out.Info("the working copy still has the newer version, so the next deploy re-applies it unless you change it too.");
        }
        else
        {
            g.Out.Success($"{(dryRun ? "would revert" : "reverted")} {result.Reverted.Count} change(s).");
        }

        return Ok;
    }

    public static async Task<int> Repair(GlobalOptions g, string[] names, bool all, string? reason, bool dryRun, bool yes, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        var options = new RepairOptions { Names = names, All = all, Reason = reason };
        var preview = await ctx.Engine.RepairAsync(options with { DryRun = true }, ct);
        if (preview.Repaired.Count == 0)
        {
            if (g.Out.Json)
            {
                g.Out.WriteJson(preview);
            }
            else
            {
                g.Out.Success("nothing to repair; deployed changes match the working copy.");
            }

            return Ok;
        }

        foreach (var item in preview.Repaired)
        {
            g.Out.Info($"  {item.Name}: {string.Join(", ", item.Scripts)} differ from what was deployed");
        }

        var deployEdited = preview.Repaired.Where(r => r.Scripts.Contains(MigrationProject.DeployFile)).Select(r => r.Name).ToList();
        if (deployEdited.Count > 0)
        {
            g.Out.Warn($"{string.Join(", ", deployEdited)}: {MigrationProject.DeployFile} was edited. Repair does not run it, so other databases that already have the change keep the old version. Only repair edits that change nothing (comments, formatting); put real changes in a new change with 'bsm rework'.");
        }

        if (dryRun)
        {
            if (g.Out.Json)
            {
                g.Out.WriteJson(preview);
            }
            else
            {
                g.Out.Success($"would repair {preview.Repaired.Count} change(s).");
            }

            return Ok;
        }

        Confirm(g, yes, ctx.Environment, $"Record the working copy of {string.Join(", ", preview.Repaired.Select(r => r.Name))} as deployed to '{ctx.Environment.Name}' ({ctx.Session.Description}) without running anything?", force: deployEdited.Count > 0);
        var result = await ctx.Engine.RepairAsync(options, ct);
        if (g.Out.Json)
        {
            g.Out.WriteJson(result);
        }
        else
        {
            g.Out.Success($"repaired {result.Repaired.Count} change(s); the journal now matches the working copy.");
        }

        return Ok;
    }

    /// <summary>
    /// Adds a new change that supersedes a deployed one: deploy.sql starts as a copy of the old deploy.sql (edit it),
    /// revert.sql restores the old definition, and the new change requires the old one.
    /// </summary>
    public static int Rework(GlobalOptions g, string name, string? newName, string? note)
    {
        RequireWorkingCopy(g, "rework");
        var dir = Path.GetFullPath(g.ProjectDir);
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
        Directory.CreateDirectory(newDir);
        File.WriteAllText(Path.Combine(newDir, MigrationProject.DeployFile), deploy, Utf8NoBom);
        File.WriteAllText(
            Path.Combine(newDir, MigrationProject.RevertFile),
            provider.FormatComment($"Revert {newName}: restore what {name} deployed.") + "\n" +
            provider.FormatComment($"Copied from {MigrationProject.ChangesFolder}/{name}/{MigrationProject.DeployFile}; edit it if that script cannot simply be run again.") + "\n\n" + deploy,
            Utf8NoBom);
        var oldVerify = Path.Combine(oldDir, MigrationProject.VerifyFile);
        if (File.Exists(oldVerify))
        {
            File.Copy(oldVerify, Path.Combine(newDir, MigrationProject.VerifyFile));
        }

        var old = plan.Changes[index];
        var author = Environment.GetEnvironmentVariable("BSM_AUTHOR") ?? Environment.UserName.Replace(' ', '_');
        AppendLine(planPath, PlanFile.FormatChange(new PlanChange(newName, [name], old.Flags & ChangeFlags.NoTransaction, DateTimeOffset.UtcNow, author, note ?? $"rework of {name}", 0)));

        g.Out.Success($"added {newName} (rework of {name})");
        g.Out.Info($"  {MigrationProject.ChangesFolder}/{newName}/{MigrationProject.DeployFile}   copy of {name}'s deploy script: edit this");
        g.Out.Info($"  {MigrationProject.ChangesFolder}/{newName}/{MigrationProject.RevertFile}   restores {name}'s version");
        if (File.Exists(oldVerify))
        {
            g.Out.Info($"  {MigrationProject.ChangesFolder}/{newName}/{MigrationProject.VerifyFile}");
        }

        g.Out.Muted($"leave {MigrationProject.ChangesFolder}/{name}/ as it was deployed. If you already edited it, move those edits here and restore it (e.g. git checkout -- {MigrationProject.ChangesFolder}/{name}).");
        return Ok;
    }

    /// <summary>name → name-v2; name-v2 → name-v3; skipping names already taken.</summary>
    public static string NextRevisionName(string name, Func<string, bool> taken)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(?<base>.+)-v(?<n>\d+)$");
        var stem = match.Success ? match.Groups["base"].Value : name;
        var n = match.Success ? int.Parse(match.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) + 1 : 2;
        while (taken($"{stem}-v{n}"))
        {
            n++;
        }

        return $"{stem}-v{n}";
    }

    public static int BundleCreate(GlobalOptions g, string? output, bool noConfig)
    {
        RequireWorkingCopy(g, "bundle create");
        var dir = Path.GetFullPath(g.ProjectDir);
        var config = BsmConfig.Load(dir);
        var provider = Providers.Resolve(config.Provider);
        var head = MigrationProject.Load(dir, provider.Id, includes: provider.Includes).HeadChainId;
        output ??= $"bsm-bundle-{(head is null ? "empty" : head[..12])}.zip";
        var manifest = ProjectBundle.Create(dir, provider.Id, output, includeConfig: !noConfig, ToolVersion, provider.Includes);
        if (g.Out.Json)
        {
            g.Out.WriteJson(new { path = Path.GetFullPath(output), manifest });
            return Ok;
        }

        g.Out.Success($"wrote {output}");
        PrintManifest(g, manifest);
        if (!noConfig)
        {
            g.Out.Muted($"includes {BsmConfig.FileName}; keep secrets out of it (use ${{env:NAME}} or ${{file:path}}), or bundle with --no-config.");
        }

        return Ok;
    }

    public static int BundleVerify(GlobalOptions g, string path)
    {
        var manifest = ProjectBundle.Verify(path);
        if (g.Out.Json)
        {
            g.Out.WriteJson(new { ok = true, manifest });
            return Ok;
        }

        PrintManifest(g, manifest);
        g.Out.Success("bundle verified: every file matches its hash.");
        return Ok;
    }

    private static void PrintManifest(GlobalOptions g, BundleManifest manifest)
    {
        g.Out.Line("bundle", manifest.ContentHash);
        g.Out.Line("provider", manifest.Provider);
        g.Out.Line("plan head", manifest.PlanHead ?? "(empty)");
        g.Out.Line("contents", $"{manifest.Changes} change(s), {manifest.Repeatables} repeatable(s), {manifest.Files.Count} file(s){(manifest.IncludesConfig ? $", {BsmConfig.FileName}" : string.Empty)}");
        g.Out.Line("made by", $"Badger SQL Migrate {manifest.ToolVersion}");
    }

    public static async Task<int> DiffSchema(GlobalOptions g, string? against, bool exitCode, string? scriptPath, CancellationToken ct)
    {
        await using var ctx = await Connect(g, ct);
        if (scriptPath is not null)
        {
            if (against is null)
            {
                throw new MigrationException("--script needs --against <environment>: it scripts what turns this database into that one.");
            }

            if (ctx.Provider.Scripter is not { } scripter)
            {
                throw new MigrationException($"{ctx.Provider.DisplayName} cannot script schema differences.");
            }

            await using var target = await Connect(g with { Environment = against, ConnectionString = null }, ct);
            var script = await scripter.ScriptAsync(ctx.Session, target.Session, new SchemaScriptOptions { StateSchema = ctx.Environment.StateSchema, Exclude = DefaultScriptExclusions }, ct);
            var text = new StringBuilder();
            text.Append(ctx.Provider.FormatComment($"Turns {ctx.Environment.Name} ({ctx.Session.Description}) into {target.Environment.Name} ({target.Session.Description}). Generated by Badger SQL Migrate {ToolVersion}; review before running.")).Append('\n');
            foreach (var line in script.Summary)
            {
                text.Append(ctx.Provider.FormatComment("  " + line)).Append('\n');
            }

            foreach (var warning in script.Warnings)
            {
                text.Append(ctx.Provider.FormatComment("WARNING: " + warning)).Append('\n');
                g.Out.Warn(warning);
            }

            await File.WriteAllTextAsync(scriptPath, text.Append('\n').Append(script.Deploy).ToString(), Utf8NoBom, ct);
            g.Out.Success($"wrote {scriptPath}: {script.Summary.Count} difference(s){(script.DataLoss ? ", with data loss" : string.Empty)}.");
            return exitCode && script.Summary.Count > 0 ? NotUpToDate : Ok;
        }

        string leftLabel, rightLabel, leftDump, rightDump;
        if (against is null)
        {
            var recorded = await ctx.Engine.GetRecordedSchemaAsync(ct)
                ?? throw new MigrationException($"No schema has been recorded for '{ctx.Environment.Name}' yet (bsm records it after each deploy). Use --against <environment> to compare two databases.");
            leftDump = recorded.Dump;
            leftLabel = $"{ctx.Environment.Name} as recorded {recorded.CapturedAtUtc:yyyy-MM-dd HH:mm}Z";
            if (await ctx.Engine.RecordedWithOtherSchemaFormatAsync(ct))
            {
                g.Out.Warn("the recorded schema was captured by an older version of bsm with a different format; some differences may only be format (e.g. system-versioned tables, partitions).");
            }
            rightDump = await ctx.Engine.CaptureSchemaAsync(ct);
            rightLabel = $"{ctx.Environment.Name} live";
        }
        else
        {
            await using var other = await Connect(g with { Environment = against, ConnectionString = null }, ct);
            leftDump = await ctx.Engine.CaptureSchemaAsync(ct);
            leftLabel = $"{ctx.Environment.Name} ({ctx.Session.Description})";
            rightDump = await other.Engine.CaptureSchemaAsync(ct);
            rightLabel = $"{other.Environment.Name} ({other.Session.Description})";
        }

        var differences = SchemaDiff.Compare(ctx.Provider.Schema.Parse(leftDump), ctx.Provider.Schema.Parse(rightDump));
        if (g.Out.Json)
        {
            g.Out.WriteJson(new
            {
                left = leftLabel,
                right = rightLabel,
                leftFingerprint = ContentHasher.Hash(leftDump),
                rightFingerprint = ContentHasher.Hash(rightDump),
                identical = differences.Count == 0,
                differences = differences.Select(d => new { kind = d.Kind.ToString().ToLowerInvariant(), d.Key, d.Left, d.Right }),
            });
        }
        else
        {
            g.Out.Line("-", leftLabel);
            g.Out.Line("+", rightLabel);
            foreach (var d in differences)
            {
                switch (d.Kind)
                {
                    case SchemaDifferenceKind.Added:
                        Console.WriteLine($"{g.Out.Paint("+", "[32m")} {d.Key}");
                        break;
                    case SchemaDifferenceKind.Removed:
                        Console.WriteLine($"{g.Out.Paint("-", "[31m")} {d.Key}");
                        break;
                    default:
                        Console.WriteLine($"{g.Out.Paint("~", "[33m")} {d.Key}");
                        foreach (var (op, line) in SchemaDiff.Lines(d.Left!, d.Right!))
                        {
                            var ansi = op switch { '+' => "[32m", '-' => "[31m", _ => "[90m" };
                            Console.WriteLine("    " + g.Out.Paint($"{op} {line}", ansi));
                        }

                        break;
                }
            }

            if (differences.Count == 0)
            {
                g.Out.Success($"schemas are identical (fingerprint {Short(ContentHasher.Hash(rightDump))}).");
            }
            else
            {
                g.Out.Info($"{differences.Count(d => d.Kind == SchemaDifferenceKind.Added)} added, {differences.Count(d => d.Kind == SchemaDifferenceKind.Removed)} removed, {differences.Count(d => d.Kind == SchemaDifferenceKind.Changed)} changed.");
            }
        }

        return exitCode && differences.Count > 0 ? NotUpToDate : Ok;
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

    public static async Task<int> Baseline(GlobalOptions g, string? to, string? from, string? journal, bool skipChecksums, bool yes, CancellationToken ct)
    {
        if ((to is null) == (from is null))
        {
            throw new MigrationException("Pass --to <change|@tag>, or --from flyway|dbup|grate to read what the database already has from that tool's journal.");
        }

        await using var ctx = await Connect(g, ct);
        IReadOnlyList<string> repeatables = [];
        if (from is not null)
        {
            var (schema, table) = SplitTable(journal, from, ctx.Provider.DefaultSchema);
            var project = MigrationProject.Load(Open(g).ProjectDir, ctx.Provider.Id, ctx.Environment.Name, ctx.Provider.Includes);
            string Rendered(RepeatableScript r) => ctx.Engine.Render(r.Name, "repeatable", r.Script).Text;
            switch (from.ToLowerInvariant())
            {
                case "flyway":
                    var history = await ForeignJournal.ReadFlywayAsync(ctx.Session, ctx.Provider, schema, table, ct);
                    to = ForeignJournal.FlywayTarget(project, history, checkChecksums: !skipChecksums);
                    repeatables = ForeignJournal.FlywayRepeatables(project, history, Rendered);
                    break;
                case "dbup":
                    to = ForeignJournal.DbUpTarget(project, await ForeignJournal.ReadDbUpAsync(ctx.Session, ctx.Provider, schema, table, ct));
                    break;
                case "sqitch":
                    to = ForeignJournal.SqitchTarget(project, await ForeignJournal.ReadSqitchAsync(ctx.Session, ctx.Provider, schema, table, ct), checkHashes: !skipChecksums);
                    break;
                case "grate" or "roundhouse":
                    var runs = await ForeignJournal.ReadGrateAsync(ctx.Session, ctx.Provider, schema, table, ct);
                    to = ForeignJournal.GrateTarget(project, runs, skipChecksums ? null : change => ctx.Engine.Render(change.Name, "deploy", change.DeployScript, change.ScriptHash).Text);
                    repeatables = ForeignJournal.GrateRepeatables(project, runs, Rendered);
                    break;
                default:
                    throw new MigrationException("--from must be flyway, dbup, grate, roundhouse or sqitch.");
            }
            if (to is null)
            {
                g.Out.Success($"{from} has not applied any of the imported changes to this database; nothing to baseline. Use 'bsm deploy'.");
                return Ok;
            }

            g.Out.Info($"{from} journal {schema}.{table}: applied up to and including '{to}'.");
        }

        Confirm(g, yes, ctx.Environment, $"Record changes up to '{to}' as already applied on '{ctx.Environment.Name}' ({ctx.Session.Description}) without running them?");
        var names = await ctx.Engine.BaselineAsync(to!, ct);
        g.Out.Success($"baselined {names.Count} change(s).");
        if (from is not null)
        {
            var recorded = await ctx.Engine.BaselineRepeatablesAsync(repeatables.ToHashSet(StringComparer.Ordinal), ct);
            if (recorded.Count > 0)
            {
                g.Out.Success($"recorded {recorded.Count} repeatable(s) {from} already applied with the same text: {string.Join(", ", recorded)}.");
            }

            g.Out.Info($"the next 'bsm deploy' applies later changes and any other repeatables. {from}'s own journal table is left in place; drop it when you no longer need it.");
        }
        return Ok;
    }

    public static async Task<int> Doctor(GlobalOptions g, CancellationToken ct)
    {
        var config = BsmConfig.Load(Open(g).ConfigDir);
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
        var config = BsmConfig.Load(Open(g).ConfigDir);
        var rows = config.Environments.Keys.Order(StringComparer.Ordinal).Select(name =>
        {
            var merged = config.Merge(name);
            return new
            {
                name,
                template = BsmConfig.IsTemplate(name),
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
        var config = BsmConfig.Load(Open(g).ConfigDir);
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
        var ws = Open(g);
        var config = BsmConfig.Load(ws.ConfigDir);
        var provider = Providers.Resolve(config.Provider);
        return (MigrationProject.Load(ws.ProjectDir, provider.Id, includes: provider.Includes), provider);
    }

    private static readonly Dictionary<string, Workspace> OpenedBundles = new(StringComparer.Ordinal);

    /// <summary>Resolves --project / --bundle. A bundle is verified and extracted once per process, then deleted on exit.</summary>
    private static Workspace Open(GlobalOptions g)
    {
        var dir = Path.GetFullPath(g.ProjectDir);
        if (g.Bundle is null)
        {
            return new Workspace(dir, dir, dir, null);
        }

        var bundle = Path.GetFullPath(g.Bundle);
        if (OpenedBundles.TryGetValue(bundle, out var opened))
        {
            return opened;
        }

        var extracted = Path.Combine(Path.GetTempPath(), "bsm-bundle-" + Guid.NewGuid().ToString("N"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(extracted, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };

        var manifest = ProjectBundle.Extract(bundle, extracted, id => Providers.Resolve(id).Includes);
        if (!manifest.IncludesConfig && !File.Exists(Path.Combine(dir, BsmConfig.FileName)))
        {
            throw new MigrationException($"The bundle has no {BsmConfig.FileName} and there is none in '{dir}'. Pass --project <dir with {BsmConfig.FileName}>.");
        }

        g.Out.Muted($"bundle {Short(manifest.ContentHash)}: {manifest.Changes} change(s), plan head {Short(manifest.PlanHead)}, verified");
        opened = new Workspace(extracted, manifest.IncludesConfig ? extracted : dir, dir, manifest);
        OpenedBundles[bundle] = opened;
        return opened;
    }

    /// <summary>Commands that write project files cannot work on a bundle.</summary>
    private static void RequireWorkingCopy(GlobalOptions g, string command)
    {
        if (g.Bundle is not null)
        {
            throw new MigrationException($"'bsm {command}' changes project files and cannot be used with --bundle.");
        }
    }

    private static async Task<ConnectedContext> Connect(GlobalOptions g, CancellationToken ct)
    {
        var ws = Open(g);
        var dir = ws.ProjectDir;
        var config = BsmConfig.Load(ws.ConfigDir);
        var provider = Providers.Resolve(config.Provider);
        var environment = config.ResolveEnvironment(g.Environment, g.ConnectionString);
        var problems = provider.ValidateEnvironment(environment);
        if (problems.Count > 0)
        {
            throw new MigrationException(string.Join("\n", problems));
        }

        var project = MigrationProject.Load(dir, provider.Id, environment.Name, provider.Includes);
        var (context, tunnel) = Prepare(g, provider, environment, ws.BaseDir);
        try
        {
            var session = await provider.OpenSessionAsync(environment, context, ct);
            var engine = new MigrationEngine(project, provider, environment, session, Actor.Current(ToolVersion), g.Out, ParseVariables(g.Variables))
            {
                AllowEnvironmentMismatch = g.ClaimEnvironment,
                Preprocessing = config.Preprocessor,
            };
            return new ConnectedContext(environment, provider, session, engine, tunnel) { Context = context, Config = config };
        }
        catch
        {
            tunnel?.Dispose();
            throw;
        }
    }

    /// <summary>Sign-in and SSH tunnel for an environment.</summary>
    private static (SessionContext Context, SshTunnel? Tunnel) Prepare(GlobalOptions g, IDatabaseProvider provider, DeploymentEnvironment environment, string baseDir)
    {
        SshTunnel? tunnel = null;
        if (environment.Ssh is not null)
        {
            tunnel = SshTunnel.Open(environment.Ssh, provider.ResolveEndpoint(environment), g.Out.Warn);
            g.Out.Detail($"ssh tunnel {environment.Ssh.User}@{environment.Ssh.Host} -> {tunnel.LocalEndpoint.Host}:{tunnel.LocalEndpoint.Port}");
        }

        Action<string> prompt = m => Console.Error.WriteLine(m);
        return (new SessionContext
        {
            EndpointOverride = tunnel?.LocalEndpoint,
            Tokens = EntraTokenProvider.Create(environment.Authentication, environment.Entra, environment.Name, prompt),
            CreateTokenProvider = (kind, options) => EntraTokenProvider.Create(kind, options, environment.Name + ":keyvault", prompt),
            Warn = g.Out.Warn,
            BaseDirectory = baseDir,
        }, tunnel);
    }

    /// <summary>"schema.table" or "table", with each tool's default schema and table (RoundhousE: RoundhousE.ScriptsRun).</summary>
    private static (string Schema, string Table) SplitTable(string? journal, string tool, string defaultSchema)
    {
        var (schema, table) = tool.ToLowerInvariant() switch
        {
            "dbup" => (defaultSchema, ForeignJournal.DbUpDefaultTable),
            "grate" => (ForeignJournal.GrateDefaultSchema, ForeignJournal.GrateDefaultTable),
            "roundhouse" => ("RoundhousE", ForeignJournal.GrateDefaultTable),
            "sqitch" => (ForeignJournal.SqitchDefaultSchema, ForeignJournal.SqitchDefaultTable),
            _ => (defaultSchema, ForeignJournal.FlywayDefaultTable),
        };
        if (string.IsNullOrWhiteSpace(journal))
        {
            return (schema, table);
        }

        var dot = journal.LastIndexOf('.');
        return dot < 0 ? (schema, journal) : (journal[..dot], journal[(dot + 1)..]);
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

    private sealed record ConnectedContext(DeploymentEnvironment Environment, IDatabaseProvider Provider, IDatabaseSession Session, MigrationEngine Engine, SshTunnel? Tunnel) : IAsyncDisposable
    {
        public SessionContext Context { get; init; } = SessionContext.Empty;

        public BsmConfig Config { get; init; } = null!;

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
