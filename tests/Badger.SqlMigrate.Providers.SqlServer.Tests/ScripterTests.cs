using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer.Scripting;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

/// <summary>
/// Round trips: deploy must turn database A into a schema-identical copy of B, and revert must turn it back.
/// "Identical" is the schema fingerprint dump, so this also proves the generated SQL is complete.
/// </summary>
[Collection("sqlserver")]
public sealed class ScripterTests(SqlServerFixture fixture)
{
    private readonly SqlServerProvider provider = new();

    private const string Base = """
        CREATE SCHEMA sales;
        GO
        CREATE TYPE dbo.Phone FROM varchar(20) NULL;
        CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL PRIMARY KEY);
        CREATE SEQUENCE dbo.seq AS int START WITH 1 INCREMENT BY 1;
        CREATE PARTITION FUNCTION pf (date) AS RANGE RIGHT FOR VALUES ('2026-01-01');
        CREATE PARTITION SCHEME ps AS PARTITION pf ALL TO ([PRIMARY]);
        CREATE TABLE dbo.Customer (
            Id int IDENTITY(100, 5) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
            Name nvarchar(100) NOT NULL,
            Email nvarchar(100) NULL,
            Old int NULL,
            Code char(3) NOT NULL CONSTRAINT DF_Customer_Code DEFAULT ('ABC'));
        CREATE TABLE dbo.[Order] (
            Id int NOT NULL PRIMARY KEY,
            CustomerId int NOT NULL,
            Total decimal(10, 2) NOT NULL DEFAULT (0),
            Note nvarchar(50) NULL DEFAULT (N'{x}'),
            CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (Id),
            CHECK (Total >= 0));
        CREATE INDEX IX_Order_Customer ON dbo.[Order] (CustomerId);
        CREATE TABLE dbo.Gone (Id int NOT NULL, Payload nvarchar(max) NULL);
        CREATE SYNONYM dbo.syn FOR dbo.Customer;
        GO
        CREATE VIEW dbo.vCustomer AS SELECT Id, Name FROM dbo.Customer;
        GO
        CREATE PROCEDURE dbo.usp_Old AS SELECT 1 AS one;
        """;

    private const string Edits = """
        ALTER TABLE dbo.Customer ALTER COLUMN Email nvarchar(200) NULL;
        ALTER TABLE dbo.Customer ADD Phone dbo.Phone NULL, Created datetime2(3) NOT NULL CONSTRAINT DF_Customer_Created DEFAULT (sysutcdatetime());
        ALTER TABLE dbo.Customer DROP COLUMN Old;
        EXEC sp_rename 'dbo.Customer.Code', 'CountryCode', 'COLUMN';
        ALTER TABLE dbo.[Order] DROP CONSTRAINT IF EXISTS FK_Order_Customer;
        DECLARE @d sysname = (SELECT name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID('dbo.[Order]') AND COL_NAME(parent_object_id, parent_column_id) = 'Total');
        DECLARE @c sysname = (SELECT name FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.[Order]'));
        EXEC ('ALTER TABLE dbo.[Order] DROP CONSTRAINT ' + @d + ', ' + @c);
        ALTER TABLE dbo.[Order] ALTER COLUMN Total decimal(19, 4) NOT NULL;
        ALTER TABLE dbo.[Order] ADD DEFAULT (0) FOR Total, CHECK (Total >= 0), CONSTRAINT FK_Order_Customer FOREIGN KEY (CustomerId) REFERENCES dbo.Customer (Id) ON DELETE CASCADE;
        DROP INDEX IX_Order_Customer ON dbo.[Order];
        CREATE INDEX IX_Order_Customer ON dbo.[Order] (CustomerId, Id);
        CREATE INDEX IX_Order_Total ON dbo.[Order] (Total DESC) INCLUDE (CustomerId) WHERE Total > 0;
        DROP TABLE dbo.Gone;
        CREATE TABLE sales.Region (
            Code char(2) NOT NULL CONSTRAINT PK_Region PRIMARY KEY,
            Name nvarchar(50) NOT NULL CONSTRAINT UQ_Region_Name UNIQUE,
            Label AS (Code + N'-' + Name));
        ALTER TABLE dbo.[Order] ADD RegionCode char(2) NULL CONSTRAINT FK_Order_Region REFERENCES sales.Region (Code);
        CREATE TABLE dbo.Sale (SaleDate date NOT NULL, Id int NOT NULL, CONSTRAINT PK_Sale PRIMARY KEY (SaleDate, Id)) ON ps (SaleDate);
        CREATE TABLE dbo.Price (
            Id int NOT NULL CONSTRAINT PK_Price PRIMARY KEY,
            Amount money NOT NULL,
            ValidFrom datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
            ValidTo datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
            PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
        WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PriceHistory, HISTORY_RETENTION_PERIOD = 6 MONTHS));
        ALTER SEQUENCE dbo.seq INCREMENT BY 5;
        DROP SYNONYM dbo.syn;
        CREATE SYNONYM dbo.syn FOR dbo.[Order];
        CREATE SEQUENCE sales.ticket AS bigint START WITH 1000 INCREMENT BY 1 CACHE 50;
        ALTER PARTITION SCHEME ps NEXT USED [PRIMARY];
        ALTER PARTITION FUNCTION pf() SPLIT RANGE ('2027-01-01');
        DROP TYPE dbo.IdList;
        CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL PRIMARY KEY, Weight int NULL);
        DROP PROCEDURE dbo.usp_Old;
        GO
        CREATE OR ALTER VIEW dbo.vCustomer AS SELECT Id, Name, Email FROM dbo.Customer;
        GO
        CREATE FUNCTION dbo.fnTotal (@id int) RETURNS decimal(19, 4) AS BEGIN RETURN (SELECT SUM(Total) FROM dbo.[Order] WHERE CustomerId = @id) END;
        GO
        CREATE VIEW dbo.vTotals AS SELECT Id, dbo.fnTotal(Id) AS Total FROM dbo.Customer;
        GO
        CREATE TRIGGER dbo.trOrder ON dbo.[Order] AFTER INSERT AS SET NOCOUNT ON;
        """;

