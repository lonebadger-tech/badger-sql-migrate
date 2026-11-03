using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Tests;

public sealed class EngineTests : IDisposable
{
    private readonly TestProject project = new();
    private readonly FakeDatabase db = new();
    private readonly FakeProvider provider = new();

    public void Dispose() => project.Dispose();

    private MigrationEngine Engine(OrderingPolicy ordering = OrderingPolicy.Dependency, IReadOnlyDictionary<string, string>? vars = null, FakeProvider? p = null) =>
        new(project.Load(), p ?? provider, new DeploymentEnvironment { Name = "test", Ordering = ordering }, new FakeSession(db), new Actor("tester", "host", "1.0"), variableOverrides: vars);

    [Fact]
    public async Task Deploys_pending_changes_in_plan_order_and_is_certified()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b [a]", "CREATE B", "DROP B");

        var result = await Engine().DeployAsync(new DeployOptions());
        var status = await Engine().StatusAsync();

        Assert.Equal(["a", "b"], result.Deployed);
        Assert.Equal(["A", "B"], db.Objects.Order());
        Assert.True(status.UpToDate);
        Assert.True(status.Certified);
        Assert.Equal(project.Load().HeadChainId, status.DatabaseHeadChainId);
        Assert.Equal(1, provider.LocksTaken);
        Assert.Single(db.Snapshots);
    }

    [Fact]
    public async Task Second_deploy_is_a_no_op()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine().DeployAsync(new DeployOptions());
        var executed = db.Executed.Count;

        var result = await Engine().DeployAsync(new DeployOptions());

        Assert.Empty(result.Deployed);
        Assert.Equal(executed, db.Executed.Count);
    }

    [Fact]
    public async Task Deploy_to_target_and_tag()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b", "CREATE B", "DROP B").Change("c", "CREATE C", "DROP C");
        File.AppendAllText(Path.Combine(project.Root, "bsm.plan"), "");
        project.WritePlan("a\nb\n@v1\nc\n");

        Assert.Equal(["a"], (await Engine().DeployAsync(new DeployOptions { To = "a" })).Deployed);
        Assert.Equal(["b"], (await Engine().DeployAsync(new DeployOptions { To = "@v1" })).Deployed);
        Assert.Equal(["c"], (await Engine().DeployAsync(new DeployOptions())).Deployed);
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        project.Change("a", "CREATE A", "DROP A");
        var result = await Engine().DeployAsync(new DeployOptions { DryRun = true });

        Assert.Equal(["a"], result.Deployed);
        Assert.Empty(db.Objects);
        Assert.False(db.StateCreated);
        Assert.Equal(0, provider.LocksTaken);
    }

    [Fact]
    public async Task Failed_change_rolls_back_and_records_failure()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b", "CREATE B\n;\nFAIL", "DROP B");

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions()));

        Assert.Contains("b (deploy) failed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["A"], db.Objects);
        Assert.Equal(["a"], db.Changes.Select(c => c.Name));
        Assert.Equal(MigrationEventKind.Fail, db.Events[^1].Kind);
    }

    [Fact]
    public async Task Failed_verify_script_rolls_back_the_deploy()
    {
        project.Change("a", "CREATE A", "DROP A", verify: "FAIL");

        await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions()));

        Assert.Empty(db.Objects);
        Assert.Empty(db.Changes);
    }

    [Fact]
    public async Task Revert_uses_scripts_stored_in_the_database_even_after_the_change_is_gone()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b", "CREATE B", "DROP B");
        await Engine().DeployAsync(new DeployOptions());

        // Another branch: change b no longer exists in the working copy.
        project.DeleteChange("b");
        project.WritePlan("a\n");

        var status = await Engine().StatusAsync();
        Assert.Equal(["b"], status.Orphans.Select(o => o.Name));

        var result = await Engine().RevertAsync(new RevertOptions { To = "a" });

        Assert.Equal(["b"], result.Reverted);
        Assert.Equal(["A"], db.Objects);
    }

    [Fact]
    public async Task Revert_last_and_all_in_reverse_deploy_order()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b", "CREATE B", "DROP B").Change("c", "CREATE C", "DROP C");
        await Engine().DeployAsync(new DeployOptions());

        Assert.Equal(["c"], (await Engine().RevertAsync(new RevertOptions { Last = 1 })).Reverted);
        Assert.Equal(["b", "a"], (await Engine().RevertAsync(new RevertOptions { All = true })).Reverted);
        Assert.Empty(db.Objects);
        Assert.Empty(db.Changes);
    }

    [Fact]
    public async Task Revert_then_redeploy_gives_the_same_certificate_and_fingerprint()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b", "CREATE B", "DROP B");
        await Engine().DeployAsync(new DeployOptions());
        var head = (await Engine().StatusAsync()).DatabaseHeadChainId;
        var fingerprint = db.Snapshots[^1].Fingerprint;

        await Engine().RevertAsync(new RevertOptions { To = "a" });
        await Engine().DeployAsync(new DeployOptions());

        Assert.Equal(head, (await Engine().StatusAsync()).DatabaseHeadChainId);
        Assert.Equal(fingerprint, db.Snapshots[^1].Fingerprint);
    }

    [Fact]
    public async Task Irreversible_changes_block_revert()
    {
        project.Change("a !irreversible", "CREATE A");
        await Engine().DeployAsync(new DeployOptions());

        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine().RevertAsync(new RevertOptions { All = true }));
        Assert.Contains("irreversible", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["A"], db.Objects);
    }

    [Fact]
    public async Task Missing_revert_without_irreversible_flag_is_a_project_problem()
    {
        project.Change("a", "CREATE A");
        Assert.Contains(project.Load().Problems, p => p.Contains("no revert.sql", StringComparison.Ordinal));
        await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions()));
    }

    [Fact]
    public async Task Editing_a_deployed_change_is_refused()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine().DeployAsync(new DeployOptions());
        project.Write("changes/a/deploy.sql", "CREATE A2");

        var status = await Engine().StatusAsync();
        Assert.Equal(ChangeState.HashMismatch, status.Changes[0].State);
        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions()));
        Assert.Contains("edited after they were deployed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bob deployed his change to the shared dev DB; then Alice's change was merged above his in the plan.
    /// Dependency ordering applies it (flagged out of order); strict ordering refuses.
    /// </summary>
    [Fact]
    public async Task Out_of_order_changes_follow_the_ordering_policy()
    {
        project.Change("base", "CREATE BASE", "DROP BASE").Change("bob", "CREATE BOB", "DROP BOB");
        await Engine().DeployAsync(new DeployOptions());

        project.Change("alice", "CREATE ALICE", "DROP ALICE");
        project.WritePlan("base\nalice\nbob\n");

        var strict = await Assert.ThrowsAsync<MigrationException>(() => Engine(OrderingPolicy.Strict).DeployAsync(new DeployOptions()));
        Assert.Contains("strict ordering", strict.Message, StringComparison.Ordinal);

        var result = await Engine().DeployAsync(new DeployOptions());
        Assert.Equal(["alice"], result.Deployed);

        var status = await Engine().StatusAsync();
        Assert.True(status.UpToDate);
        Assert.False(status.Certified);
        Assert.True(db.Changes.Single(c => c.Name == "alice").AppliedOutOfOrder);

        // A fresh database (e.g. production) receives exact plan order and is certified.
        var fresh = new FakeDatabase();
        var prod = new MigrationEngine(project.Load(), provider, new DeploymentEnvironment { Name = "prod", Ordering = OrderingPolicy.Strict }, new FakeSession(fresh), new Actor("ci", "ci", "1.0"));
        await prod.DeployAsync(new DeployOptions());
        Assert.True((await prod.StatusAsync()).Certified);
    }

    [Fact]
    public async Task Variables_are_substituted_but_not_hashed()
    {
        project.Change("a", "CREATE $(Name)", "DROP $(Name)");
        var hash = project.Load().HeadChainId;

        await Engine(vars: new Dictionary<string, string> { ["Name"] = "Custom" }).DeployAsync(new DeployOptions());

        Assert.Equal(["Custom"], db.Objects);
        Assert.Equal(hash, project.Load().HeadChainId);
    }

    [Fact]
    public async Task Missing_variable_is_an_error()
    {
        project.Change("a", "CREATE $(Name)", "DROP $(Name)");
        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions()));
        Assert.Contains("$(Name)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lint_errors_block_deploy()
    {
        project.Change("a", "CREATE A LINT-ERROR", "DROP A");
        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions()));
        Assert.Contains("Lint errors", ex.Message, StringComparison.Ordinal);
        Assert.Empty(db.Objects);
    }

    [Fact]
    public async Task Repeatables_apply_in_dependency_order_and_only_when_changed()
    {
        project.Change("a", "CREATE A", "DROP A")
            .Repeatable("views/b.sql", "-- bsm:requires views/c.sql\nPRINT b")
            .Repeatable("views/c.sql", "PRINT c");

        var first = await Engine().DeployAsync(new DeployOptions());
        Assert.Equal(["views/c.sql", "views/b.sql"], first.Repeatables);

        Assert.Empty((await Engine().DeployAsync(new DeployOptions())).Repeatables);

        project.Repeatable("views/c.sql", "PRINT c2");
        var third = await Engine().DeployAsync(new DeployOptions());
        Assert.Equal(["views/c.sql"], third.Repeatables);
        Assert.Equal("PRINT c", db.Repeatables.Single(r => r.Name == "views/c.sql").PreviousScript);
    }

    [Fact]
    public async Task Verify_detects_schema_drift()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine().DeployAsync(new DeployOptions());
        Assert.True((await Engine().VerifyAsync()).Ok);

        db.Objects.Add("HOTFIX");
        var result = await Engine().VerifyAsync();

        Assert.True(result.Drifted);
        Assert.Contains(result.Problems, p => p.Contains("schema drift", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Baseline_records_without_executing()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b", "CREATE B", "DROP B");

        var names = await Engine().BaselineAsync("a");

        Assert.Equal(["a"], names);
        Assert.Empty(db.Objects);
        Assert.True(db.Changes.Single().Baselined);
        Assert.Equal(["b"], (await Engine().DeployAsync(new DeployOptions())).Deployed);
        Assert.True((await Engine().StatusAsync()).Certified);
    }

    [Fact]
    public async Task Non_transactional_providers_record_after_success()
    {
        var mysqlLike = new FakeProvider { TransactionalDdl = false };
        project.Change("a", "CREATE A", "DROP A").Change("b", "CREATE B\n;\nFAIL", "DROP B");

        await Assert.ThrowsAsync<MigrationException>(() => Engine(p: mysqlLike).DeployAsync(new DeployOptions()));

        // Without transactional DDL the partial work stays; the change is not recorded so it can be fixed and re-run.
        Assert.Equal(["A", "B"], db.Objects.Order());
        Assert.Equal(["a"], db.Changes.Select(c => c.Name));
    }
}
