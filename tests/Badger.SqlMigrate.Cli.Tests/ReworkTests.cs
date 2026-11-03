using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Cli.Tests;

public sealed class ReworkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bsm-rework-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private GlobalOptions G => new(root, null, null, [], false, new ConsoleOutput(false, false, true));

    [Theory]
    [InlineData("proc", "proc-v2")]
    [InlineData("proc-v2", "proc-v3")]
    [InlineData("proc-v9", "proc-v10")]
    [InlineData("a-very-long-v-name", "a-very-long-v-name-v2")]
    public void Next_revision_name(string name, string expected) => Assert.Equal(expected, Commands.NextRevisionName(name, _ => false));

    [Fact]
    public void Next_revision_name_skips_taken_names() => Assert.Equal("proc-v4", Commands.NextRevisionName("proc", n => n is "proc-v2" or "proc-v3"));

    [Fact]
    public async Task Rework_copies_the_deploy_script_and_reverts_to_it()
    {
        Assert.Equal(0, Commands.Init(G, "sqlserver"));
        Assert.Equal(0, Commands.Add(G, "get-customer", [], null, noTx: true, irreversible: false));
        File.WriteAllText(Path.Combine(root, "changes/get-customer/deploy.sql"), "CREATE OR ALTER PROCEDURE dbo.GetCustomer AS SELECT 1;\n");

        Assert.Equal(0, Commands.Rework(G, "get-customer", null, null));

        var dir = Path.Combine(root, "changes/get-customer-v2");
        Assert.Equal("CREATE OR ALTER PROCEDURE dbo.GetCustomer AS SELECT 1;\n", File.ReadAllText(Path.Combine(dir, "deploy.sql")));
        Assert.EndsWith("CREATE OR ALTER PROCEDURE dbo.GetCustomer AS SELECT 1;\n", File.ReadAllText(Path.Combine(dir, "revert.sql")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "verify.sql")));

        var plan = PlanFile.Load(Path.Combine(root, PlanFile.FileName));
        var change = plan.Changes[^1];
        Assert.Equal("get-customer-v2", change.Name);
        Assert.Equal(["get-customer"], change.Requires);
        Assert.Equal(ChangeFlags.NoTransaction, change.Flags);
        Assert.Equal("rework of get-customer", change.Note);

        Assert.Equal(1, await Commands.Run(G, g => Commands.Rework(g, "missing", null, null)));
        Assert.Equal(1, await Commands.Run(G, g => Commands.Rework(g, "get-customer", "get-customer-v2", null)));
        Assert.Equal(1, await Commands.Run(G with { Bundle = "x.zip" }, g => Commands.Rework(g, "get-customer", null, null)));
    }
}
