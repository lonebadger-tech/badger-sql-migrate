using Badger.SqlMigrate.Core.Configuration;

namespace Badger.SqlMigrate.Core.Providers;

/// <summary>
/// Things that must exist before changes run but legitimately differ per environment, so they cannot live in
/// hashed change scripts. SQL Server uses this for Always Encrypted column master keys and column encryption
/// keys: scripts reference stable key names; each environment supplies its own key paths and key material.
/// </summary>
public interface IEnvironmentResources
{
    /// <summary>Compares the environment's configuration with the database. Read-only.</summary>
    Task<IReadOnlyList<ResourceAction>> PlanAsync(IDatabaseSession session, DeploymentEnvironment environment, CancellationToken cancellationToken);

    /// <summary>Applies the given actions (taken from <see cref="PlanAsync"/>).</summary>
    Task ApplyAsync(IDatabaseSession session, DeploymentEnvironment environment, IReadOnlyList<ResourceAction> actions, CancellationToken cancellationToken);

    /// <summary>Validates the provider settings for resources without connecting.</summary>
    IReadOnlyList<string> Validate(DeploymentEnvironment environment);
}

public enum ResourceActionKind
{
    /// <summary>Already as configured. Reported for status only.</summary>
    InSync,

    /// <summary>Safe to apply automatically before a deploy (create a key, add a key value).</summary>
    Apply,

    /// <summary>Removes something; only applied with explicit consent (e.g. dropping an old key value after rotation).</summary>
    Destructive,

    /// <summary>Informational, nothing to do (e.g. a key exists in the database but not in config).</summary>
    Info,

    /// <summary>The database contradicts the configuration in a way badger will not fix (e.g. a key path differs).</summary>
    Conflict,
}

/// <param name="Kind">What applying means.</param>
/// <param name="ResourceType">E.g. "column master key".</param>
/// <param name="Name">Resource name.</param>
/// <param name="Description">Human-readable action, never containing secrets or key material.</param>
/// <param name="Payload">Provider-private data needed to apply (not shown).</param>
public sealed record ResourceAction(ResourceActionKind Kind, string ResourceType, string Name, string Description, string? Payload = null);
