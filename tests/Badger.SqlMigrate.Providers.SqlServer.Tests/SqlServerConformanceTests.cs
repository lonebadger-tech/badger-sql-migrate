using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;
using Badger.SqlMigrate.Providers.Testing;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

/// <summary>
/// One SQL Server for the whole test run: BSM_TEST_SQLSERVER (a connection string to an existing server)
/// or a Testcontainers SQL Server 2022 container. When neither is available the conformance tests skip.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? container;

    public string? MasterConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("BSM_TEST_SQLSERVER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            MasterConnectionString = configured;
            return;
        }

        try
        {
            container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await container.StartAsync();
            MasterConnectionString = container.GetConnectionString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UnavailableReason = $"SQL Server container could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set BSM_TEST_SQLSERVER.";
        }
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
