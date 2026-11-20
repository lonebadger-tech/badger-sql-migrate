using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Engine;

namespace Badger.Trek.Core.Tests;

/// <summary>Bugs found while using trek on a real project.</summary>
public sealed class DogfoodingFixesTests : IDisposable
{
    private readonly TestProject project = new();
    private readonly FakeDatabase db = new();
    private readonly FakeProvider provider = new();
    private readonly Log log = new();

    public void Dispose() => project.Dispose();

    private sealed class Log : IMigrationLog
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add("info: " + message);

        public void Success(string message) => Lines.Add("success: " + message);

        public void Warn(string message) => Lines.Add("warn: " + message);

        public void Detail(string message) => Lines.Add("detail: " + message);
    }

    private MigrationEngine Engine() =>
        new(project.Load(), provider, new DeploymentEnvironment { Name = "test" }, new FakeSession(db), new Actor("tester", "host", "1.0"), log);

    [Fact]
    public async Task A_failed_fingerprint_after_deploy_is_a_warning_and_the_next_deploy_records_it()
    {
        project.Change("a", "CREATE A", "DROP A");
        provider.CaptureFailure = new InvalidOperationException("Invalid column name 'history_retention_period'.");

        var result = await Engine().DeployAsync(new DeployOptions());

        Assert.Equal(["a"], result.Deployed);
        Assert.Single(db.Changes);
        Assert.Empty(db.Snapshots);
        Assert.Contains(log.Lines, l => l.StartsWith("warn: the changes are applied, but the schema fingerprint could not be recorded: Invalid column name", StringComparison.Ordinal));

        // Nothing is pending, but the fingerprint is missing: a deploy records it.
        provider.CaptureFailure = null;
        Assert.Empty((await Engine().DeployAsync(new DeployOptions())).Deployed);
        Assert.Single(db.Snapshots);
    }

    [Fact]
    public async Task Verify_without_a_recorded_fingerprint_says_so()
    {
        project.Change("a", "CREATE A", "DROP A");
        await Engine().DeployAsync(new DeployOptions());
        db.Snapshots.Clear();
        db.Objects.Remove("A");

        var verify = await Engine().VerifyAsync();

        Assert.Null(verify.RecordedFingerprint);
        Assert.False(verify.Drifted);
        Assert.Contains(log.Lines, l => l.StartsWith("warn: no schema fingerprint is recorded for this database", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_quiet_revert_preview_logs_nothing()
    {
        project.Change("a", "CREATE A", "DROP A").Change("b [a]", "CREATE B", "DROP B");
        await Engine().DeployAsync(new DeployOptions());
        log.Lines.Clear();

        var preview = await Engine().RevertAsync(new RevertOptions { All = true, DryRun = true, Quiet = true });

        Assert.Equal(["b", "a"], preview.Reverted);
        Assert.DoesNotContain(log.Lines, l => l.Contains("would revert", StringComparison.Ordinal));
        Assert.Equal(2, db.Changes.Count);
    }
}
