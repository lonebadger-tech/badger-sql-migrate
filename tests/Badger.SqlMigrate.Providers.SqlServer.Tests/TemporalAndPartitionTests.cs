using System.Text.Json.Nodes;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

[Collection("sqlserver")]
public sealed class TemporalAndPartitionTests(SqlServerFixture fixture)
{
    private const string Deploy = """
        CREATE PARTITION FUNCTION pfSaleDate (date) AS RANGE RIGHT FOR VALUES ('2026-01-01', '2026-02-01');
        CREATE PARTITION SCHEME psSaleDate AS PARTITION pfSaleDate ALL TO ([PRIMARY]);
        CREATE TABLE dbo.Sale (
            SaleDate date NOT NULL,
            Id int NOT NULL,
            Amount money NOT NULL,
            CONSTRAINT PK_Sale PRIMARY KEY (SaleDate, Id)) ON psSaleDate (SaleDate);
        CREATE INDEX IX_Sale_Id ON dbo.Sale (Id) ON psSaleDate (SaleDate);

        -- history table named by SQL Server: MSSQL_TemporalHistoryFor_<object id>, different on every server
        CREATE TABLE dbo.Price (
            Id int NOT NULL CONSTRAINT PK_Price PRIMARY KEY,
            Amount money NOT NULL,
            ValidFrom datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
            ValidTo datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON);

        CREATE TABLE dbo.Rate (
            Id int NOT NULL CONSTRAINT PK_Rate PRIMARY KEY,
            ValidFrom datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
            ValidTo datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.RateHistory, HISTORY_RETENTION_PERIOD = 6 MONTHS));
        """;

    private const string Revert = """
        DECLARE @history nvarchar(300) = (
            SELECT QUOTENAME(SCHEMA_NAME(h.schema_id)) + N'.' + QUOTENAME(h.name)
            FROM sys.tables t JOIN sys.tables h ON h.object_id = t.history_table_id
            WHERE t.object_id = OBJECT_ID(N'dbo.Price'));
        ALTER TABLE dbo.Price SET (SYSTEM_VERSIONING = OFF);
        DROP TABLE dbo.Price;
        EXEC (N'DROP TABLE ' + @history);

        ALTER TABLE dbo.Rate SET (SYSTEM_VERSIONING = OFF);
        DROP TABLE dbo.Rate, dbo.RateHistory;

        DROP TABLE dbo.Sale;
        DROP PARTITION SCHEME psSaleDate;
        DROP PARTITION FUNCTION pfSaleDate;
        """;

    private readonly SqlServerProvider provider = new();

    [SkippableFact]
    public async Task Temporal_and_partitioned_schemas_fingerprint_the_same_on_every_database_and_round_trip()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        using var project = new ProjectDir();
        project.Change("sales-and-prices", Deploy, Revert);
        await using var a = await DatabaseAsync();
        await using var b = await DatabaseAsync();

