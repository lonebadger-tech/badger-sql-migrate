using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.PostgreSql;
using Badger.SqlMigrate.Providers.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Badger.SqlMigrate.Providers.PostgreSql.Tests;

/// <summary>
/// One PostgreSQL for the whole run: BSM_TEST_POSTGRES (a connection string to an existing server) or a
/// Testcontainers postgres:17 container. When neither is available the database tests skip.
/// </summary>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private PostgreSqlContainer? container;

    public string? AdminConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("BSM_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            AdminConnectionString = configured;
            return;
        }

        try
        {
            container = new PostgreSqlBuilder("postgres:17").Build();
            await container.StartAsync();
            AdminConnectionString = container.GetConnectionString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UnavailableReason = $"PostgreSQL container could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set BSM_TEST_POSTGRES.";
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
        var name = "bsm_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
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
        ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString,
        LockTimeoutSeconds = 5,
    };

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE);", connection);
        await drop.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("postgresql")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
}

[Collection("postgresql")]
public sealed class PostgreSqlConformanceTests(PostgreSqlFixture fixture) : ProviderConformanceTests
{
    protected override IDatabaseProvider Provider { get; } = new PostgreSqlProvider();

    protected override IConformanceDialect Dialect { get; } = new PgDialect();

    protected override async Task<IConformanceDatabase?> CreateDatabaseAsync() =>
        fixture.AdminConnectionString is null ? null : await fixture.CreateDatabaseAsync();

    private sealed class PgDialect : IConformanceDialect
    {
        public string Failing => "SELECT 1 / 0;";

        public string CreateTable(string name) => $"CREATE TABLE public.{name} (id integer NOT NULL PRIMARY KEY, created timestamptz NOT NULL DEFAULT now());";

        public string DropTable(string name) => $"DROP TABLE public.{name};";

        public string Repeatable(string name, int value) => $"CREATE OR REPLACE VIEW public.{name} AS SELECT {value} AS value;";

        public string VerifyTableExists(string name) => $"DO $$ BEGIN IF to_regclass('public.{name}') IS NULL THEN RAISE EXCEPTION '{name} missing'; END IF; END $$;";
    }
}
