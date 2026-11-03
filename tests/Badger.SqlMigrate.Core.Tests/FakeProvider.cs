using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Tests;

/// <summary>
/// In-memory provider used to prove the engine is vendor-agnostic. "Executing" SQL understands a tiny
/// language: "CREATE x" / "DROP x" add and remove objects, anything containing "FAIL" throws.
/// Transactions snapshot and restore all state.
/// </summary>
public sealed class FakeDatabase
{
    public HashSet<string> Objects { get; set; } = new(StringComparer.Ordinal);

    public List<AppliedChange> Changes { get; set; } = [];

    public List<AppliedRepeatable> Repeatables { get; set; } = [];

    public List<MigrationEvent> Events { get; set; } = [];

    public List<SchemaSnapshot> Snapshots { get; set; } = [];

    public List<string> Executed { get; } = [];

    public bool StateCreated { get; set; }

    public Dictionary<string, string> Meta { get; set; } = new(StringComparer.Ordinal);

    public long NextSequence { get; set; } = 1;

    public FakeDatabase Clone() => new()
    {
        Objects = [.. Objects],
        Changes = [.. Changes],
        Repeatables = [.. Repeatables],
        Events = [.. Events],
        Snapshots = [.. Snapshots],
        StateCreated = StateCreated,
        NextSequence = NextSequence,
        Meta = new(Meta, StringComparer.Ordinal),
    };

    public void RestoreFrom(FakeDatabase other)
    {
        Objects = other.Objects;
        Changes = other.Changes;
        Repeatables = other.Repeatables;
        Events = other.Events;
        Snapshots = other.Snapshots;
        StateCreated = other.StateCreated;
        NextSequence = other.NextSequence;
        Meta = other.Meta;
    }
}

public sealed class FakeSession(FakeDatabase db) : IDatabaseSession
{
    private FakeDatabase? snapshot;

    public event Action<string>? Message;

    public FakeDatabase Db => db;

    public string Description => "fake://memory";

    public bool InTransaction => snapshot is not null;

    public TimeSpan? CommandTimeout { get; set; }

    public Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (snapshot is not null)
        {
            throw new InvalidOperationException("nested transaction");
        }

        snapshot = db.Clone();
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        snapshot = snapshot is null ? throw new InvalidOperationException("no transaction") : null;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (snapshot is not null)
        {
            db.RestoreFrom(snapshot);
            snapshot = null;
        }

