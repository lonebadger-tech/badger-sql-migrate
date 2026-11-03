namespace Badger.SqlMigrate.Core.Providers;

/// <summary>Persists badger's journal in the target database using the vendor's DDL and types.</summary>
public interface IStateStore
{
    /// <summary>Creates or upgrades the state tables. Idempotent.</summary>
    Task EnsureCreatedAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken);

    /// <summary>Returns false (without creating anything) when the state tables do not exist yet.</summary>
    Task<bool> ExistsAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken);

    /// <summary>Reads a value from the state meta table (null when absent).</summary>
    Task<string?> GetMetaAsync(IDatabaseSession session, string schema, string key, CancellationToken cancellationToken);

    Task SetMetaAsync(IDatabaseSession session, string schema, string key, string value, CancellationToken cancellationToken);

    /// <summary>Currently applied changes ordered by deploy sequence.</summary>
    Task<IReadOnlyList<AppliedChange>> GetAppliedChangesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken);

    Task InsertChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken);

    Task DeleteChangeAsync(IDatabaseSession session, string schema, string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<AppliedRepeatable>> GetRepeatablesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken);

    Task UpsertRepeatableAsync(IDatabaseSession session, string schema, AppliedRepeatable repeatable, CancellationToken cancellationToken);

    Task AppendEventAsync(IDatabaseSession session, string schema, MigrationEvent migrationEvent, CancellationToken cancellationToken);

    Task<IReadOnlyList<MigrationEvent>> GetEventsAsync(IDatabaseSession session, string schema, int limit, CancellationToken cancellationToken);

    Task InsertSchemaSnapshotAsync(IDatabaseSession session, string schema, SchemaSnapshot snapshot, CancellationToken cancellationToken);

    Task<SchemaSnapshot?> GetLatestSchemaSnapshotAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken);
}

/// <summary>A change as recorded in the database. Scripts are stored so revert works from any checkout.</summary>
public sealed record AppliedChange
{
    /// <summary>Deploy order; assigned by the store on insert.</summary>
    public long Sequence { get; init; }

    public required string Name { get; init; }

    public required string ScriptHash { get; init; }

    /// <summary>Chain id computed from the actual deploy order in this database.</summary>
    public required string ChainId { get; init; }

    public required string DeployScript { get; init; }

    public string? RevertScript { get; init; }

    public string? VerifyScript { get; init; }

    public required int Flags { get; init; }

    public required DateTimeOffset DeployedAtUtc { get; init; }

    public required string DeployedBy { get; init; }

    public required string Host { get; init; }

    public required string ToolVersion { get; init; }

    public long DurationMs { get; init; }

    public bool AppliedOutOfOrder { get; init; }

    /// <summary>SHA-256 of deploy+verify as rendered with this environment's variables (null when per-run variables were used).</summary>
    public string? RenderedHash { get; init; }

    /// <summary>SHA-256 of the revert script as rendered at deploy time; revert refuses if it now renders differently.</summary>
    public string? RenderedRevertHash { get; init; }

    /// <summary>True when recorded by 'bsm baseline' without executing the deploy script.</summary>
    public bool Baselined { get; init; }
}

public sealed record AppliedRepeatable
{
    public required string Name { get; init; }

    public required string Hash { get; init; }

    public required string Script { get; init; }

    public string? PreviousHash { get; init; }

    public string? PreviousScript { get; init; }

    public required DateTimeOffset AppliedAtUtc { get; init; }

    public required string AppliedBy { get; init; }
}

public enum MigrationEventKind
{
    Deploy,
    Revert,
    Fail,
    Baseline,
    Repeatable,
    Verify,
    Tag,
    Resource,
}

public sealed record MigrationEvent
{
    public long Id { get; init; }

    public required MigrationEventKind Kind { get; init; }

    public required string Name { get; init; }

    public string? ScriptHash { get; init; }

    public string? ChainId { get; init; }

    public string? Message { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    public required string Actor { get; init; }

    public required string Host { get; init; }

    public required string ToolVersion { get; init; }

    /// <summary>Name of the badger environment that performed the action.</summary>
    public string? Environment { get; init; }
}

public sealed record SchemaSnapshot(string Fingerprint, string Dump, DateTimeOffset CapturedAtUtc, string? HeadChainId);
