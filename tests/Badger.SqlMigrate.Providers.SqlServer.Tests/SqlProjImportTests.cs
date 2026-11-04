using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Import;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

/// <summary>Importing an SSDT project: the plan's shape, then deploying it and baselining a published database.</summary>
[Collection("sqlserver")]
public sealed class SqlProjImportTests(SqlServerFixture fixture) : IDisposable
{
    private readonly SqlServerProvider provider = new();
    private readonly string root = Path.Combine(Path.GetTempPath(), "bsm-sqlproj", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private ImportedProject Import(Func<string, string> write)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        return SqlProjImporter.Import(new SqlProjImportOptions { Path = write(source), Parser = new TSqlObjectParser() });
    }

    private MigrationProject Write(ImportedProject imported)
    {
        var dir = Path.Combine(root, "bsm");
        imported.WriteScripts(dir);
        var project = MigrationProject.Load(dir, provider.Id);
        Assert.Empty(project.Problems);
        return project;
    }

    [Fact]
    public void Objects_become_changes_and_repeatables_in_dependency_order()
    {
        var imported = Import(SqlProjSample.WriteClassic);
        var names = imported.Changes.Select(c => c.Name).ToList();

        Assert.Equal(
        [
            "create-schema-sales", "create-role-appreader", "create-type-dbo-phone", "create-sequence-sales-ordernumber",
            "create-dbo-country", "create-dbo-customer", "create-sales-order", "create-sales-orderline", "add-foreign-keys-dbo-customer",
        ], names);
        int At(string name) => names.IndexOf(name);
        Assert.True(At("create-sales-order") > At("create-dbo-customer"));

        var customer = imported.Changes.Single(c => c.Name == "create-dbo-customer");
        Assert.Equal(["create-type-dbo-phone", "create-dbo-country"], customer.Requires);
        Assert.DoesNotContain("FK_Customer_LastOrder", customer.Deploy, StringComparison.Ordinal);
        Assert.Contains("FK_Customer_Country", customer.Deploy, StringComparison.Ordinal);
        Assert.Contains("IX_Customer_Name", customer.Deploy, StringComparison.Ordinal);
        Assert.Contains("-- Customers.", customer.Deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", customer.Deploy, StringComparison.Ordinal);
        Assert.Equal("DROP TABLE [dbo].[Customer];\n", customer.Revert);
        Assert.Equal("sqlproj Tables/Customer.sql: table dbo.Customer", customer.Note);

        var order = imported.Changes.Single(c => c.Name == "create-sales-order");
        Assert.Equal(["create-schema-sales", "create-sequence-sales-ordernumber", "create-dbo-customer"], order.Requires);
        Assert.DoesNotContain("TRIGGER", order.Deploy, StringComparison.Ordinal);

        var fks = imported.Changes.Single(c => c.Name == "add-foreign-keys-dbo-customer");
        Assert.Equal("ALTER TABLE [dbo].[Customer] ADD CONSTRAINT [FK_Customer_LastOrder] FOREIGN KEY ([LastOrderId]) REFERENCES [Sales].[Order] ([Id]);\n", fks.Deploy);
        Assert.Equal("ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [FK_Customer_LastOrder];\n", fks.Revert);
        Assert.Equal(["create-dbo-customer", "create-sales-order"], fks.Requires.Order(StringComparer.Ordinal));

        var repeatables = imported.Repeatables.ToDictionary(r => r.Path, r => r.Script);
        Assert.Equal(
        [
            "functions/Sales.fnOrderTotal.sql", "permissions/Permissions.sql", "post-deploy/Script.PostDeployment.sql", "procedures/dbo.usp_GetBigCustomers.sql",
            "triggers/Sales.trOrder_Insert.sql", "views/Sales.vOrderTotals.sql", "views/dbo.vBigCustomers.sql",
        ], repeatables.Keys.Order(StringComparer.Ordinal));
        Assert.StartsWith("-- bsm:requires views/Sales.vOrderTotals.sql\nGO\nCREATE OR ALTER VIEW [dbo].[vBigCustomers]", repeatables["views/dbo.vBigCustomers.sql"], StringComparison.Ordinal);
        Assert.StartsWith("-- bsm:requires functions/Sales.fnOrderTotal.sql\nGO\nCREATE OR ALTER VIEW", repeatables["views/Sales.vOrderTotals.sql"], StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON [dbo].[usp_GetBigCustomers]", repeatables["procedures/dbo.usp_GetBigCustomers.sql"], StringComparison.Ordinal);
        Assert.StartsWith("CREATE OR ALTER TRIGGER [Sales].[trOrder_Insert]", repeatables["triggers/Sales.trOrder_Insert.sql"], StringComparison.Ordinal);
        Assert.StartsWith("-- bsm:requires views/Sales.vOrderTotals.sql\nGO\nGRANT SELECT", repeatables["permissions/Permissions.sql"], StringComparison.Ordinal);

        var post = repeatables["post-deploy/Script.PostDeployment.sql"];
        Assert.Contains("MERGE dbo.Country", post, StringComparison.Ordinal);
        Assert.Contains("-- :r .\\Seed\\Countries.sql", post, StringComparison.Ordinal);
        Assert.Contains("$(Environment)", post, StringComparison.Ordinal);

        Assert.Equal("dev", imported.Variables["Environment"]);
        Assert.Empty(imported.UndefinedVariables);
        Assert.Contains(imported.Warnings, w => w.Contains("pre-deployment script was not imported", StringComparison.Ordinal));
        Assert.Contains(imported.Warnings, w => w.Contains("foreign keys form a cycle", StringComparison.Ordinal));
        Assert.DoesNotContain(imported.Warnings, w => w.Contains("master.dacpac", StringComparison.Ordinal));
        Assert.DoesNotContain(imported.Warnings, w => w.Contains("does not exist", StringComparison.Ordinal));
        Write(imported);
    }

    [Fact]
    public void Sdk_style_projects_build_every_script_except_removed_ones()
    {
        var sdk = Import(SqlProjSample.WriteSdkStyle);
        Directory.Delete(Path.Combine(root, "source"), recursive: true);
        var classic = Import(SqlProjSample.WriteClassic);

        Assert.Equal(classic.Changes.Select(c => (c.Name, c.Deploy)), sdk.Changes.Select(c => (c.Name, c.Deploy)));
        Assert.Equal(classic.Repeatables.Select(r => r.Path).Order(StringComparer.Ordinal), sdk.Repeatables.Select(r => r.Path).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(sdk.Changes, c => c.Deploy.Contains("Junk", StringComparison.Ordinal) || c.Deploy.Contains("SELECT 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Functions_a_table_needs_become_changes()
    {
        var imported = Import(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "fn.sql"), "CREATE FUNCTION dbo.Twice (@x int) RETURNS int WITH SCHEMABINDING AS BEGIN RETURN @x * 2 END");
            File.WriteAllText(Path.Combine(dir, "t.sql"), "CREATE TABLE dbo.T (A int NOT NULL, B AS dbo.Twice(A));");
            File.WriteAllText(Path.Combine(dir, "v.sql"), "CREATE VIEW dbo.V AS SELECT A FROM dbo.T;");
            File.WriteAllText(Path.Combine(dir, "p.sqlproj"), "<Project Sdk=\"Microsoft.Build.Sql/1.0.0\"><ItemGroup><Content Include=\"x.sql\" /></ItemGroup></Project>");
            return Path.Combine(dir, "p.sqlproj");
        });

        Assert.Equal(["create-function-dbo-twice", "create-dbo-t"], imported.Changes.Select(c => c.Name));
        Assert.StartsWith("CREATE FUNCTION", imported.Changes[0].Deploy, StringComparison.Ordinal);
        Assert.Equal("DROP FUNCTION dbo.Twice;\n", imported.Changes[0].Revert);
        Assert.Equal(["views/dbo.V.sql"], imported.Repeatables.Select(r => r.Path));
        Assert.Contains(imported.Warnings, w => w.Contains("is a change, not a repeatable", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Deploying_the_import_matches_a_published_database_and_reverts_cleanly()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        var imported = Import(SqlProjSample.WriteClassic);
        var project = Write(imported);
        await using var deployed = await DatabaseAsync();
        await using var published = await DatabaseAsync();
        await RunAsync(published, SqlProjSample.PublishScript());

        await using (var session = await provider.OpenSessionAsync(deployed.Environment, SessionContext.Empty, CancellationToken.None))
        {
            var engine = Engine(project, deployed, session, imported);
            var result = await engine.DeployAsync(new DeployOptions());
            Assert.Equal(imported.Changes.Count, result.Deployed.Count);
            Assert.Equal(imported.Repeatables.Count, result.Repeatables.Count);
        }

        Assert.Equal(await DumpAsync(published), await DumpAsync(deployed));
        Assert.Equal(2, await ScalarAsync(deployed, "SELECT COUNT(*) FROM dbo.Country"));

        // The generated reverts run: back to no tables. (The schema stays: repeatables in it are not reverted.)
        await using (var session = await provider.OpenSessionAsync(deployed.Environment, SessionContext.Empty, CancellationToken.None))
        {
            await Engine(project, deployed, session, imported).RevertAsync(new RevertOptions { To = "create-schema-sales" });
        }

        Assert.Equal(0, await ScalarAsync(deployed, "SELECT COUNT(*) FROM sys.tables t WHERE SCHEMA_NAME(t.schema_id) <> 'bsm'"));
        Assert.Equal(0, await ScalarAsync(deployed, "SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1"));
    }

    [SkippableFact]
    public async Task Baselining_a_published_database_leaves_nothing_to_deploy()
    {
        Skip.If(fixture.MasterConnectionString is null, fixture.UnavailableReason);
        var imported = Import(SqlProjSample.WriteClassic);
        var project = Write(imported);
        await using var published = await DatabaseAsync();
        await RunAsync(published, SqlProjSample.PublishScript());

        // An older version of one view, as in an environment published from an earlier release.
        await RunAsync(published, "ALTER VIEW [Sales].[vOrderTotals] AS SELECT o.Id, o.CustomerId, CAST(0 AS decimal(19, 4)) AS Total FROM Sales.[Order] o;");

        await using var session = await provider.OpenSessionAsync(published.Environment, SessionContext.Empty, CancellationToken.None);
        var engine = Engine(project, published, session, imported);
        var (present, missing) = SqlProjImporter.CheckTables(project, await SqlServerCatalog.ReadTableNamesAsync(session, CancellationToken.None));
        Assert.Empty(missing);
        Assert.Equal(4, present.Count);

        var target = SqlProjImporter.BaselineTarget(project);
        Assert.Equal("add-foreign-keys-dbo-customer", target);

        // Baseline matches modules by definition: all of them minus the edited view.
        var matching = RepeatableDefinitions.Matching(project, await SqlServerCatalog.ReadModuleDefinitionsAsync(session, CancellationToken.None), r => engine.Render(r.Name, "repeatable", r.Script).Text);
        Assert.Equal(["functions/Sales.fnOrderTotal.sql", "procedures/dbo.usp_GetBigCustomers.sql", "triggers/Sales.trOrder_Insert.sql", "views/dbo.vBigCustomers.sql"], matching.Order(StringComparer.Ordinal));

        await engine.BaselineAsync(target!);
        await engine.BaselineRepeatablesAsync(matching.ToHashSet(StringComparer.Ordinal));

        var status = await engine.StatusAsync();
        Assert.True(status.UpToDate);
        Assert.True(status.Certified);
        var dryRun = await engine.DeployAsync(new DeployOptions { DryRun = true });
        Assert.Empty(dryRun.Deployed);
        // The edited view, plus the permission and post-deployment scripts: they may be newer than what the database
        // was published with, and they are safe to run again.
        var rerun = project.Repeatables.Select(r => r.Name).Where(n => n.StartsWith("permissions/", StringComparison.Ordinal) || n.StartsWith("post-deploy/", StringComparison.Ordinal)).Append("views/Sales.vOrderTotals.sql");
        Assert.Contains(dryRun.Repeatables, n => n.StartsWith("post-deploy/", StringComparison.Ordinal));
        Assert.Equal(rerun.Order(StringComparer.Ordinal), dryRun.Repeatables.Order(StringComparer.Ordinal));
    }

    private MigrationEngine Engine(MigrationProject project, TestDatabase db, IDatabaseSession session, ImportedProject imported) =>
        new(project, provider, db.Environment, session, new Actor("test", "test", "0.0.0"), null, imported.Variables);

    private async Task<string> DumpAsync(TestDatabase db)
    {
        await using var session = await provider.OpenSessionAsync(db.Environment, SessionContext.Empty, CancellationToken.None);
        return await provider.Schema.CaptureAsync(session, "bsm", CancellationToken.None);
    }

    private static async Task<int> ScalarAsync(TestDatabase db, string sql)
    {
        await using var connection = new SqlConnection(db.Environment.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RunAsync(TestDatabase db, string sql)
    {
        await using var session = await provider.OpenSessionAsync(db.Environment, SessionContext.Empty, CancellationToken.None);
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

    private async Task<TestDatabase> DatabaseAsync()
    {
        var name = "bsm_sp_" + Guid.NewGuid().ToString("N")[..12];
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