    [SkippableFact]
    public async Task Deploy_turns_a_into_b_and_revert_turns_it_back()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        await using var a = await DatabaseAsync();
        await using var b = await DatabaseAsync();
        await RunAsync(a, Base);
        await RunAsync(b, Base);
        await RunAsync(b, Edits);
        var before = await DumpAsync(a);
        var target = await DumpAsync(b);

        var script = await ScriptAsync(a, b);

        Assert.Contains(script.Summary, s => s == "~ column dbo.Customer.Code renamed to CountryCode");
        Assert.Contains(script.Summary, s => s == "- table dbo.Gone");
        Assert.Contains(script.Summary, s => s == "+ table dbo.Price");
        Assert.Contains(script.Summary, s => s.StartsWith("~ column dbo.Order.Total: decimal(10,2)", StringComparison.Ordinal));
        Assert.Contains(script.Summary, s => s == "+ partition boundary pf 2027-01-01");
        Assert.True(script.DataLoss);
        Assert.Contains(script.Warnings, w => w.Contains("drops table dbo.Gone", StringComparison.Ordinal));
        Assert.Contains(script.Warnings, w => w.Contains("dbo.Customer: column Code was replaced by CountryCode", StringComparison.Ordinal));

        await RunAsync(a, script.Deploy);
        Assert.Equal(target, await DumpAsync(a));

        var again = await ScriptAsync(a, b);
        Assert.Equal(string.Empty, again.Deploy);

