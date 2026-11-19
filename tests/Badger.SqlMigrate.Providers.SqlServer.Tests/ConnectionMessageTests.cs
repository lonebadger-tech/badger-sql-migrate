using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

[Collection("sqlserver")]
public sealed class ConnectionMessageTests(SqlServerFixture fixture)
{
    [Theory]
    [InlineData(".", null, null, "sqlserver://./app")]
    [InlineData("(localdb)\\MSSQLLocalDB", null, null, "sqlserver://(localdb)\\MSSQLLocalDB/app")]
    [InlineData("sql01", 1444, null, "sqlserver://sql01:1444/app")]
    [InlineData(null, null, "Server=tcp:sql01,1455;Database=app", "sqlserver://sql01:1455/app")]
    [InlineData(null, null, "Server=sql01;Database=app", "sqlserver://sql01/app")]
    public void The_description_shows_a_port_only_when_one_is_configured(string? host, int? port, string? connectionString, string expected)
    {
        var environment = new DeploymentEnvironment { Name = "x", Host = host, Port = port, ConnectionString = connectionString, Database = "app" };
        Assert.Equal(expected, SqlServerConnectionFactory.Describe(environment, SqlServerConnectionFactory.ResolveEndpoint(environment), "app", tunnel: false));
    }

    [SkippableFact]
    public async Task A_missing_database_says_to_create_it()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        var missing = "bsm_missing_" + Guid.NewGuid().ToString("N")[..8];
        var environment = new DeploymentEnvironment
        {
            Name = "x",
            ConnectionString = new SqlConnectionStringBuilder(fixture.MasterConnectionString) { InitialCatalog = missing, TrustServerCertificate = true }.ConnectionString,
        };

        var ex = await Assert.ThrowsAsync<MigrationException>(() => new SqlServerProvider().OpenSessionAsync(environment, SessionContext.Empty, CancellationToken.None));
        Assert.Contains($"Could not open database '{missing}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("create it first", ex.Message, StringComparison.Ordinal);
    }
}
