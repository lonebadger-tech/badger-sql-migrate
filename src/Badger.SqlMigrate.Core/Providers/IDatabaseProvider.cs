using Badger.SqlMigrate.Core.Configuration;

namespace Badger.SqlMigrate.Core.Providers;

/// <summary>
/// Everything vendor-specific lives behind this contract. The engine, CLI and hashing never reference a
/// database driver; adding PostgreSQL/MySQL/SQLite means a new assembly implementing this interface and
/// one registration in <see cref="ProviderRegistry"/>.
/// </summary>
public interface IDatabaseProvider
{
    /// <summary>Stable id used in bsm.yaml and in change chain ids, e.g. "sqlserver".</summary>
    string Id { get; }

    string DisplayName { get; }

    ProviderCapabilities Capabilities { get; }

    /// <summary>Validates an environment without connecting (unsupported auth kinds, bad connection properties, resource settings).</summary>
    IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment);

    /// <summary>Opens a session to the environment's database.</summary>
    Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken);

    /// <summary>The host and port the environment points at (needed to build an SSH tunnel).</summary>
    Endpoint ResolveEndpoint(DeploymentEnvironment environment);

    /// <summary>The database name the environment targets (for the {db} built-in variable), or null if unknown.</summary>
    string? ResolveDatabaseName(DeploymentEnvironment environment);

    /// <summary>Per-environment resources provisioned before changes run (null when the provider has none).</summary>
    IEnvironmentResources? Resources { get; }

    IScriptParser Parser { get; }

    IScriptLinter Linter { get; }

    IStateStore StateStore { get; }

    IDeployLock Lock { get; }

    ISchemaInspector Schema { get; }


    /// <summary>Server facts for 'bsm doctor' (version, login, database, encryption state).</summary>
    Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken);

    /// <summary>Text shown to separate batches when producing a reviewable script (e.g. "GO").</summary>
    string FormatBatchSeparator();

    /// <summary>A comment line in this vendor's dialect.</summary>
    string FormatComment(string text);

    /// <summary>Scripts schema differences as DDL; null when the provider cannot.</summary>
    ISchemaScripter? Scripter => null;

    /// <summary>Creates temporary databases; null when the provider cannot.</summary>
    IScratchDatabases? ScratchDatabases => null;

    /// <summary>Quotes an identifier (schema or table name) for this vendor. Default: ANSI double quotes.</summary>
    string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

public sealed record Endpoint(string Host, int Port);

public sealed record ProviderCapabilities
{
    /// <summary>DDL can be rolled back in a transaction (SQL Server, PostgreSQL, SQLite: yes; MySQL: no).</summary>
    public required bool TransactionalDdl { get; init; }

    /// <summary>The database offers a lock the engine can hold for the whole run.</summary>
    public required bool SupportsDeployLock { get; init; }

    public required bool SupportsSchemaFingerprint { get; init; }

    public IReadOnlySet<Configuration.AuthenticationKind> SupportedAuthentication { get; init; } = new HashSet<Configuration.AuthenticationKind>();
}
