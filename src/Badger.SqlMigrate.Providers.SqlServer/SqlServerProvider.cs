using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>SQL Server and Azure SQL (Database, Managed Instance) provider.</summary>
public sealed class SqlServerProvider : IDatabaseProvider
{
    public const string ProviderId = "sqlserver";

    public string Id => ProviderId;

    public string DisplayName => "SQL Server / Azure SQL";

    public ProviderCapabilities Capabilities { get; } = new()
    {
        TransactionalDdl = true,
        SupportsDeployLock = true,
        SupportsSchemaFingerprint = true,
        SupportedAuthentication = Enum.GetValues<AuthenticationKind>().ToHashSet(),
    };

    public IScriptParser Parser { get; } = new TSqlBatchParser();

    public IScriptLinter Linter { get; } = new TSqlLinter();

    public IStateStore StateStore { get; } = new SqlServerStateStore();

    public IDeployLock Lock { get; } = new SqlServerDeployLock();

    public ISchemaInspector Schema { get; } = new SqlServerSchemaInspector();


    public IEnvironmentResources? Resources { get; } = new SqlServerEnvironmentResources();

    /// <summary>Registers a custom Always Encrypted key store provider for all environments (library use).</summary>
    public static void RegisterKeyStoreProvider(string name, SqlColumnEncryptionKeyStoreProvider provider) => KeyStoreProviders.Register(name, provider);

    public IReadOnlyList<string> ValidateEnvironment(DeploymentEnvironment environment) =>
        [.. SqlServerConnectionFactory.Validate(environment), .. Resources!.Validate(environment)];

    public Endpoint ResolveEndpoint(DeploymentEnvironment environment) => SqlServerConnectionFactory.ResolveEndpoint(environment);

    public string? ResolveDatabaseName(DeploymentEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.Database))
        {
            return environment.Database;
        }

        if (environment.ConnectionProperties.TryGetValue("Initial Catalog", out var catalog) || environment.ConnectionProperties.TryGetValue("Database", out catalog))
        {
            return catalog;
        }

        return environment.ConnectionString is null ? null : new SqlConnectionStringBuilder(environment.ConnectionString).InitialCatalog;
    }

    public async Task<IDatabaseSession> OpenSessionAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var fingerprint = SchemaFingerprintSettings.From(environment);
        var (connection, description, keyStores) = SqlServerConnectionFactory.Create(environment, context);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new MigrationException($"Could not connect to {description}: {ex.Message}", ex);
        }

        return new SqlServerSession(connection, description, keyStores) { Fingerprint = fingerprint };
    }

    public async Task<IReadOnlyList<KeyValuePair<string, string>>> DescribeAsync(IDatabaseSession session, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS version,
                CAST(SERVERPROPERTY('Edition') AS nvarchar(128)) AS edition,
                CAST(SERVERPROPERTY('EngineEdition') AS int) AS engine_edition,
                SUSER_SNAME() AS login,
                DB_NAME() AS [database],
                (SELECT encrypt_option FROM sys.dm_exec_connections WHERE session_id = @@SPID) AS encrypted,
                (SELECT auth_scheme FROM sys.dm_exec_connections WHERE session_id = @@SPID) AS auth_scheme
            """;
        var row = (await session.QueryAsync(sql, null, cancellationToken).ConfigureAwait(false))[0];
        var engine = Convert.ToInt32(row["engine_edition"], System.Globalization.CultureInfo.InvariantCulture) switch
        {
            5 => "Azure SQL Database",
            8 => "Azure SQL Managed Instance",
            9 => "Azure SQL Edge",
            _ => "SQL Server",
        };
        return
        [
            new("server", $"{engine} {row["version"]} ({row["edition"]})"),
            new("login", Convert.ToString(row["login"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
            new("database", Convert.ToString(row["database"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
            new("encrypted", Convert.ToString(row["encrypted"], System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"),
            new("auth scheme", Convert.ToString(row["auth_scheme"], System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"),
        ];
    }

    public string FormatBatchSeparator() => "GO";

    public string FormatComment(string text) => "-- " + text;
}
