using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.Testing;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Oracle.ManagedDataAccess.Client;

namespace Badger.Trek.Providers.Oracle.Tests;

/// <summary>
/// One Oracle for the whole run: TREK_TEST_ORACLE (an ODP.NET connection string for a user that may create users, such
/// as SYSTEM on a pluggable database) or a Testcontainers container of TREK_TEST_ORACLE_IMAGE (default Oracle Database
/// Free 26ai, gvenzl/oracle-free:23-slim-faststart). Each test gets its own schema (user). When neither is available the
/// database tests skip.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    public const string Password = "Trek_test_1";

    private IContainer? container;

    public string? AdminConnectionString { get; private set; }

    public string? UnavailableReason { get; private set; }

    public string Host { get; private set; } = "localhost";

    public int Port { get; private set; } = 1521;

    public string Service { get; private set; } = "FREEPDB1";

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("TREK_TEST_ORACLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            AdminConnectionString = configured;
            var source = new OracleConnectionStringBuilder(configured).DataSource;
            var m = System.Text.RegularExpressions.Regex.Match(source, @"^(?<h>[^:/]+)(?::(?<p>\d+))?/(?<s>.+)$");
            if (m.Success)
            {
                (Host, Port, Service) = (m.Groups["h"].Value, m.Groups["p"].Success ? int.Parse(m.Groups["p"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1521, m.Groups["s"].Value);
            }

            return;
        }

        try
        {
            var image = Environment.GetEnvironmentVariable("TREK_TEST_ORACLE_IMAGE") is { Length: > 0 } i ? i : "gvenzl/oracle-free:23-slim-faststart";
            container = new ContainerBuilder(image)
                .WithEnvironment("ORACLE_PASSWORD", Password)
                .WithPortBinding(1521, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("DATABASE IS READY TO USE!"))
                .Build();
            await container.StartAsync();
            Host = container.Hostname;
            Port = container.GetMappedPublicPort(1521);
            AdminConnectionString = $"User Id=system;Password={Password};Data Source={Host}:{Port}/{Service}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UnavailableReason = $"Oracle container could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set TREK_TEST_ORACLE.";
        }
    }

    public async Task DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>A new, empty schema (user) with the privileges a deploy login has; dropped on dispose.</summary>
    public async Task<TestSchema> CreateSchemaAsync(string? extraGrants = null)
    {
        var name = "TREK_T_" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        await using (var admin = new OracleConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            foreach (var sql in new[]
            {
                $"CREATE USER {name} IDENTIFIED BY \"{Password}\" QUOTA UNLIMITED ON USERS",
                $"GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE, CREATE TRIGGER, CREATE SYNONYM, CREATE TYPE, CREATE MATERIALIZED VIEW TO {name}",
                extraGrants is null ? null : $"GRANT {extraGrants} TO {name}",
            }.OfType<string>())
            {
                await using var command = new OracleCommand(sql, admin);
                await command.ExecuteNonQueryAsync();
            }
        }

        return new TestSchema(this, name);
    }
}

public sealed class TestSchema(OracleFixture fixture, string name) : IConformanceDatabase
{
    public string Name => name;

    public OracleFixture Fixture => fixture;

    public DeploymentEnvironment Profile { get; } = new()
    {
        Name = "conformance",
        Host = fixture.Host,
        Port = fixture.Port,
        Database = fixture.Service,
        Authentication = AuthenticationKind.Password,
        User = name,
        Password = OracleFixture.Password,
        LockTimeoutSeconds = 5,
    };

    public async ValueTask DisposeAsync()
    {
        OracleConnection.ClearAllPools();
        await using var connection = new OracleConnection(fixture.AdminConnectionString);
        await connection.OpenAsync();
        await using var drop = new OracleCommand($"DROP USER {name} CASCADE", connection);
        await drop.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("oracle")]
public sealed class OracleCollection : ICollectionFixture<OracleFixture>
{
}

[Collection("oracle")]
public sealed class OracleConformanceTests(OracleFixture fixture) : ProviderConformanceTests
{
    protected override IDatabaseProvider Provider { get; } = new OracleProvider();

    protected override IConformanceDialect Dialect { get; } = new OracleDialect();

    protected override async Task<IConformanceDatabase?> CreateDatabaseAsync() =>
        fixture.AdminConnectionString is null ? null : await fixture.CreateSchemaAsync();

    private sealed class OracleDialect : IConformanceDialect
    {
        public string Failing => "SELECT * FROM trek_no_such_table;";

        public string CreateTable(string name) => $"CREATE TABLE {name} (id NUMBER(10) NOT NULL PRIMARY KEY, created TIMESTAMP(6) DEFAULT SYSTIMESTAMP NOT NULL);";

        public string DropTable(string name) => $"DROP TABLE {name} PURGE;";

        public string Repeatable(string name, int value) => $"CREATE OR REPLACE VIEW {name} AS SELECT {value} AS value FROM dual;";

        public string VerifyTableExists(string name) => $"SELECT * FROM {name} WHERE 1 = 0;";
    }
}
