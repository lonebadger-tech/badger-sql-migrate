using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Xunit;

namespace Badger.SqlMigrate.Providers.Testing;

/// <summary>Vendor SQL the conformance tests need. Each provider's test project supplies one.</summary>
public interface IConformanceDialect
{
    string CreateTable(string name);

    string DropTable(string name);

    /// <summary>A statement that fails at execution time.</summary>
    string Failing { get; }

    /// <summary>A repeatable object definition (e.g. CREATE OR ALTER VIEW) that selects <paramref name="value"/>.</summary>
    string Repeatable(string name, int value);

    /// <summary>A script that raises an error unless table <paramref name="name"/> exists.</summary>
    string VerifyTableExists(string name);
}

/// <summary>A live database for one test, dropped on dispose.</summary>
public interface IConformanceDatabase : IAsyncDisposable
{
    DeploymentEnvironment Profile { get; }
}

/// <summary>
/// Behaviour every provider must have. A new vendor is "done" when a subclass of this passes against a real
/// server (typically a Testcontainers fixture). Tests skip when the server cannot be started.
/// </summary>
public abstract class ProviderConformanceTests
{
    protected abstract IDatabaseProvider Provider { get; }

    protected abstract IConformanceDialect Dialect { get; }

    /// <summary>Returns null when no server is available (tests are skipped).</summary>
    protected abstract Task<IConformanceDatabase?> CreateDatabaseAsync();

    [SkippableFact]
    public async Task Deploy_revert_redeploy_is_certified_and_fingerprints_match()
    {
        await using var db = await RequireDatabaseAsync();
        using var project = NewProject();
        project.Change("t1", Dialect.CreateTable("t1"), Dialect.DropTable("t1"), Dialect.VerifyTableExists("t1"));
        project.Change("t2", Dialect.CreateTable("t2"), Dialect.DropTable("t2"));

        await using (var e = await EngineAsync(db, project))
        {
            Assert.Equal(["t1", "t2"], (await e.Engine.DeployAsync(new DeployOptions())).Deployed);
        }

        string? head;
        string? fingerprint;
        await using (var e = await EngineAsync(db, project))
        {
            var verify = await e.Engine.VerifyAsync();
            Assert.True(verify.Ok, string.Join("\n", verify.Problems));
            Assert.True(verify.Status.Certified);
            head = verify.Status.DatabaseHeadChainId;
            fingerprint = verify.LiveFingerprint;
            Assert.Equal(project.Load(Provider.Id).HeadChainId, head);

            Assert.Equal(["t2", "t1"], (await e.Engine.RevertAsync(new RevertOptions { All = true })).Reverted);
            Assert.Equal(["t1", "t2"], (await e.Engine.DeployAsync(new DeployOptions())).Deployed);

            var again = await e.Engine.VerifyAsync();
            Assert.Equal(head, again.Status.DatabaseHeadChainId);
            Assert.Equal(fingerprint, again.LiveFingerprint);
        }
    }

    [SkippableFact]
    public async Task Failed_change_leaves_no_trace_when_ddl_is_transactional()
    {
        await using var db = await RequireDatabaseAsync();
        Skip.IfNot(Provider.Capabilities.TransactionalDdl, "provider has no transactional DDL");
        using var project = NewProject();
        project.Change("good", Dialect.CreateTable("good"), Dialect.DropTable("good"));
        project.Change("bad", Dialect.CreateTable("partial") + "\n" + Dialect.Failing, Dialect.DropTable("partial"));

        await using var e = await EngineAsync(db, project);
        await Assert.ThrowsAsync<MigrationException>(() => e.Engine.DeployAsync(new DeployOptions()));

        var status = await e.Engine.StatusAsync();
        Assert.Equal(["good"], status.Applied.Select(a => a.Name));
        Assert.Equal(MigrationEventKind.Fail, (await e.Engine.LogAsync(1))[0].Kind);

        // "partial" must not exist: deploying a fixed change that creates it again succeeds.
        project.Write("changes/bad/deploy.sql", Dialect.CreateTable("partial"));
        await using var e2 = await EngineAsync(db, project);
        Assert.Equal(["bad"], (await e2.Engine.DeployAsync(new DeployOptions())).Deployed);
    }

