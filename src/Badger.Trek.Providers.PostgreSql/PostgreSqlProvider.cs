using System.Globalization;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Npgsql;

namespace Badger.Trek.Providers.PostgreSql;

/// <summary>PostgreSQL 15+ (including Azure Database for PostgreSQL, Amazon RDS/Aurora, Supabase, Neon).</summary>
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

    public Core.Conversion.ISchemaExporter Exporter { get; } = new Conversion.PostgreSqlExporter();

    public Core.Conversion.IDataTransfer Data { get; } = new Conversion.PostgreSqlDataTransfer();

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

    /// <summary>
    /// The oldest PostgreSQL major version trek supports: the oldest the PostgreSQL project still supports (each major
    /// version for five years; 14 ended in November 2026).
    /// </summary>
    public const int OldestSupportedMajorVersion = 15;

    /// <summary>A note for 'trek doctor' when the server (server_version_num, e.g. 140025) is older than that, else empty.</summary>
    public static string UnsupportedVersionNote(int serverVersionNum) =>
        serverVersionNum / 10000 < OldestSupportedMajorVersion
            ? $" - not supported: PostgreSQL {serverVersionNum / 10000} is past its end of life; Badger Trek needs PostgreSQL {OldestSupportedMajorVersion} or later"
            : string.Empty;

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        var row = (await session.QueryAsync("""
            SELECT version() AS version, current_user AS login, current_database() AS database,
                   (SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()) AS ssl,
                   (SELECT version FROM pg_stat_ssl WHERE pid = pg_backend_pid()) AS tls_version,
                   current_setting('server_encoding') AS encoding,
                   current_setting('server_version_num')::int AS version_num
            """, null, cancellationToken).ConfigureAwait(false))[0];
        string S(string key) => Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? "unknown";
        return
        [
            new("server", S("version") + UnsupportedVersionNote(Convert.ToInt32(row["version_num"], CultureInfo.InvariantCulture))),
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