        string dump;
        await using (var e = await EngineAsync(a.Environment(), project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
            dump = (await e.Engine.GetRecordedSchemaAsync())!.Dump;
        }

        Assert.Contains("temporal|dbo.Price|SYSTEM_VERSIONED_TEMPORAL_TABLE|ValidFrom|ValidTo|dbo.MSSQL_TemporalHistoryFor_<dbo.Price>|-1|INFINITE", dump, StringComparison.Ordinal);
        Assert.Contains("temporal|dbo.Rate|SYSTEM_VERSIONED_TEMPORAL_TABLE|ValidFrom|ValidTo|dbo.RateHistory|6|MONTH", dump, StringComparison.Ordinal);
        Assert.Contains("|AS_ROW_START|hidden", dump, StringComparison.Ordinal);
        Assert.Contains("pfunction|pfSaleDate|date|3|10|0||RIGHT|2026-01-01,2026-02-01", dump, StringComparison.Ordinal);
        Assert.Contains("pscheme|psSaleDate|pfSaleDate|PRIMARY,PRIMARY,PRIMARY,PRIMARY\n", dump, StringComparison.Ordinal);
        Assert.Contains("partitioned|dbo.Sale|PK_Sale|psSaleDate|SaleDate", dump, StringComparison.Ordinal);
        Assert.Contains("partitioned|dbo.Sale|IX_Sale_Id|psSaleDate|SaleDate", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("TemporalHistoryFor_1", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("TemporalHistoryFor_9", dump, StringComparison.Ordinal);

        // A second database gets different object ids, so a different anonymous history table name, but the same fingerprint.
        await using (var e = await EngineAsync(b.Environment(), project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
            Assert.Equal(dump, (await e.Engine.GetRecordedSchemaAsync())!.Dump);
            Assert.True((await e.Engine.VerifyAsync()).Ok);

            Assert.Equal(["sales-and-prices"], (await e.Engine.RevertAsync(new RevertOptions { All = true })).Reverted);
            Assert.DoesNotContain("temporal|", await e.Engine.CaptureSchemaAsync(), StringComparison.Ordinal);
            await e.Engine.DeployAsync(new DeployOptions());
            Assert.Equal(dump, await e.Engine.CaptureSchemaAsync());
        }
    }

    [SkippableFact]
    public async Task Partition_boundaries_are_drift_unless_the_environment_ignores_them()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        using var project = new ProjectDir();
        project.Change("sales-and-prices", Deploy, Revert);
        await using var strict = await DatabaseAsync();
        await using var window = await DatabaseAsync();
        const string slide = "ALTER PARTITION SCHEME psSaleDate NEXT USED [PRIMARY]; ALTER PARTITION FUNCTION pfSaleDate() SPLIT RANGE ('2026-03-01');";

        await using (var e = await EngineAsync(strict.Environment(), project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
            await e.Session.ExecuteAsync(slide, null, CancellationToken.None);
            Assert.False((await e.Engine.VerifyAsync()).Ok);

            var recorded = (await e.Engine.GetRecordedSchemaAsync())!.Dump;
            var diff = SchemaDiff.Compare(provider.Schema.Parse(recorded), provider.Schema.Parse(await e.Engine.CaptureSchemaAsync()));
            var changed = Assert.Single(diff);
            Assert.Equal((SchemaDifferenceKind.Changed, "pfunction pfSaleDate"), (changed.Kind, changed.Key));
            Assert.EndsWith("|2026-01-01,2026-02-01,2026-03-01", changed.Right, StringComparison.Ordinal);
        }

        await using (var e = await EngineAsync(window.Environment(ignoreBoundaries: true), project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
            var dump = (await e.Engine.GetRecordedSchemaAsync())!.Dump;
            Assert.Contains("pfunction|pfSaleDate|date|3|10|0||RIGHT\n", dump + "\n", StringComparison.Ordinal);
            Assert.Contains("pscheme|psSaleDate|pfSaleDate|PRIMARY\n", dump + "\n", StringComparison.Ordinal);

            await e.Session.ExecuteAsync(slide, null, CancellationToken.None);
            Assert.True((await e.Engine.VerifyAsync()).Ok);
        }
    }

    [SkippableFact]
    public async Task Whitespace_around_a_module_definition_does_not_change_the_fingerprint()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        await using var a = await DatabaseAsync();
        await using var b = await DatabaseAsync();
        var dumps = new List<string>();
        foreach (var (db, sql) in new[] { (a, "\n  CREATE VIEW dbo.v AS SELECT 1 AS x;\r\n\r\n"), (b, "CREATE VIEW dbo.v AS SELECT 1 AS x;") })
        {
            await using var session = await provider.OpenSessionAsync(db.Environment(), SessionContext.Empty, CancellationToken.None);
            await session.ExecuteAsync(sql, null, CancellationToken.None);
            dumps.Add(await provider.Schema.CaptureAsync(session, "bsm", CancellationToken.None));
        }

        Assert.Equal(dumps[0], dumps[1]);
        Assert.Contains("module|V |dbo.v|CREATE VIEW dbo.v AS SELECT 1 AS x;", dumps[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_settings_are_validated()
    {
        var bad = new DeploymentEnvironment { Name = "p", Host = "x", Authentication = AuthenticationKind.Integrated, ProviderSettings = (JsonObject)JsonNode.Parse("""{ "schemaFingerprint": { "partitionBoundaries": "sometimes" } }""")! };
        Assert.Contains(provider.ValidateEnvironment(bad), p => p.Contains("invalid schemaFingerprint settings", StringComparison.Ordinal));

        var good = bad with { ProviderSettings = (JsonObject)JsonNode.Parse("""{ "schemaFingerprint": { "partitionBoundaries": "ignore" } }""")! };
        Assert.Empty(provider.ValidateEnvironment(good));
        Assert.Equal(PartitionBoundaries.Ignore, SchemaFingerprintSettings.From(good).PartitionBoundaries);
    }

    private async Task<TestDatabase> DatabaseAsync()
    {
        var name = "bsm_tp_" + Guid.NewGuid().ToString("N")[..12];
        await using var master = new SqlConnection(fixture.MasterConnectionString);
        await master.OpenAsync();
        await using var create = new SqlCommand($"CREATE DATABASE [{name}];", master);
        await create.ExecuteNonQueryAsync();
        return new TestDatabase(fixture.MasterConnectionString!, name);
    }

    private async Task<EngineHandle> EngineAsync(DeploymentEnvironment environment, ProjectDir project)
    {
        var session = await provider.OpenSessionAsync(environment, SessionContext.Empty, CancellationToken.None);
        var engine = new MigrationEngine(MigrationProject.Load(project.Root, provider.Id), provider, environment, session, new Actor("tests", "host", "0.0.0"));
        return new EngineHandle(engine, session);
    }

    private sealed record EngineHandle(MigrationEngine Engine, IDatabaseSession Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class TestDatabase(string master, string name) : IAsyncDisposable
    {
        public DeploymentEnvironment Environment(bool ignoreBoundaries = false) => new()
        {
            Name = "test",
            ConnectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString,
            ProviderSettings = ignoreBoundaries ? (JsonObject)JsonNode.Parse("""{ "schemaFingerprint": { "partitionBoundaries": "ignore" } }""")! : [],
        };

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(master);
            await connection.OpenAsync();
            await using var drop = new SqlCommand($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ProjectDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bsm-tp", Guid.NewGuid().ToString("N"));

        public ProjectDir()
        {
            Directory.CreateDirectory(Path.Combine(Root, MigrationProject.ChangesFolder));
            File.WriteAllText(Path.Combine(Root, "bsm.plan"), string.Empty);
        }

        public void Change(string name, string deploy, string revert)
        {
            var dir = Path.Combine(Root, MigrationProject.ChangesFolder, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MigrationProject.DeployFile), deploy);
            File.WriteAllText(Path.Combine(dir, MigrationProject.RevertFile), revert);
            File.AppendAllText(Path.Combine(Root, "bsm.plan"), name + "\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
