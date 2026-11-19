using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;
using Badger.SqlMigrate.Providers.Testing;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

/// <summary>
/// One SQL Server for the whole test run: BSM_TEST_SQLSERVER (a connection string to an existing server, such as
/// LocalDB) or a Testcontainers container of BSM_TEST_SQLSERVER_IMAGE (default SQL Server 2022; CI runs 2017 through
/// 2025, and 2016 on LocalDB). When neither is available the conformance tests skip.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? container;

    public string? MasterConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    /// <summary>The server's major version (13 = SQL Server 2016, 14 = 2017, 16 = 2022); 0 when no server is available.</summary>
    public int MajorVersion { get; private set; }

    /// <summary>HISTORY_RETENTION_PERIOD for temporal tables arrived in SQL Server 2017.</summary>
    public bool SupportsHistoryRetention => MajorVersion >= 14;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("BSM_TEST_SQLSERVER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            MasterConnectionString = configured;
            await ReadVersionAsync();
            return;
        }

        try
        {
            var image = Environment.GetEnvironmentVariable("BSM_TEST_SQLSERVER_IMAGE") is { Length: > 0 } configuredImage ? configuredImage : "mcr.microsoft.com/mssql/server:2022-latest";
            container = new MsSqlBuilder(image).Build();
            await container.StartAsync();
            MasterConnectionString = container.GetConnectionString();
            await ReadVersionAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UnavailableReason = $"SQL Server container could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set BSM_TEST_SQLSERVER.";
        }
    }

    private async Task ReadVersionAsync()
    {
        await using var connection = new SqlConnection(MasterConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);", connection);
        MajorVersion = (int)(await command.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

[CollectionDefinition("sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}

[Collection("sqlserver")]
public sealed class SqlServerConformanceTests(SqlServerFixture fixture) : ProviderConformanceTests
{
    protected override IDatabaseProvider Provider { get; } = new SqlServerProvider();

    protected override IConformanceDialect Dialect { get; } = new TSqlDialect();

    protected override async Task<IConformanceDatabase?> CreateDatabaseAsync()
    {
        if (fixture.MasterConnectionString is null)
        {
            return null;
        }

        var name = "bsm_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var master = new SqlConnection(fixture.MasterConnectionString))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{name}];", master);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new SqlConnectionStringBuilder(fixture.MasterConnectionString) { InitialCatalog = name };
        return new Database(fixture.MasterConnectionString, name, new DeploymentEnvironment
        {
            Name = "conformance",
            ConnectionString = builder.ConnectionString,
            LockTimeoutSeconds = 5,
        });
    }

    private sealed class Database(string master, string name, DeploymentEnvironment profile) : IConformanceDatabase
    {
        public DeploymentEnvironment Profile => profile;

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(master);
            await connection.OpenAsync();
            await using var drop = new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class TSqlDialect : IConformanceDialect
    {
        public string Failing => "GO\nSELECT 1/0;";

        public string CreateTable(string name) => $"CREATE TABLE dbo.[{name}] (id int NOT NULL CONSTRAINT [PK_{name}] PRIMARY KEY, created datetime2 NOT NULL DEFAULT (sysutcdatetime()));";

        public string DropTable(string name) => $"DROP TABLE dbo.[{name}];";

        public string Repeatable(string name, int value) => $"CREATE OR ALTER VIEW dbo.[{name}] AS SELECT {value} AS value;\nGO";

        public string VerifyTableExists(string name) => $"IF OBJECT_ID(N'dbo.[{name}]', N'U') IS NULL THROW 50000, N'{name} missing', 1;";
    }
}