        return Task.CompletedTask;
    }

    public Task ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        db.Executed.Add(sql);
        foreach (var line in sql.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Contains("FAIL", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated failure");
            }

            if (line.StartsWith("CREATE ", StringComparison.Ordinal))
            {
                if (!db.Objects.Add(line[7..]))
                {
                    throw new InvalidOperationException($"object {line[7..]} already exists");
                }
            }
            else if (line.StartsWith("DROP ", StringComparison.Ordinal))
            {
                if (!db.Objects.Remove(line[5..]))
                {
                    throw new InvalidOperationException($"object {line[5..]} does not exist");
                }
            }
            else if (line.StartsWith("PRINT ", StringComparison.Ordinal))
            {
                Message?.Invoke(line[6..]);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakeProvider : IDatabaseProvider, IStateStore, IDeployLock, ISchemaInspector, IScriptParser, IScriptLinter
{
    public string Id => "fake";

    public string DisplayName => "Fake";

    public bool TransactionalDdl { get; init; } = true;

    public int LocksTaken { get; private set; }

    public ProviderCapabilities Capabilities => new() { TransactionalDdl = TransactionalDdl, SupportsDeployLock = true, SupportsSchemaFingerprint = true };

    public IScriptParser Parser => this;

    public IScriptLinter Linter => this;

    public IStateStore StateStore => this;

    public IDeployLock Lock => this;

    public ISchemaInspector Schema => this;

    public string? ResolveDatabaseName(DeploymentEnvironment environment) => environment.Database ?? "memorydb";

    /// <summary>Optional fake resources: each name in the set is "created" in FakeDatabase.Objects as RESOURCE:name.</summary>
    public FakeResources? FakeResources { get; init; }

    public IEnvironmentResources? Resources => FakeResources;

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) => [];

    public Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Endpoint ResolveEndpoint(DeploymentEnvironment environment) => new("memory", 0);

    public Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([]);

    public string FormatBatchSeparator() => ";";

    public string FormatComment(string text) => "-- " + text;

    // IScriptParser: batches separated by lines containing only ";".
    public IReadOnlyList<ScriptBatch> Split(string script) =>
        script.Split("\n;\n").Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => new ScriptBatch(b, 1)).ToList();

    // IScriptLinter: "LINT-ERROR" in a script is an error.
    public IReadOnlyList<LintDiagnostic> Lint(string script, LintContext context) =>
        script.Contains("LINT-ERROR", StringComparison.Ordinal) ? [new LintDiagnostic(LintSeverity.Error, "lint error", 1, "FAKE001")] : [];

    // IDeployLock
    public Task<IAsyncDisposable> AcquireAsync(IDatabaseSession session, string stateSchema, TimeSpan timeout, CancellationToken cancellationToken)
    {
        LocksTaken++;
        return Task.FromResult<IAsyncDisposable>(new Releaser());
    }

    // ISchemaInspector
    public Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken) =>
        Task.FromResult(string.Join('\n', Db(session).Objects.Order(StringComparer.Ordinal)));

    // IStateStore
    public Task EnsureCreatedAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        Db(session).StateCreated = true;
        return Task.CompletedTask;
    }

    public Task<string?> GetMetaAsync(IDatabaseSession session, string schema, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Db(session).Meta.TryGetValue(key, out var v) ? v : null);

    public Task SetMetaAsync(IDatabaseSession session, string schema, string key, string value, CancellationToken cancellationToken)
    {
        Db(session).Meta[key] = value;
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken) => Task.FromResult(Db(session).StateCreated);

    public Task<IReadOnlyList<AppliedChange>> GetAppliedChangesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AppliedChange>>(Db(session).Changes.OrderBy(c => c.Sequence).ToList());

    public Task InsertChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken)
    {
        var db = Db(session);
        db.Changes.Add(change with { Sequence = db.NextSequence++ });
        return Task.CompletedTask;
    }

    public Task DeleteChangeAsync(IDatabaseSession session, string schema, string name, CancellationToken cancellationToken)
    {
        Db(session).Changes.RemoveAll(c => c.Name == name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AppliedRepeatable>> GetRepeatablesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AppliedRepeatable>>(Db(session).Repeatables.ToList());

    public Task UpsertRepeatableAsync(IDatabaseSession session, string schema, AppliedRepeatable repeatable, CancellationToken cancellationToken)
    {
        var db = Db(session);
        db.Repeatables.RemoveAll(r => r.Name == repeatable.Name);
        db.Repeatables.Add(repeatable);
        return Task.CompletedTask;
    }

    public Task AppendEventAsync(IDatabaseSession session, string schema, MigrationEvent migrationEvent, CancellationToken cancellationToken)
    {
        Db(session).Events.Add(migrationEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MigrationEvent>> GetEventsAsync(IDatabaseSession session, string schema, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MigrationEvent>>(Db(session).Events.AsEnumerable().Reverse().Take(limit).ToList());

    public Task InsertSchemaSnapshotAsync(IDatabaseSession session, string schema, SchemaSnapshot snapshot, CancellationToken cancellationToken)
    {
        Db(session).Snapshots.Add(snapshot);
        return Task.CompletedTask;
    }

    public Task<SchemaSnapshot?> GetLatestSchemaSnapshotAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken) =>
        Task.FromResult(Db(session).Snapshots.LastOrDefault());

    private static FakeDatabase Db(IDatabaseSession session) => ((FakeSession)session).Db;

    private sealed class Releaser : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Resources that must exist as "RESOURCE:name" objects; "OLD:name" objects are destructive leftovers; "CONFLICT:name" conflicts.</summary>
public sealed class FakeResources(params string[] wanted) : IEnvironmentResources
{
    public IReadOnlyList<string> Validate(DeploymentEnvironment environment) => [];

    public Task<IReadOnlyList<ResourceAction>> PlanAsync(IDatabaseSession session, DeploymentEnvironment environment, CancellationToken cancellationToken)
    {
        var objects = ((FakeSession)session).Db.Objects;
        var actions = new List<ResourceAction>();
        foreach (var name in wanted)
        {
            actions.Add(objects.Contains("CONFLICT:" + name)
                ? new ResourceAction(ResourceActionKind.Conflict, "key", name, "conflict")
                : objects.Contains("RESOURCE:" + name)
                    ? new ResourceAction(ResourceActionKind.InSync, "key", name, "ok")
                    : new ResourceAction(ResourceActionKind.Apply, "key", name, "create", "RESOURCE:" + name));
        }

        foreach (var old in objects.Where(o => o.StartsWith("OLD:", StringComparison.Ordinal)))
        {
            actions.Add(new ResourceAction(ResourceActionKind.Destructive, "key", old[4..], "drop old", old));
        }

        return Task.FromResult<IReadOnlyList<ResourceAction>>(actions);
    }

    public Task ApplyAsync(IDatabaseSession session, DeploymentEnvironment environment, IReadOnlyList<ResourceAction> actions, CancellationToken cancellationToken)
    {
        var objects = ((FakeSession)session).Db.Objects;
        foreach (var action in actions)
        {
            if (action.Kind == ResourceActionKind.Destructive)
            {
                objects.Remove(action.Payload!);
            }
            else
            {
                objects.Add(action.Payload!);
            }
        }

        return Task.CompletedTask;
    }
}
