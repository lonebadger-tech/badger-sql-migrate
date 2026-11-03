using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Tests;

public sealed class EnvironmentTests : IDisposable
{
    private readonly TestProject project = new();
    private readonly FakeDatabase db = new();

    public void Dispose() => project.Dispose();

    private MigrationEngine Engine(string env = "dev", bool allowRevert = true, bool claim = false, FakeProvider? provider = null) =>
        new(project.Load("fake", env), provider ?? new FakeProvider(), new DeploymentEnvironment { Name = env, AllowRevert = allowRevert }, new FakeSession(db), new Actor("t", "h", "1"))
        {
            AllowEnvironmentMismatch = claim,
        };

    [Fact]
    public async Task First_deploy_records_the_environment_and_other_environments_are_refused()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine("test").DeployAsync(new DeployOptions());
        Assert.Equal("test", db.Meta[MigrationEngine.EnvironmentMetaKey]);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine("prod").DeployAsync(new DeployOptions()));
        Assert.Contains("belongs to environment 'test'", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<MigrationException>(() => Engine("prod").RevertAsync(new RevertOptions { All = true }));
        await Assert.ThrowsAsync<MigrationException>(() => Engine("prod").DeployAsync(new DeployOptions { DryRun = true }));
    }

    [Fact]
    public async Task Claiming_re_records_the_environment()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine("prod").DeployAsync(new DeployOptions());

        await Engine("test", claim: true).DeployAsync(new DeployOptions());

        Assert.Equal("test", db.Meta[MigrationEngine.EnvironmentMetaKey]);
    }

    [Fact]
    public async Task Revert_is_refused_when_the_environment_disallows_it()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine("prod", allowRevert: false).DeployAsync(new DeployOptions());

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine("prod", allowRevert: false).RevertAsync(new RevertOptions { All = true }));
        Assert.Contains("does not allow revert", ex.Message, StringComparison.Ordinal);
        Assert.Contains("A", db.Objects);
    }

    [Fact]
    public async Task Events_record_the_environment()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine("qa").DeployAsync(new DeployOptions());
        Assert.All(db.Events, e => Assert.Equal("qa", e.Environment));
    }

    [Fact]
    public async Task Environment_scripts_run_only_in_their_environment_after_shared_repeatables()
    {
        project.Change("a", "CREATE A", "DROP A").Repeatable("views/v.sql", "PRINT v");
        Write("environments/dev/seed.sql", "-- badger:requires views/v.sql\nCREATE SEED");
        Write("environments/prod/grants.sql", "CREATE GRANTS");

        var dev = await Engine("dev").DeployAsync(new DeployOptions());
        Assert.Equal(["views/v.sql", "@dev/seed.sql"], dev.Repeatables);
        Assert.Contains("SEED", db.Objects);
        Assert.DoesNotContain("GRANTS", db.Objects);
        Assert.Empty(project.Load("fake", "dev").Problems);
    }

    [Fact]
    public async Task Environment_scripts_do_not_change_the_plan_certificate()
    {
        project.Change("a", "CREATE A", "DROP A");
        Write("environments/dev/seed.sql", "CREATE SEED");
        Assert.Equal(project.Load("fake", "dev").HeadChainId, project.Load("fake", "prod").HeadChainId);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Resources_are_provisioned_before_changes_and_conflicts_stop_the_deploy()
    {
        var provider = new FakeProvider { FakeResources = new FakeResources("CMK", "CEK") };
        project.Change("a", "CREATE A", "DROP A");

        await Engine(provider: provider).DeployAsync(new DeployOptions());
        Assert.Contains("RESOURCE:CMK", db.Objects);
        Assert.Contains("RESOURCE:CEK", db.Objects);
        Assert.Equal(2, db.Events.Count(e => e.Kind == MigrationEventKind.Resource));

        var fresh = new FakeDatabase();
        fresh.Objects.Add("CONFLICT:CMK");
        var engine = new MigrationEngine(project.Load("fake"), provider, new DeploymentEnvironment { Name = "dev" }, new FakeSession(fresh), new Actor("t", "h", "1"));
        var ex = await Assert.ThrowsAsync<MigrationException>(() => engine.DeployAsync(new DeployOptions()));
        Assert.Contains("conflict", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("A", fresh.Objects);
    }

    [Fact]
    public async Task Destructive_resource_actions_need_explicit_consent()
    {
        var provider = new FakeProvider { FakeResources = new FakeResources("CEK") };
        project.Change("a", "CREATE A", "DROP A");
        db.Objects.Add("OLD:CMK1");

        await Engine(provider: provider).DeployAsync(new DeployOptions());
        Assert.Contains("OLD:CMK1", db.Objects);

        Assert.Empty(await Engine(provider: provider).ProvisionAsync(dryRun: false, allowDestructive: false));
        var applied = await Engine(provider: provider).ProvisionAsync(dryRun: false, allowDestructive: true);
        Assert.Single(applied);
        Assert.DoesNotContain("OLD:CMK1", db.Objects);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(project.Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
