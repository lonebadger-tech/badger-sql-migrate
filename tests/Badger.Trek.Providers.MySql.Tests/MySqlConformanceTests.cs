using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.Testing;
using MySqlConnector;
using Testcontainers.MySql;

namespace Badger.Trek.Providers.MySql.Tests;

/// <summary>
/// One MySQL for the whole run: TREK_TEST_MYSQL (a connection string to an existing server, with rights to create
/// databases) or a Testcontainers container of TREK_TEST_MYSQL_IMAGE (default mysql:8.4; CI runs 8.4 and 9.7).
/// When neither is available the database tests skip.
/// </summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private MySqlContainer? container;

    public string? AdminConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("TREK_TEST_MYSQL");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            AdminConnectionString = configured;
            return;
        }

        try
        {
            var image = Environment.GetEnvironmentVariable("TREK_TEST_MYSQL_IMAGE") is { Length: > 0 } i ? i : "mysql:8.4";
            container = new MySqlBuilder(image).WithUsername("root").WithPassword("trek-test").Build();
            await container.StartAsync();
            AdminConnectionString = container.GetConnectionString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UnavailableReason = $"MySQL container could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set TREK_TEST_MYSQL.";
        }
    }

    public async Task DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>A new, empty database; dropped on dispose.</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = "trek_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new MySqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new MySqlCommand($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;", admin);
            await create.ExecuteNonQueryAsync();
        }

        return new TestDatabase(AdminConnectionString!, name);
    }
}

public sealed class TestDatabase(string admin, string name) : IConformanceDatabase
{
    public string Name => name;

    public DeploymentEnvironment Profile { get; } = new()
    {
        Name = "conformance",
        ConnectionString = new MySqlConnectionStringBuilder(admin) { Database = name }.ConnectionString,
        LockTimeoutSeconds = 5,
    };

    public async ValueTask DisposeAsync()
    {
        MySqlConnection.ClearAllPools();
        await using var connection = new MySqlConnection(admin);
        await connection.OpenAsync();
        await using var drop = new MySqlCommand($"DROP DATABASE IF EXISTS `{name}`;", connection);
        await drop.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>
{
}

[Collection("mysql")]
public sealed class MySqlConformanceTests(MySqlFixture fixture) : ProviderConformanceTests
{
    protected override IDatabaseProvider Provider { get; } = new MySqlProvider();

    protected override IConformanceDialect Dialect { get; } = new MyDialect();

    protected override async Task<IConformanceDatabase?> CreateDatabaseAsync() =>
        fixture.AdminConnectionString is null ? null : await fixture.CreateDatabaseAsync();

    private sealed class MyDialect : IConformanceDialect
    {
        public string Failing => "SELECT * FROM trek_no_such_table;";

        public string CreateTable(string name) => $"CREATE TABLE {name} (id INT NOT NULL PRIMARY KEY, created DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6));";

        public string DropTable(string name) => $"DROP TABLE {name};";

        public string Repeatable(string name, int value) => $"CREATE OR REPLACE VIEW {name} AS SELECT {value} AS value;";

        public string VerifyTableExists(string name) => $"SELECT * FROM {name} LIMIT 0;";
    }
}
