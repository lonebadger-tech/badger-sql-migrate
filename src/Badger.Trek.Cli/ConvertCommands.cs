using System.Text.RegularExpressions;
using Badger.Trek.Connectivity;
using Badger.Trek.Conversion;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Cli;

/// <summary>'trek convert': move a live database to the other provider as a new trek project, then copy its rows.</summary>
public static partial class Commands
{
    public const string ConversionReportFile = "CONVERSION.md";

    /// <summary>How 'convert schema' mapped names, so 'convert data' maps them the same way.</summary>
    public const string ConversionSettingsFile = "convert.json";

    private sealed record ConversionSettings(string Source, string Names, bool CaseSensitive, string[] Exclude);

    /// <summary>The source database: an environment of the project in --project, or --connection-string with --from-provider.</summary>
    private sealed class Source(IDatabaseProvider provider, DeploymentEnvironment environment, IDatabaseSession session, SshTunnel? tunnel) : IAsyncDisposable
    {
        public IDatabaseProvider Provider { get; } = provider;

        public DeploymentEnvironment Environment { get; } = environment;

        public IDatabaseSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            tunnel?.Dispose();
        }
    }

    private static async Task<Source> OpenSource(GlobalOptions g, string? fromProvider, CancellationToken ct)
    {
        IDatabaseProvider provider;
        DeploymentEnvironment environment;
        string baseDir;
        var projectConfig = Path.Combine(Path.GetFullPath(g.ProjectDir), TrekConfig.FileName);
        if (fromProvider is null && File.Exists(projectConfig))
        {
            var ws = Open(g);
            var config = TrekConfig.Load(ws.ConfigDir);
            provider = config.CreateProvider();
            environment = config.ResolveEnvironment(g.Environment, g.ConnectionString);
            baseDir = ws.BaseDir;
        }
        else
        {
            if (fromProvider is null || g.ConnectionString is null)
            {
                throw new MigrationException($"No {TrekConfig.FileName} in {Path.GetFullPath(g.ProjectDir)}: pass --from-provider and --connection-string for the source database, or -C <project> -e <environment>.");
            }

            provider = Providers.Resolve(fromProvider);
            environment = new DeploymentEnvironment { Name = "source", ConnectionString = g.ConnectionString };
            baseDir = Directory.GetCurrentDirectory();
        }

        var problems = provider.ValidateEnvironment(environment);
        if (problems.Count > 0)
        {
            throw new MigrationException(string.Join("\n", problems));
        }

        var (context, tunnel) = Prepare(g, provider, environment, baseDir);
        try
        {
            var session = await provider.OpenSessionAsync(environment, context, ct);
            return new Source(provider, environment, session, tunnel);
        }
        catch
        {
            tunnel?.Dispose();
            throw;
        }
    }

    private static string OtherProvider(string provider) => ConversionContext.DialectOf(provider) switch
    {
        Dialect.SqlServer => "postgresql",
        Dialect.PostgreSql => "sqlserver",
        _ => throw new MigrationException("Pass --to sqlserver or --to postgresql: a SQLite database can become either."),
    };

    private static Func<PortableName, bool> Globs(IEnumerable<string> patterns)
    {
        var regexes = patterns.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        return name => regexes.Any(r => r.IsMatch($"{name.Schema}.{name.Name}"));
    }

    public static async Task<int> ConvertSchema(GlobalOptions g, string outDir, string? toProvider, string? fromProvider, string? names, bool caseSensitive, string[] exclude, bool dryRun, CancellationToken ct)
    {
        RequireWorkingCopy(g, "convert");
        PortableSchema schema;
        string target;
        await using (var source = await OpenSource(g, fromProvider, ct))
        {
            var exporter = source.Provider.Exporter ?? throw new MigrationException($"{source.Provider.DisplayName} databases cannot be converted yet.");
            target = Providers.Resolve(toProvider ?? OtherProvider(source.Provider.Id)).Id;
            g.Out.Muted($"reading {source.Session.Description}");
            schema = await exporter.ExportAsync(source.Session, new ExportOptions
            {
                StateSchema = source.Environment.StateSchema,
                Excluded = Globs([.. DefaultScriptExclusions, .. exclude]),
            }, ct);
        }

        var result = ConversionProject.Build(schema, new ConversionOptions
        {
            TargetProvider = target,
            Names = names is null ? null : Enum.Parse<NameStyle>(names, ignoreCase: true),
            CaseSensitive = caseSensitive,
        });
        if (!g.Out.Json)
        {
            g.Out.Muted($"{schema.Tables.Count} table(s), {schema.Sequences.Count} sequence(s), {schema.Code.Count} view(s), routine(s) and trigger(s) -> {target}");
        }

        var output = g with { ProjectDir = outDir };
        var code = WriteImport(output, result.Project, dryRun, Providers.Resolve(target));
        if (!dryRun)
        {
            var report = Path.Combine(Path.GetFullPath(outDir), ConversionReportFile);
            await File.WriteAllTextAsync(report, result.Report, Utf8NoBom, ct);
            var settings = new ConversionSettings(schema.SourceProvider, result.Context.Names.Style.ToString().ToLowerInvariant(), caseSensitive, exclude);
            await File.WriteAllTextAsync(Path.Combine(Path.GetFullPath(outDir), ConversionSettingsFile),
                System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }) + "\n", Utf8NoBom, ct);
            if (!g.Out.Json)
            {
                g.Out.Info($"read {report} first: {result.Context.Issues.Count} item(s) to review. Then deploy the new project and run 'trek convert data'.");
            }
        }

        return code;
    }

    public static async Task<int> ConvertData(GlobalOptions g, string targetDir, string? targetEnvironment, string? fromProvider, string[] tables, bool truncate, CancellationToken ct)
    {
        var targetRoot = Path.GetFullPath(targetDir);
        var settingsPath = Path.Combine(targetRoot, ConversionSettingsFile);
        var settings = File.Exists(settingsPath)
            ? System.Text.Json.JsonSerializer.Deserialize<ConversionSettings>(await File.ReadAllTextAsync(settingsPath, ct), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            : null;
        if (settings is null)
        {
            throw new MigrationException($"{settingsPath} not found: --target must be the project 'trek convert schema' wrote.");
        }

        var targetConfig = TrekConfig.Load(targetRoot);
        var targetProvider = targetConfig.CreateProvider();
        var targetEnv = targetConfig.ResolveEnvironment(targetEnvironment, null);
        var problems = targetProvider.ValidateEnvironment(targetEnv);
        if (problems.Count > 0)
        {
            throw new MigrationException(string.Join("\n", problems));
        }

        await using var source = await OpenSource(g, fromProvider, ct);
        var reader = source.Provider.Data ?? throw new MigrationException($"{source.Provider.DisplayName} cannot export data.");
        var writer = targetProvider.Data ?? throw new MigrationException($"{targetProvider.DisplayName} cannot load data.");
        var exporter = source.Provider.Exporter ?? throw new MigrationException($"{source.Provider.DisplayName} databases cannot be converted.");
        var schema = await exporter.ExportAsync(source.Session, new ExportOptions
        {
            StateSchema = source.Environment.StateSchema,
            Excluded = Globs([.. DefaultScriptExclusions, .. settings.Exclude]),
        }, ct);
        var ctx = new ConversionContext(schema, new ConversionOptions
        {
            TargetProvider = targetProvider.Id,
            Names = Enum.Parse<NameStyle>(settings.Names, ignoreCase: true),
            CaseSensitive = settings.CaseSensitive,
        });

        var (context, tunnel) = Prepare(g, targetProvider, targetEnv, targetRoot);
        try
        {
            await using var target = await targetProvider.OpenSessionAsync(targetEnv, context, ct);
            g.Out.Muted($"copying {source.Session.Description} -> {target.Description}");
            var include = tables.Length == 0 ? (Func<PortableName, bool>)(_ => true) : Globs(tables);
            var results = await DataCopier.CopyAsync(ctx, reader, source.Session, writer, target, new CopyOptions { Include = include, Truncate = truncate }, m => g.Out.Detail(m), ct);
            var mismatched = results.Where(r => r.SourceRows != r.TargetRows).ToList();
            if (g.Out.Json)
            {
                g.Out.WriteJson(new { tables = results.Select(r => new { source = r.Source.ToString(), target = r.Target.ToString(), r.SourceRows, r.TargetRows, skippedColumns = r.Skipped }) });
            }
            else
            {
                foreach (var r in results)
                {
                    Console.WriteLine($"  {r.Source,-40} -> {r.Target,-40} {r.TargetRows,12:N0} row(s){(r.SourceRows != r.TargetRows ? $" (source has {r.SourceRows:N0})" : string.Empty)}");
                    foreach (var column in r.Skipped)
                    {
                        g.Out.Warn($"{r.Source}.{column}: no matching column in the target; not copied.");
                    }
                }

                if (mismatched.Count == 0)
                {
                    g.Out.Success($"copied {results.Sum(r => r.TargetRows):N0} row(s) in {results.Count} table(s); row counts match.");
                }
            }

            foreach (var r in mismatched)
            {
                g.Out.Error($"{r.Target}: {r.TargetRows} row(s) in the target, {r.SourceRows} in the source.");
            }

            return mismatched.Count == 0 ? Ok : Failed;
        }
        finally
        {
            tunnel?.Dispose();
        }
    }
}
