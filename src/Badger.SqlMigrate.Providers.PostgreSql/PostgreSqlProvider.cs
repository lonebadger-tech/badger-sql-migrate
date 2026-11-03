using System.Globalization;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Npgsql;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>PostgreSQL 12+ (including Azure Database for PostgreSQL, Amazon RDS/Aurora, Supabase, Neon).</summary>
public sealed class PostgreSqlProvider : IDatabaseProvider
{
    public const string ProviderId = "postgresql";

    public string Id => ProviderId;

    public string DisplayName => "PostgreSQL";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        TransactionalDdl = true,
        SupportsDeployLock = true,
        SupportsSchemaFingerprint = true,
        SupportedAuthentication = PostgreSqlConnectionFactory.Supported,
    };

    public IScriptParser Parser { get; } = new PostgreSqlStatementSplitter();

    public IScriptIncludes Includes { get; } = new PostgreSqlIncludes();

    public IScriptLinter Linter { get; } = new PostgreSqlLinter();

    public IStateStore StateStore { get; } = new PostgreSqlStateStore();

    public IDeployLock Lock { get; } = new PostgreSqlDeployLock();

    public ISchemaInspector Schema { get; } = new PostgreSqlSchemaInspector();

    public IEnvironmentResources? Resources => null;

    public ISchemaScripter Scripter { get; } = new Scripting.PostgreSqlScripter();

    public IScratchDatabases ScratchDatabases => new Scripting.PostgreSqlScratchDatabases(this);

    public string DefaultSchema => "public";

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) => PostgreSqlConnectionFactory.Validate(environment);

    public Endpoint ResolveEndpoint(DeploymentEnvironment environment) => PostgreSqlConnectionFactory.ResolveEndpoint(environment);

    public string? ResolveDatabaseName(DeploymentEnvironment environment) => PostgreSqlConnectionFactory.ResolveDatabaseName(environment);

    public async Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var fingerprint = PostgreSqlFingerprintSettings.From(environment);
        var (builder, description) = await PostgreSqlConnectionFactory.BuildAsync(environment, context, cancellationToken).ConfigureAwait(false);
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new MigrationException($"Could not connect to {description}: {ex.Message}", ex);
        }

        return new PostgreSqlSession(connection, description) { Fingerprint = fingerprint };
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        var row = (await session.QueryAsync("""
            SELECT version() AS version, current_user AS login, current_database() AS database,
                   (SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()) AS ssl,
                   (SELECT version FROM pg_stat_ssl WHERE pid = pg_backend_pid()) AS tls_version,
                   current_setting('server_encoding') AS encoding
            """, null, cancellationToken).ConfigureAwait(false))[0];
        string S(string key) => Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? "unknown";
        return
        [
            new("server", S("version")),
            new("login", S("login")),
            new("database", S("database")),
            new("encrypted", row["ssl"] is true ? $"yes ({S("tls_version")})" : "no"),
            new("encoding", S("encoding")),
        ];
    }

    /// <summary>PostgreSQL separates statements with semicolons; there is no batch separator line.</summary>
    public string FormatBatchSeparator() => string.Empty;

    public string FormatComment(string text) => "-- " + text;
}