    [SkippableFact]
    public async Task Revert_works_from_stored_scripts_after_the_change_left_the_working_copy()
    {
        await using var db = await RequireDatabaseAsync();
        using var project = NewProject();
        project.Change("keep", Dialect.CreateTable("keep"), Dialect.DropTable("keep"));
        project.Change("gone", Dialect.CreateTable("gone"), Dialect.DropTable("gone"));
        await using (var e = await EngineAsync(db, project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
        }

        project.DeleteChange("gone");
        project.WritePlan("keep\n");
        await using (var e = await EngineAsync(db, project))
        {
            Assert.Equal(["gone"], (await e.Engine.StatusAsync()).Orphans.Select(o => o.Name));
            Assert.Equal(["gone"], (await e.Engine.RevertAsync(new RevertOptions { To = "keep" })).Reverted);
            Assert.True((await e.Engine.StatusAsync()).UpToDate);
        }
    }

    [SkippableFact]
    public async Task Deploy_lock_blocks_a_concurrent_deployer()
    {
        await using var db = await RequireDatabaseAsync();
        Skip.IfNot(Provider.Capabilities.SupportsDeployLock, "provider has no deploy lock");
        await using var first = await Provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await using var second = await Provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);

        var held = await Provider.Lock.AcquireAsync(first, db.Profile.StateSchema, TimeSpan.FromSeconds(5), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<MigrationException>(() => Provider.Lock.AcquireAsync(second, db.Profile.StateSchema, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Contains("deploy lock", ex.Message, StringComparison.Ordinal);

        await held.DisposeAsync();
        await using var acquired = await Provider.Lock.AcquireAsync(second, db.Profile.StateSchema, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [SkippableFact]
    public async Task Verify_detects_changes_made_outside_badger()
    {
        await using var db = await RequireDatabaseAsync();
        using var project = NewProject();
        project.Change("t1", Dialect.CreateTable("t1"), Dialect.DropTable("t1"));
        await using var e = await EngineAsync(db, project);
        await e.Engine.DeployAsync(new DeployOptions());

        await e.Session.ExecuteAsync(Dialect.CreateTable("hotfix"), null, CancellationToken.None);

        var result = await e.Engine.VerifyAsync();
        Assert.True(result.Drifted);
        Assert.False(result.Ok);
    }

    [SkippableFact]
    public async Task Repeatables_are_applied_and_reapplied_on_change()
    {
        await using var db = await RequireDatabaseAsync();
        using var project = NewProject();
        project.Change("t1", Dialect.CreateTable("t1"), Dialect.DropTable("t1"));
        project.Repeatable("views/v1.sql", Dialect.Repeatable("v1", 1));

        await using (var e = await EngineAsync(db, project))
        {
            Assert.Equal(["views/v1.sql"], (await e.Engine.DeployAsync(new DeployOptions())).Repeatables);
            Assert.Empty((await e.Engine.DeployAsync(new DeployOptions())).Repeatables);
        }

        project.Repeatable("views/v1.sql", Dialect.Repeatable("v1", 2));
        await using (var e = await EngineAsync(db, project))
        {
            Assert.Equal(["views/v1.sql"], (await e.Engine.DeployAsync(new DeployOptions())).Repeatables);
        }
    }

    private async Task<IConformanceDatabase> RequireDatabaseAsync()
    {
        var db = await CreateDatabaseAsync();
        Skip.If(db is null, "No database server available for conformance tests.");
        return db!;
    }

    private static ConformanceProject NewProject() => new();

    private async Task<EngineHandle> EngineAsync(IConformanceDatabase db, ConformanceProject project)
    {
        var session = await Provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        var engine = new MigrationEngine(project.Load(Provider.Id), Provider, db.Profile, session, new Actor("conformance", "test", "0.0.0"));
        return new EngineHandle(engine, session);
    }

    private sealed record EngineHandle(MigrationEngine Engine, IDatabaseSession Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class ConformanceProject : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bsm-conformance", Guid.NewGuid().ToString("N"));

        public ConformanceProject()
        {
            Directory.CreateDirectory(Path.Combine(Root, MigrationProject.ChangesFolder));
            File.WriteAllText(Path.Combine(Root, "bsm.plan"), string.Empty);
        }

        public void Change(string name, string deploy, string revert, string? verify = null)
        {
            var dir = Path.Combine(Root, MigrationProject.ChangesFolder, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MigrationProject.DeployFile), deploy);
            File.WriteAllText(Path.Combine(dir, MigrationProject.RevertFile), revert);
            if (verify is not null)
            {
                File.WriteAllText(Path.Combine(dir, MigrationProject.VerifyFile), verify);
            }

            File.AppendAllText(Path.Combine(Root, "bsm.plan"), name + "\n");
        }

        public void Repeatable(string name, string script)
        {
            var path = Path.Combine(Root, MigrationProject.RepeatableFolder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, script);
        }

        public void Write(string relative, string text) => File.WriteAllText(Path.Combine(Root, relative), text);

        public void WritePlan(string text) => File.WriteAllText(Path.Combine(Root, "bsm.plan"), text);

        public void DeleteChange(string name) => Directory.Delete(Path.Combine(Root, MigrationProject.ChangesFolder, name), true);

        public MigrationProject Load(string providerId) => MigrationProject.Load(Root, providerId);

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