        // Re-added columns go to the end of the table (SQL Server cannot insert them in place), so compare without positions.
        await RunAsync(a, script.Revert);
        Assert.Equal(WithoutColumnPositions(before), WithoutColumnPositions(await DumpAsync(a)));
    }

    private static string WithoutColumnPositions(string dump) =>
        string.Join('\n', dump.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^column\|([^|]*)\|\d+\|", "column|$1|#|")).Order(StringComparer.Ordinal));

    [SkippableFact]
    public async Task System_named_constraints_are_dropped_by_lookup_on_any_server()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        await using var a = await DatabaseAsync();
        await using var b = await DatabaseAsync();
        await using var c = await DatabaseAsync();
        const string table = "CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, Code int NOT NULL UNIQUE, Flag bit NOT NULL DEFAULT (0), Qty int NOT NULL CHECK (Qty > 0), ParentId int NULL REFERENCES dbo.T (Id));";
        await RunAsync(a, table);
        await RunAsync(c, table);
        await RunAsync(b, "CREATE TABLE dbo.T (Id int NOT NULL, Code int NOT NULL, Flag bit NOT NULL, Qty int NOT NULL, ParentId int NULL);");

        // Scripted from a, run on c: the system-generated names differ between the two databases.
        var script = await ScriptAsync(a, b);
        await RunAsync(c, script.Deploy);
        Assert.Equal(await DumpAsync(b), await DumpAsync(c));

        await RunAsync(c, script.Revert);
        Assert.Equal(await DumpAsync(a), await DumpAsync(c));
    }

    [SkippableFact]
    public async Task Repeatable_objects_and_journal_tables_are_left_out()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        await using var a = await DatabaseAsync();
        await using var b = await DatabaseAsync();
        await RunAsync(a, "CREATE TABLE dbo.T (Id int NOT NULL);\nGO\nCREATE VIEW dbo.v AS SELECT Id FROM dbo.T;");
        await RunAsync(b, "CREATE TABLE dbo.T (Id int NOT NULL, Name int NULL);\nGO\nCREATE VIEW dbo.v AS SELECT Id, Name FROM dbo.T;\nGO\nCREATE TABLE dbo.flyway_schema_history (installed_rank int NOT NULL);\nGO\nCREATE SCHEMA bsm;\nGO\nCREATE TABLE bsm.changes (x int);");

        var script = await provider.Scripter.ScriptAsync(await SessionAsync(a), await SessionAsync(b), new SchemaScriptOptions
        {
            StateSchema = "bsm",
            Exclude = ["*.flyway_schema_history"],
            RepeatableScripts = ["-- view\nCREATE OR ALTER VIEW [dbo].[v] AS SELECT 1 AS x;"],
        }, CancellationToken.None);

        Assert.Equal(["+ column dbo.T.Name int"], script.Summary);
        Assert.Contains(script.Warnings, w => w.Contains("dbo.v is defined by a repeatable script", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeatable_scripts_own_the_modules_they_create()
    {
        var owned = SqlServerScripter.OwnedModules([
            "CREATE OR ALTER VIEW dbo.vA AS SELECT 1;",
            "create procedure [sales].[usp B] as select 1",
            "CREATE FUNCTION fnC() RETURNS int AS BEGIN RETURN 1 END",
            "-- CREATE TABLE dbo.NotAModule (Id int)",
        ]);
        Assert.True(owned.SetEquals(["dbo.va", "sales.usp b", "dbo.fnc"]), string.Join(", ", owned));
    }

    private async Task<SchemaScript> ScriptAsync(TestDatabase from, TestDatabase to)
    {
        await using var a = await SessionAsync(from);
        await using var b = await SessionAsync(to);
        return await provider.Scripter.ScriptAsync(a, b, new SchemaScriptOptions { StateSchema = "bsm" }, CancellationToken.None);
    }

    private async Task<string> DumpAsync(TestDatabase db)
    {
        await using var session = await SessionAsync(db);
        return await provider.Schema.CaptureAsync(session, "bsm", CancellationToken.None);
    }

    private async Task RunAsync(TestDatabase db, string sql)
    {
        await using var session = await SessionAsync(db);
        foreach (var batch in provider.Parser.Split(sql))
        {
            try
            {
                await session.ExecuteAsync(batch.Text, null, CancellationToken.None);
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException($"{ex.Message}\n--- batch ---\n{batch.Text}", ex);
            }
        }
    }

    private Task<IDatabaseSession> SessionAsync(TestDatabase db) => provider.OpenSessionAsync(db.Environment, SessionContext.Empty, CancellationToken.None);

    private async Task<TestDatabase> DatabaseAsync()
    {
        var name = "bsm_sc_" + Guid.NewGuid().ToString("N")[..12];
        await using var master = new SqlConnection(fixture.MasterConnectionString);
        await master.OpenAsync();
        await using var create = new SqlCommand($"CREATE DATABASE [{name}];", master);
        await create.ExecuteNonQueryAsync();
        return new TestDatabase(fixture.MasterConnectionString!, name);
    }

    private sealed class TestDatabase(string master, string name) : IAsyncDisposable
    {
        public DeploymentEnvironment Environment { get; } = new()
        {
            Name = "test",
            ConnectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = name }.ConnectionString,
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
}
