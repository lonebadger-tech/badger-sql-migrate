using System.Diagnostics;
using System.Text;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Preprocessing;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Engine;

public sealed record DeployOptions
{
    /// <summary>Deploy up to and including this change name or "@tag". Null = whole plan.</summary>
    public string? To { get; init; }

    /// <summary>Report what would run without executing anything.</summary>
    public bool DryRun { get; init; }

    /// <summary>Overrides the profile's ordering policy.</summary>
    public OrderingPolicy? Ordering { get; init; }

    public bool SkipRepeatables { get; init; }

    /// <summary>Deploy no changes, only repeatables (for building a copy of a database that has none).</summary>
    public bool OnlyRepeatables { get; init; }
}

public sealed record RevertOptions
{
    /// <summary>Revert everything deployed after this change name or "@tag" (the target stays applied).</summary>
    public string? To { get; init; }

    public int? Last { get; init; }

    public bool All { get; init; }

    /// <summary>
    /// Restore these repeatables (e.g. "views/vCustomer.sql") to the definition they had before their last apply,
    /// instead of reverting changes.
    /// </summary>
    public IReadOnlyList<string> Repeatables { get; init; } = [];

    public bool DryRun { get; init; }

    /// <summary>
    /// Revert even though the revert script now renders differently than at deploy time
    /// (a variable used by it changed value).
    /// </summary>
    public bool AllowVariableDrift { get; init; }
}

public sealed record DeployResult(IReadOnlyList<string> Deployed, IReadOnlyList<string> Repeatables, bool DryRun);

public sealed record RevertResult(IReadOnlyList<string> Reverted, bool DryRun)
{
    public IReadOnlyList<string> Repeatables { get; init; } = [];
}

public sealed record RepairOptions
{
    /// <summary>Changes to repair. Empty with <see cref="All"/> = every change whose hash no longer matches.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    public bool All { get; init; }

    /// <summary>Why the deployed scripts were edited; recorded in the event log.</summary>
    public string? Reason { get; init; }

    public bool DryRun { get; init; }
}

/// <param name="Name">The repaired change.</param>
/// <param name="Scripts">Which of deploy, revert, verify and the plan line (requires/flags) differ from what was deployed.</param>
public sealed record RepairedChange(string Name, IReadOnlyList<string> Scripts);

public sealed record RepairResult(IReadOnlyList<RepairedChange> Repaired, bool DryRun);

public sealed record VerifyResult(StatusReport Status, string? RecordedFingerprint, string? LiveFingerprint, IReadOnlyList<string> Problems)
{
    public bool Drifted => RecordedFingerprint is not null && LiveFingerprint is not null && !string.Equals(RecordedFingerprint, LiveFingerprint, StringComparison.Ordinal);

    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Vendor-neutral migration engine. All database specifics go through <see cref="IDatabaseProvider"/>;
/// this class never references a driver.
/// </summary>
public sealed class MigrationEngine
{
    private readonly MigrationProject project;
    private readonly IDatabaseProvider provider;
    private readonly DeploymentEnvironment environment;
    private readonly IDatabaseSession session;
    private readonly IMigrationLog log;
    private readonly Actor actor;
    private readonly IReadOnlyDictionary<string, string> commandLineVariables;
    private readonly DateTimeOffset runStartedUtc = DateTimeOffset.UtcNow;
    private readonly string runId = Guid.NewGuid().ToString("N");
    private ScriptPreprocessor? preprocessor;

    public MigrationEngine(
        MigrationProject project,
        IDatabaseProvider provider,
        DeploymentEnvironment environment,
        IDatabaseSession session,
        Actor actor,
        IMigrationLog? log = null,
        IReadOnlyDictionary<string, string>? variableOverrides = null)
    {
        if (!string.Equals(project.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new MigrationException($"Project targets provider '{project.ProviderId}' but provider '{provider.Id}' was supplied.");
        }

        this.project = project;
        this.provider = provider;
        this.environment = environment;
        this.session = session;
        this.actor = actor;
        this.log = log ?? NullMigrationLog.Instance;

        commandLineVariables = variableOverrides ?? new Dictionary<string, string>();
        if (environment.CommandTimeoutSeconds is { } timeout)
        {
            session.CommandTimeout = TimeSpan.FromSeconds(timeout);
        }

        session.Message += m => this.log.Detail(m);
    }

    private string Schema => environment.StateSchema;

    /// <summary>The meta key recording which environment owns the database.</summary>
    public const string EnvironmentMetaKey = "environment";

    /// <summary>
    /// Allow running against a database that was first deployed by a different environment name
    /// (e.g. a database restored from prod into test). The new name is recorded.
    /// </summary>
    public bool AllowEnvironmentMismatch { get; init; }

    public DeploymentEnvironment Environment => environment;

    /// <summary>How {name} and $(name) are expanded. Set from bsm.yaml 'preprocessor:'.</summary>
    public PreprocessorOptions Preprocessing { get; init; } = new();

    /// <summary>Built-in and user variables for this environment and run.</summary>
    public ScriptPreprocessor Preprocessor => preprocessor ??= CreatePreprocessor();

    /// <summary>Renders a script exactly as it would be sent to the database.</summary>
    public RenderResult Render(string name, string kind, string script, string? scriptHash = null) =>
        Preprocessor.Render(script, new ScriptContext(name, kind, scriptHash));

    /// <summary>The environment name recorded in the database on its first deploy, or null.</summary>
    public async Task<string?> GetRecordedEnvironmentAsync(CancellationToken cancellationToken = default) =>
        await provider.StateStore.ExistsAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            ? await provider.StateStore.GetMetaAsync(session, Schema, EnvironmentMetaKey, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Compares the environment's resources (e.g. Always Encrypted keys) with the database.</summary>
    public async Task<IReadOnlyList<ResourceAction>> PlanResourcesAsync(CancellationToken cancellationToken = default) =>
        provider.Resources is null ? [] : await provider.Resources.PlanAsync(session, environment, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Creates or updates per-environment resources. Destructive actions (e.g. dropping an old key value after
    /// rotation) only run with <paramref name="allowDestructive"/>.
    /// </summary>
    public async Task<IReadOnlyList<ResourceAction>> ProvisionAsync(bool dryRun, bool allowDestructive, CancellationToken cancellationToken = default)
    {
        await using var lockHandle = await AcquireLockAsync(dryRun, cancellationToken).ConfigureAwait(false);
        if (!dryRun)
        {
            await provider.StateStore.EnsureCreatedAsync(session, Schema, cancellationToken).ConfigureAwait(false);
            await ClaimEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ProvisionCoreAsync(dryRun, allowDestructive, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StatusReport> StatusAsync(CancellationToken cancellationToken = default)
    {
        var applied = await provider.StateStore.ExistsAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            ? await provider.StateStore.GetAppliedChangesAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            : [];
        return StatusReport.Build(project, applied);
    }

    /// <summary>Lints every change script in the project.</summary>
    public IReadOnlyList<(string Change, string Script, LintDiagnostic Diagnostic)> Lint()
    {
        var results = new List<(string, string, LintDiagnostic)>();
        foreach (var change in project.Changes)
        {
            foreach (var (kind, script) in ScriptsOf(change))
            {
                foreach (var diagnostic in provider.Linter.Lint(script, new LintContext(change.Name, kind, change.Plan.Flags)))
                {
                    results.Add((change.Name, kind, diagnostic));
                }
            }
        }

        return results;
    }

    public async Task<DeployResult> DeployAsync(DeployOptions options, CancellationToken cancellationToken = default)
    {
        EnsureProjectIsValid();

        var targetIndex = project.Changes.Count - 1;
        if (options.To is not null)
        {
            targetIndex = ResolveTarget(options.To);
        }

        await using var lockHandle = await AcquireLockAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        if (!options.DryRun)
        {
            await provider.StateStore.EnsureCreatedAsync(session, Schema, cancellationToken).ConfigureAwait(false);
            await ClaimEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CheckEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        }

        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        ThrowOnMismatch(status);

        var pending = options.OnlyRepeatables ? [] : status.Changes.Where(c => c.State == ChangeState.Pending && c.PlanIndex <= targetIndex).ToList();
        var maxAppliedIndex = status.Changes.Where(c => c.State != ChangeState.Pending).Select(c => c.PlanIndex).DefaultIfEmpty(-1).Max();
        var outOfOrder = pending.Where(p => p.PlanIndex < maxAppliedIndex).Select(p => p.Change.Name).ToHashSet(StringComparer.Ordinal);
        var ordering = options.Ordering ?? environment.Ordering;

        if (outOfOrder.Count > 0)
        {
            if (ordering == OrderingPolicy.Strict)
            {
                throw new MigrationException(
                    $"Changes {string.Join(", ", outOfOrder.Select(n => $"'{n}'"))} come before changes already deployed to this database. " +
                    "This profile uses strict ordering; revert the later changes first or deploy with ordering 'dependency'.");
            }

            log.Warn($"Deploying out of plan order (dependency ordering): {string.Join(", ", outOfOrder)}. This database will not be certified against the plan head.");
        }

        LintOrThrow(pending.Select(p => p.Change));
        await ProvisionCoreAsync(options.DryRun, allowDestructive: false, cancellationToken).ConfigureAwait(false);

        var deployed = new List<string>();
        var parentChainId = status.DatabaseHeadChainId;
        foreach (var item in pending)
        {
            var change = item.Change;
            if (options.DryRun)
            {
                log.Info($"would deploy {change.Name}{(outOfOrder.Contains(change.Name) ? " (out of order)" : string.Empty)}");
                deployed.Add(change.Name);
                continue;
            }

            var chainId = MigrationChange.ComputeChainId(provider.Id, change.Name, change.ScriptHash, parentChainId);
            await DeployChangeAsync(change, chainId, outOfOrder.Contains(change.Name), cancellationToken).ConfigureAwait(false);
            parentChainId = chainId;
            deployed.Add(change.Name);
        }

        var repeatables = options.SkipRepeatables
            ? []
            : await DeployRepeatablesAsync(options.DryRun, cancellationToken).ConfigureAwait(false);

        if (!options.DryRun && (deployed.Count > 0 || repeatables.Count > 0))
        {
            await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        return new DeployResult(deployed, repeatables, options.DryRun);
    }

    public async Task<RevertResult> RevertAsync(RevertOptions options, CancellationToken cancellationToken = default)
    {
        var selectors = (options.To is not null ? 1 : 0) + (options.Last is not null ? 1 : 0) + (options.All ? 1 : 0) + (options.Repeatables.Count > 0 ? 1 : 0);
        if (selectors != 1)
        {
            throw new MigrationException("Specify exactly one of --to <change|@tag>, --last <n>, --all or --repeatable <name>.");
        }

        if (!environment.AllowRevert)
        {
            throw new MigrationException($"Environment '{environment.Name}' does not allow revert (allowRevert: false). Deploy a new change that undoes the work instead.");
        }

        await using var lockHandle = await AcquireLockAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        await CheckEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (options.Repeatables.Count > 0)
        {
            return new RevertResult([], options.DryRun) { Repeatables = await RevertRepeatablesAsync(options.Repeatables, options.DryRun, cancellationToken).ConfigureAwait(false) };
        }

        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        var applied = status.Applied;

        IReadOnlyList<AppliedChange> toRevert;
        if (options.All)
        {
            toRevert = [.. applied.Reverse()];
        }
        else if (options.Last is { } last)
        {
            toRevert = [.. applied.Reverse().Take(last)];
        }
        else
        {
            var target = options.To!;
            var name = target.StartsWith('@') ? project.Plan.Changes.ElementAtOrDefault(project.Plan.ResolveTarget(target))?.Name : target;
            var keep = applied.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal))
                ?? throw new MigrationException($"'{target}' is not deployed to this database.");
            toRevert = [.. applied.Where(a => a.Sequence > keep.Sequence).Reverse()];
        }

        var irreversible = toRevert.Where(a => a.RevertScript is null).Select(a => a.Name).ToList();
        if (irreversible.Count > 0)
        {
            throw new MigrationException($"Cannot revert: {string.Join(", ", irreversible)} {(irreversible.Count == 1 ? "is" : "are")} irreversible (no revert script was deployed).");
        }

        var reverted = new List<string>();
        foreach (var row in toRevert)
        {
            if (options.DryRun)
            {
                log.Info($"would revert {row.Name}");
                reverted.Add(row.Name);
                continue;
            }

            var working = project.Find(row.Name);
            if (working?.RevertScript is not null && !string.Equals(working.RevertScript, row.RevertScript, StringComparison.Ordinal))
            {
                log.Warn($"{row.Name}: the working copy's {MigrationProject.RevertFile} differs from the one deployed; using the deployed script.");
            }

            if (row.RenderedRevertHash is not null && !options.AllowVariableDrift)
            {
                var now = RenderedHash(row.Name, row.ScriptHash, ("revert", row.RevertScript));
                if (now is not null && !string.Equals(now, row.RenderedRevertHash, StringComparison.Ordinal))
                {
                    throw new MigrationException($"{row.Name}: the revert script renders differently than when it was deployed (a variable it uses changed value). Check 'bsm vars', or pass --allow-variable-drift to revert anyway.");
                }
            }

            await RevertChangeAsync(row, cancellationToken).ConfigureAwait(false);
            reverted.Add(row.Name);
        }

        if (!options.DryRun && reverted.Count > 0)
        {
            await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RevertResult(reverted, options.DryRun);
    }

    /// <summary>Checks hashes of applied changes against the working copy and the live schema against the last snapshot.</summary>
    public async Task<VerifyResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        foreach (var mismatch in status.Mismatched)
        {
            problems.Add($"{mismatch.Change.Name}: deployed hash {Short(mismatch.Applied!.ScriptHash)} != working copy {Short(mismatch.Change.ScriptHash)}.");
        }

        foreach (var orphan in status.Orphans)
        {
            problems.Add($"{orphan.Name}: deployed but not in the plan.");
        }

        foreach (var row in status.Applied.Where(a => a.RenderedHash is not null))
        {
            try
            {
                var now = RenderedHash(row.Name, row.ScriptHash, ("deploy", row.DeployScript), ("verify", row.VerifyScript));
                if (now is not null && !string.Equals(now, row.RenderedHash, StringComparison.Ordinal))
                {
                    problems.Add($"{row.Name}: renders differently now than when deployed to this environment (variable values changed).");
                }
            }
            catch (MigrationException ex)
            {
                problems.Add($"{row.Name}: {ex.Message}");
            }
        }

        string? recorded = null;
        string? live = null;
        if (provider.Capabilities.SupportsSchemaFingerprint && status.Applied.Count > 0)
        {
            recorded = (await provider.StateStore.GetLatestSchemaSnapshotAsync(session, Schema, cancellationToken).ConfigureAwait(false))?.Fingerprint;
            var dump = await provider.Schema.CaptureAsync(session, Schema, cancellationToken).ConfigureAwait(false);
            live = ContentHasher.Hash(dump);
            if (recorded is not null && !string.Equals(recorded, live, StringComparison.Ordinal))
            {
                if (await RecordedWithOtherSchemaFormatAsync(cancellationToken).ConfigureAwait(false))
                {
                    log.Warn("the recorded schema fingerprint was taken by an older version of bsm whose fingerprint format differs, so drift cannot be checked; the next deploy records a new one.");
                }
                else
                {
                    problems.Add($"schema drift: live fingerprint {Short(live)} != recorded {Short(recorded)}. Something changed the schema outside bsm.");
                }
            }
        }

        foreach (var row in status.Applied.Where(a => a.VerifyScript is not null))
        {
            try
            {
                await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await RunScriptAsync(row.Name, "verify", row.VerifyScript!, cancellationToken).ConfigureAwait(false);
            }
            catch (MigrationException ex)
            {
                problems.Add(ex.Message);
            }
            finally
            {
                if (session.InTransaction)
                {
                    await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new VerifyResult(status, recorded, live, problems);
    }

    /// <summary>
    /// Accepts edits to deployed changes without running anything: records the working copy's scripts, hashes and
    /// flags for the named changes and re-chains every later change, so the database certifies against the plan again.
    /// For fixing a revert or verify script, or a comment or whitespace edit to a deploy script, after deploy. The
    /// database itself is not touched, so a repaired deploy.sql must describe what the database already has.
    /// </summary>
    public async Task<RepairResult> RepairAsync(RepairOptions options, CancellationToken cancellationToken = default)
    {
        if ((options.Names.Count > 0) == options.All)
        {
            throw new MigrationException("Name the changes to repair, or pass --all to repair every edited change.");
        }

        await using var lockHandle = await AcquireLockAsync(options.DryRun, cancellationToken).ConfigureAwait(false);
        await CheckEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        var byName = status.Changes.ToDictionary(c => c.Change.Name, StringComparer.Ordinal);

        var targets = new List<ChangeStatus>();
        if (options.All)
        {
            targets.AddRange(status.Mismatched);
        }
        else
        {
            foreach (var name in options.Names.Distinct(StringComparer.Ordinal))
            {
                if (!byName.TryGetValue(name, out var item))
                {
                    throw new MigrationException(status.Orphans.Any(o => o.Name == name)
                        ? $"'{name}' is deployed but no longer in the plan; there is no working copy to repair it from."
                        : $"'{name}' is not a change in the plan.");
                }

                if (item.State == ChangeState.Pending)
                {
                    throw new MigrationException($"'{name}' is not deployed to this database; there is nothing to repair.");
                }

                if (item.State == ChangeState.Applied)
                {
                    log.Info($"{name} already matches the working copy.");
                    continue;
                }

                targets.Add(item);
            }
        }

        var repaired = targets.Select(t => new RepairedChange(t.Change.Name, Differences(t.Change, t.Applied!))).ToList();
        if (options.DryRun || repaired.Count == 0)
        {
            return new RepairResult(repaired, options.DryRun);
        }

        var working = targets.ToDictionary(t => t.Change.Name, t => t.Change, StringComparer.Ordinal);
        await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? parent = null;
            foreach (var row in status.Applied)
            {
                var updated = row;
                if (working.TryGetValue(row.Name, out var change))
                {
                    var fresh = NewApplied(change, row.ChainId, row.DurationMs, row.AppliedOutOfOrder);
                    updated = row with
                    {
                        ScriptHash = fresh.ScriptHash,
                        DeployScript = fresh.DeployScript,
                        RevertScript = fresh.RevertScript,
                        VerifyScript = fresh.VerifyScript,
                        Flags = fresh.Flags,
                        RenderedHash = fresh.RenderedHash,
                        RenderedRevertHash = fresh.RenderedRevertHash,
                    };
                }

                updated = updated with { ChainId = MigrationChange.ComputeChainId(provider.Id, updated.Name, updated.ScriptHash, parent) };
                parent = updated.ChainId;
                if (!updated.Equals(row))
                {
                    await provider.StateStore.UpdateChangeAsync(session, Schema, updated, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var item in repaired)
            {
                var message = $"repaired {string.Join(", ", item.Scripts)} (was {Short(byName[item.Name].Applied!.ScriptHash)})" + (options.Reason is null ? string.Empty : $": {options.Reason}");
                await AppendEventAsync(MigrationEventKind.Repair, item.Name, working[item.Name].ScriptHash, null, message, cancellationToken).ConfigureAwait(false);
                log.Success($"repaired {item.Name} ({string.Join(", ", item.Scripts)})");
            }

            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (session.InTransaction)
            {
                await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        return new RepairResult(repaired, options.DryRun);

        static IReadOnlyList<string> Differences(MigrationChange change, AppliedChange row)
        {
            var parts = new List<string>();
            if (!string.Equals(change.DeployScript, row.DeployScript, StringComparison.Ordinal))
            {
                parts.Add(MigrationProject.DeployFile);
            }

            if (!string.Equals(change.RevertScript, row.RevertScript, StringComparison.Ordinal))
            {
                parts.Add(MigrationProject.RevertFile);
            }

            if (!string.Equals(change.VerifyScript, row.VerifyScript, StringComparison.Ordinal))
            {
                parts.Add(MigrationProject.VerifyFile);
            }

            if (parts.Count == 0 || (int)change.Plan.Flags != row.Flags)
            {
                parts.Add("plan line");
            }

            return parts;
        }
    }

    /// <summary>
    /// Records a change as applied without running it: for a change generated from this database's own schema
    /// ('bsm add --from-diff'), which the database therefore already has. It must be the next pending change.
    /// </summary>
    public async Task RecordAppliedAsync(string name, string reason, CancellationToken cancellationToken = default)
    {
        EnsureProjectIsValid();
        await using var lockHandle = await AcquireLockAsync(false, cancellationToken).ConfigureAwait(false);
        await provider.StateStore.EnsureCreatedAsync(session, Schema, cancellationToken).ConfigureAwait(false);
        await ClaimEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        ThrowOnMismatch(status);
        var next = status.Pending.FirstOrDefault();
        if (next is null || !string.Equals(next.Change.Name, name, StringComparison.Ordinal))
        {
            throw new MigrationException($"'{name}' is not the next pending change in this database; deploy the changes before it first.");
        }

        var chainId = MigrationChange.ComputeChainId(provider.Id, next.Change.Name, next.Change.ScriptHash, status.DatabaseHeadChainId);
        await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await provider.StateStore.InsertChangeAsync(session, Schema, NewApplied(next.Change, chainId, 0, false) with { Baselined = true }, cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(MigrationEventKind.Baseline, name, next.Change.ScriptHash, chainId, reason, cancellationToken).ConfigureAwait(false);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The live schema dump (for 'bsm diff-schema').</summary>
    public Task<string> CaptureSchemaAsync(CancellationToken cancellationToken = default) =>
        provider.Capabilities.SupportsSchemaFingerprint
            ? provider.Schema.CaptureAsync(session, Schema, cancellationToken)
            : throw new MigrationException($"{provider.DisplayName} does not support schema fingerprints.");

    /// <summary>The meta key recording <see cref="ISchemaInspector.FormatVersion"/> of the last snapshot (absent = 1).</summary>
    public const string SchemaFormatMetaKey = "schema_format";

    /// <summary>True when the last snapshot was recorded with a different schema dump format than this provider writes.</summary>
    public async Task<bool> RecordedWithOtherSchemaFormatAsync(CancellationToken cancellationToken = default)
    {
        var recorded = await provider.StateStore.GetMetaAsync(session, Schema, SchemaFormatMetaKey, cancellationToken).ConfigureAwait(false) ?? "1";
        return recorded != provider.Schema.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The schema as recorded after the last deploy or revert, or null.</summary>
    public async Task<SchemaSnapshot?> GetRecordedSchemaAsync(CancellationToken cancellationToken = default) =>
        await provider.StateStore.ExistsAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            ? await provider.StateStore.GetLatestSchemaSnapshotAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Records changes as applied without running them, for adopting an existing database.</summary>
    public async Task<IReadOnlyList<string>> BaselineAsync(string to, CancellationToken cancellationToken = default)
    {
        EnsureProjectIsValid();
        var targetIndex = ResolveTarget(to);
        await using var lockHandle = await AcquireLockAsync(false, cancellationToken).ConfigureAwait(false);
        await provider.StateStore.EnsureCreatedAsync(session, Schema, cancellationToken).ConfigureAwait(false);
        await ClaimEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Applied.Count > 0)
        {
            throw new MigrationException("Baseline is only allowed on a database with no deployed changes.");
        }

        string? parent = null;
        var names = new List<string>();
        foreach (var change in project.Changes.Take(targetIndex + 1))
        {
            var chainId = MigrationChange.ComputeChainId(provider.Id, change.Name, change.ScriptHash, parent);
            await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await provider.StateStore.InsertChangeAsync(session, Schema, NewApplied(change, chainId, 0, false) with { Baselined = true }, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(MigrationEventKind.Baseline, change.Name, change.ScriptHash, chainId, null, cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            parent = chainId;
            names.Add(change.Name);
        }

        await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return names;
    }

    /// <summary>
    /// Records repeatables as applied in their current form without running them, for repeatables another tool
    /// already applied with exactly this text ('bsm baseline --from').
    /// </summary>
    public async Task<IReadOnlyList<string>> BaselineRepeatablesAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken = default)
    {
        var scripts = project.Repeatables.Where(r => names.Contains(r.Name)).ToList();
        if (scripts.Count == 0)
        {
            return [];
        }

        await using var lockHandle = await AcquireLockAsync(false, cancellationToken).ConfigureAwait(false);
        await provider.StateStore.EnsureCreatedAsync(session, Schema, cancellationToken).ConfigureAwait(false);
        await ClaimEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var existing = (await provider.StateStore.GetRepeatablesAsync(session, Schema, cancellationToken).ConfigureAwait(false)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var recorded = new List<string>();
        foreach (var script in scripts.Where(s => !existing.Contains(s.Name)))
        {
            await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await provider.StateStore.UpsertRepeatableAsync(session, Schema, new AppliedRepeatable
            {
                Name = script.Name,
                Hash = script.Hash,
                Script = script.Script,
                AppliedAtUtc = DateTimeOffset.UtcNow,
                AppliedBy = actor.User,
            }, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(MigrationEventKind.Baseline, script.Name, script.Hash, null, "repeatable", cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            recorded.Add(script.Name);
        }

        return recorded;
    }

    public async Task<IReadOnlyList<MigrationEvent>> LogAsync(int limit, CancellationToken cancellationToken = default) =>
        await provider.StateStore.ExistsAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            ? await provider.StateStore.GetEventsAsync(session, Schema, limit, cancellationToken).ConfigureAwait(false)
            : [];

    /// <summary>Produces a reviewable script of pending changes (variables substituted). The journal is not updated by it.</summary>
    public async Task<string> GenerateScriptAsync(string? to, CancellationToken cancellationToken = default)
    {
        EnsureProjectIsValid();
        var targetIndex = to is null ? project.Changes.Count - 1 : ResolveTarget(to);
        var status = await StatusAsync(cancellationToken).ConfigureAwait(false);
        ThrowOnMismatch(status);

        var sb = new StringBuilder();
        sb.AppendLine(provider.FormatComment($"Badger SQL Migrate {actor.ToolVersion} - generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z for {session.Description}"));
        sb.AppendLine(provider.FormatComment("Review script only: running it does not record changes in the bsm journal. Use 'bsm deploy' or 'bsm baseline' afterwards."));
        foreach (var item in status.Changes.Where(c => c.State == ChangeState.Pending && c.PlanIndex <= targetIndex))
        {
            sb.AppendLine();
            sb.AppendLine(provider.FormatComment($"---- {item.Change.Name}  hash {item.Change.ScriptHash}"));
            foreach (var batch in provider.Parser.Split(Render(item.Change.Name, "deploy", item.Change.DeployScript, item.Change.ScriptHash).Text))
            {
                sb.AppendLine(batch.Text.TrimEnd());
                sb.AppendLine(provider.FormatBatchSeparator());
            }
        }

        return sb.ToString();
    }

    private async Task DeployChangeAsync(MigrationChange change, string chainId, bool outOfOrder, CancellationToken cancellationToken)
    {
        var useTransaction = provider.Capabilities.TransactionalDdl && !change.Plan.Flags.HasFlag(ChangeFlags.NoTransaction);
        var stopwatch = Stopwatch.StartNew();
        log.Info($"deploying {change.Name}");
        try
        {
            if (useTransaction)
            {
                await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            await RunScriptAsync(change.Name, "deploy", change.DeployScript, cancellationToken).ConfigureAwait(false);
            if (change.VerifyScript is not null)
            {
                await RunScriptAsync(change.Name, "verify", change.VerifyScript, cancellationToken).ConfigureAwait(false);
            }

            if (!useTransaction)
            {
                await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            await provider.StateStore.InsertChangeAsync(session, Schema, NewApplied(change, chainId, stopwatch.ElapsedMilliseconds, outOfOrder), cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(MigrationEventKind.Deploy, change.Name, change.ScriptHash, chainId, outOfOrder ? "out of order" : null, cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(change.Name, change.ScriptHash, ex, useTransaction, cancellationToken).ConfigureAwait(false);
            throw;
        }

        log.Success($"deployed {change.Name} ({stopwatch.ElapsedMilliseconds} ms)");
    }

    private async Task RevertChangeAsync(AppliedChange row, CancellationToken cancellationToken)
    {
        var useTransaction = provider.Capabilities.TransactionalDdl && !((ChangeFlags)row.Flags).HasFlag(ChangeFlags.NoTransaction);
        log.Info($"reverting {row.Name}");
        try
        {
            if (useTransaction)
            {
                await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            await RunScriptAsync(row.Name, "revert", row.RevertScript!, cancellationToken).ConfigureAwait(false);
            if (!useTransaction)
            {
                await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            }

            await provider.StateStore.DeleteChangeAsync(session, Schema, row.Name, cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(MigrationEventKind.Revert, row.Name, row.ScriptHash, row.ChainId, null, cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAsync(row.Name, row.ScriptHash, ex, useTransaction, cancellationToken).ConfigureAwait(false);
            throw;
        }

        log.Success($"reverted {row.Name}");
    }

    private async Task<IReadOnlyList<string>> DeployRepeatablesAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (project.Repeatables.Count == 0)
        {
            return [];
        }

        var existing = dryRun && !await provider.StateStore.ExistsAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            ? new Dictionary<string, AppliedRepeatable>()
            : (await provider.StateStore.GetRepeatablesAsync(session, Schema, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.Name, StringComparer.Ordinal);

        var applied = new List<string>();
        foreach (var script in project.Repeatables)
        {
            existing.TryGetValue(script.Name, out var previous);
            if (previous is not null && string.Equals(previous.Hash, script.Hash, StringComparison.Ordinal))
            {
                continue;
            }

            if (dryRun)
            {
                log.Info($"would apply repeatable {script.Name}");
                applied.Add(script.Name);
                continue;
            }

            log.Info($"applying repeatable {script.Name}");
            try
            {
                await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await RunScriptAsync(script.Name, "repeatable", script.Script, cancellationToken).ConfigureAwait(false);
                await provider.StateStore.UpsertRepeatableAsync(session, Schema, new AppliedRepeatable
                {
                    Name = script.Name,
                    Hash = script.Hash,
                    Script = script.Script,
                    PreviousHash = previous?.Hash,
                    PreviousScript = previous?.Script,
                    AppliedAtUtc = DateTimeOffset.UtcNow,
                    AppliedBy = actor.User,
                }, cancellationToken).ConfigureAwait(false);
                await AppendEventAsync(MigrationEventKind.Repeatable, script.Name, script.Hash, null, null, cancellationToken).ConfigureAwait(false);
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailAsync(script.Name, script.Hash, ex, true, cancellationToken).ConfigureAwait(false);
                throw;
            }

            applied.Add(script.Name);
        }

        return applied;
    }

    /// <summary>
    /// Re-applies each repeatable's previous definition (stored at its last apply) and records it as current, so
    /// the next deploy re-applies the working copy unless that is changed too.
    /// </summary>
    private async Task<IReadOnlyList<string>> RevertRepeatablesAsync(IReadOnlyList<string> names, bool dryRun, CancellationToken cancellationToken)
    {
        var stored = await provider.StateStore.ExistsAsync(session, Schema, cancellationToken).ConfigureAwait(false)
            ? (await provider.StateStore.GetRepeatablesAsync(session, Schema, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.Name, StringComparer.Ordinal)
            : [];

        var rows = new List<AppliedRepeatable>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (!stored.TryGetValue(name, out var row))
            {
                throw new MigrationException($"Repeatable '{name}' has not been applied to this database (names look like views/vCustomer.sql or @dev/seed.sql).");
            }

            if (row.PreviousScript is null)
            {
                throw new MigrationException($"Repeatable '{name}' has only been applied once, so there is no earlier definition to restore. Remove the object with a new change instead.");
            }

            rows.Add(row);
        }

        var restored = new List<string>();
        foreach (var row in rows)
        {
            if (dryRun)
            {
                log.Info($"would restore repeatable {row.Name} to {Short(row.PreviousHash!)}");
                restored.Add(row.Name);
                continue;
            }

            log.Info($"restoring repeatable {row.Name}");
            try
            {
                await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await RunScriptAsync(row.Name, "repeatable", row.PreviousScript!, cancellationToken).ConfigureAwait(false);
                await provider.StateStore.UpsertRepeatableAsync(session, Schema, row with
                {
                    Hash = row.PreviousHash!,
                    Script = row.PreviousScript!,
                    PreviousHash = row.Hash,
                    PreviousScript = row.Script,
                    AppliedAtUtc = DateTimeOffset.UtcNow,
                    AppliedBy = actor.User,
                }, cancellationToken).ConfigureAwait(false);
                await AppendEventAsync(MigrationEventKind.Revert, row.Name, row.PreviousHash, null, $"repeatable restored from {Short(row.Hash)}", cancellationToken).ConfigureAwait(false);
                await session.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await FailAsync(row.Name, row.PreviousHash, ex, true, cancellationToken).ConfigureAwait(false);
                throw;
            }

            log.Success($"restored repeatable {row.Name}");
            restored.Add(row.Name);
        }

        if (!dryRun && restored.Count > 0)
        {
            await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }

        return restored;
    }

    private async Task RunScriptAsync(string name, string kind, string script, CancellationToken cancellationToken)
    {
        var substituted = Render(name, kind, script, project.Find(name)?.ScriptHash).Text;
        foreach (var batch in provider.Parser.Split(substituted))
        {
            for (var i = 0; i < batch.RepeatCount; i++)
            {
                try
                {
                    await session.ExecuteAsync(batch.Text, null, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not MigrationException)
                {
                    throw new MigrationException($"{name} ({kind}) failed in the batch starting at line {batch.StartLine}: {ex.Message}", ex);
                }
            }
        }
    }

    private async Task FailAsync(string name, string? hash, Exception ex, bool wasTransactional, CancellationToken cancellationToken)
    {
        if (session.InTransaction)
        {
            await session.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!wasTransactional)
        {
            log.Warn($"{name} ran without a transaction; the database may be partially changed. Fix the script so it can be re-run safely.");
        }

        try
        {
            await AppendEventAsync(MigrationEventKind.Fail, name, hash, null, ex.Message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            log.Warn($"could not record the failure event: {logEx.Message}");
        }
    }

    private async Task CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!provider.Capabilities.SupportsSchemaFingerprint)
        {
            return;
        }

        var dump = await provider.Schema.CaptureAsync(session, Schema, cancellationToken).ConfigureAwait(false);
        var applied = await provider.StateStore.GetAppliedChangesAsync(session, Schema, cancellationToken).ConfigureAwait(false);
        var snapshot = new SchemaSnapshot(ContentHasher.Hash(dump), dump, DateTimeOffset.UtcNow, applied.Count == 0 ? null : applied[^1].ChainId);
        await provider.StateStore.InsertSchemaSnapshotAsync(session, Schema, snapshot, cancellationToken).ConfigureAwait(false);
        await provider.StateStore.SetMetaAsync(session, Schema, SchemaFormatMetaKey, provider.Schema.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        log.Detail($"schema fingerprint {Short(snapshot.Fingerprint)}");
    }

    private async Task<IReadOnlyList<ResourceAction>> ProvisionCoreAsync(bool dryRun, bool allowDestructive, CancellationToken cancellationToken)
    {
        if (provider.Resources is null)
        {
            return [];
        }

        var plan = await provider.Resources.PlanAsync(session, environment, cancellationToken).ConfigureAwait(false);
        var conflicts = plan.Where(a => a.Kind == ResourceActionKind.Conflict).ToList();
        if (conflicts.Count > 0)
        {
            throw new MigrationException($"Environment '{environment.Name}' resources conflict with the database:\n  " + string.Join("\n  ", conflicts.Select(c => $"{c.ResourceType} {c.Name}: {c.Description}")));
        }

        var toApply = plan.Where(a => a.Kind == ResourceActionKind.Apply || (allowDestructive && a.Kind == ResourceActionKind.Destructive)).ToList();
        foreach (var skipped in plan.Where(a => a.Kind == ResourceActionKind.Destructive && !allowDestructive))
        {
            log.Warn($"{skipped.ResourceType} {skipped.Name}: {skipped.Description} (run 'bsm env provision --allow-destructive' when every client can use the new key)");
        }

        foreach (var info in plan.Where(a => a.Kind == ResourceActionKind.Info))
        {
            log.Detail($"{info.ResourceType} {info.Name}: {info.Description}");
        }

        if (toApply.Count == 0)
        {
            return [];
        }

        if (dryRun)
        {
            foreach (var action in toApply)
            {
                log.Info($"would {action.Description} ({action.ResourceType} {action.Name})");
            }

            return toApply;
        }

        foreach (var action in toApply)
        {
            log.Info($"{action.Description} ({action.ResourceType} {action.Name})");
            try
            {
                await provider.Resources.ApplyAsync(session, environment, [action], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not MigrationException)
            {
                throw new MigrationException($"{action.ResourceType} {action.Name}: {ex.Message}", ex);
            }

            await AppendEventAsync(MigrationEventKind.Resource, $"{action.ResourceType}:{action.Name}", null, null, action.Description, cancellationToken).ConfigureAwait(false);
        }

        return toApply;
    }

    /// <summary>Records the environment name on first use; refuses a database that belongs to another environment.</summary>
    private async Task ClaimEnvironmentAsync(CancellationToken cancellationToken)
    {
        var recorded = await provider.StateStore.GetMetaAsync(session, Schema, EnvironmentMetaKey, cancellationToken).ConfigureAwait(false);
        if (recorded is null || (AllowEnvironmentMismatch && !string.Equals(recorded, environment.Name, StringComparison.Ordinal)))
        {
            if (recorded is not null)
            {
                log.Warn($"database was recorded as environment '{recorded}'; re-recording it as '{environment.Name}'.");
            }

            await provider.StateStore.SetMetaAsync(session, Schema, EnvironmentMetaKey, environment.Name, cancellationToken).ConfigureAwait(false);
            return;
        }

        ThrowOnEnvironmentMismatch(recorded);
    }

    private async Task CheckEnvironmentAsync(CancellationToken cancellationToken)
    {
        if (!AllowEnvironmentMismatch)
        {
            ThrowOnEnvironmentMismatch(await GetRecordedEnvironmentAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private void ThrowOnEnvironmentMismatch(string? recorded)
    {
        if (recorded is not null && !string.Equals(recorded, environment.Name, StringComparison.Ordinal))
        {
            throw new MigrationException(
                $"This database belongs to environment '{recorded}', but you are running as '{environment.Name}'. " +
                "Check --env and the connection settings. If the database was intentionally copied (e.g. prod restored into test), pass --claim-environment.");
        }
    }

    private async Task<IAsyncDisposable> AcquireLockAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (dryRun || !provider.Capabilities.SupportsDeployLock)
        {
            return NoopDisposable.Instance;
        }

        return await provider.Lock.AcquireAsync(session, Schema, TimeSpan.FromSeconds(environment.LockTimeoutSeconds), cancellationToken).ConfigureAwait(false);
    }

    private Task AppendEventAsync(MigrationEventKind kind, string name, string? hash, string? chainId, string? message, CancellationToken cancellationToken) =>
        provider.StateStore.AppendEventAsync(session, Schema, new MigrationEvent
        {
            Kind = kind,
            Name = name,
            ScriptHash = hash,
            ChainId = chainId,
            Message = message,
            AtUtc = DateTimeOffset.UtcNow,
            Actor = actor.User,
            Host = actor.Host,
            ToolVersion = actor.ToolVersion,
            Environment = environment.Name,
        }, cancellationToken);

    private AppliedChange NewApplied(MigrationChange change, string chainId, long durationMs, bool outOfOrder) => new()
    {
        RenderedHash = RenderedHash(change.Name, change.ScriptHash, ("deploy", change.DeployScript), ("verify", change.VerifyScript)),
        RenderedRevertHash = RenderedHash(change.Name, change.ScriptHash, ("revert", change.RevertScript)),
        Name = change.Name,
        ScriptHash = change.ScriptHash,
        ChainId = chainId,
        DeployScript = change.DeployScript,
        RevertScript = change.RevertScript,
        VerifyScript = change.VerifyScript,
        Flags = (int)change.Plan.Flags,
        DeployedAtUtc = DateTimeOffset.UtcNow,
        DeployedBy = actor.User,
        Host = actor.Host,
        ToolVersion = actor.ToolVersion,
        DurationMs = durationMs,
        AppliedOutOfOrder = outOfOrder,
    };

    /// <summary>Hash of the SQL as rendered for this environment; null when a per-run variable (deploy.*) makes it unrepeatable.</summary>
    private string? RenderedHash(string name, string scriptHash, params (string Kind, string? Script)[] scripts)
    {
        var parts = new List<(string, string?)>();
        foreach (var (kind, script) in scripts)
        {
            if (script is null)
            {
                parts.Add((kind, null));
                continue;
            }

            var rendered = Render(name, kind, script, scriptHash);
            if (rendered.UsesVolatile)
            {
                return null;
            }

            parts.Add((kind, rendered.Text));
        }

        return ContentHasher.HashParts([.. parts]);
    }

    private ScriptPreprocessor CreatePreprocessor()
    {
        var endpoint = SafeEndpoint();
        var builtIns = ScriptPreprocessor.BuiltIns(
            environment.Name,
            environment.Description,
            provider.ResolveDatabaseName(environment),
            endpoint?.Host,
            endpoint?.Port,
            provider.Id,
            actor.ToolVersion,
            environment.StateSchema,
            project.HeadChainId,
            actor.User,
            actor.Host,
            runStartedUtc,
            runId);
        var user = environment.Variables.Select(kv => new VariableInfo(kv.Key, kv.Value, VariableSource.Config))
            .Where(v => !commandLineVariables.ContainsKey(v.Name))
            .Concat(commandLineVariables.Select(kv => new VariableInfo(kv.Key, kv.Value, VariableSource.CommandLine)));
        return new ScriptPreprocessor(builtIns, user, Preprocessing);
    }

    private Endpoint? SafeEndpoint()
    {
        try
        {
            return environment.Host is null && environment.ConnectionString is null ? null : provider.ResolveEndpoint(environment);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void LintOrThrow(IEnumerable<MigrationChange> changes)
    {
        var errors = new List<string>();
        foreach (var change in changes)
        {
            foreach (var (kind, raw) in ScriptsOf(change))
            {
                var script = Render(change.Name, kind, raw, change.ScriptHash).Text;
                foreach (var d in provider.Linter.Lint(script, new LintContext(change.Name, kind, change.Plan.Flags)))
                {
                    var text = $"{change.Name}/{kind}.sql line {d.Line}: {d.Message} [{d.Code}]";
                    if (d.Severity == LintSeverity.Error)
                    {
                        errors.Add(text);
                    }
                    else if (d.Severity == LintSeverity.Warning)
                    {
                        log.Warn(text);
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new MigrationException("Lint errors:\n  " + string.Join("\n  ", errors));
        }
    }

    private static IEnumerable<(string Kind, string Script)> ScriptsOf(MigrationChange change)
    {
        yield return ("deploy", change.DeployScript);
        if (change.RevertScript is not null)
        {
            yield return ("revert", change.RevertScript);
        }

        if (change.VerifyScript is not null)
        {
            yield return ("verify", change.VerifyScript);
        }
    }

    private void EnsureProjectIsValid()
    {
        if (project.Problems.Count > 0)
        {
            throw new MigrationException("The project has problems; fix them before deploying:\n  " + string.Join("\n  ", project.Problems));
        }
    }

    private static void ThrowOnMismatch(StatusReport status)
    {
        var mismatched = status.Mismatched.ToList();
        if (mismatched.Count > 0)
        {
            throw new MigrationException(
                "These changes were edited after they were deployed (hash mismatch): " +
                string.Join(", ", mismatched.Select(m => m.Change.Name)) +
                ". Deployed changes are immutable: put the edit in a new change ('bsm rework <change>'), " +
                "or if the edit is harmless (a revert or verify fix, a comment), accept it with 'bsm repair <change>'.");
        }
    }

    private int ResolveTarget(string target)
    {
        var index = project.Plan.ResolveTarget(target);
        if (index < 0)
        {
            throw new MigrationException($"'{target}' is not a change or tag in the plan.");
        }

        var name = project.Plan.Changes[index].Name;
        return project.Changes.Select((c, i) => (c, i)).First(x => x.c.Name == name).i;
    }

    private static string Short(string hash) => hash.Length > 12 ? hash[..12] : hash;

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
