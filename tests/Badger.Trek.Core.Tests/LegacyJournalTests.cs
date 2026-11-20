using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Engine;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Core.Tests;

/// <summary>
/// A database deployed by Badger SQL Migrate (0.3.0 and earlier) keeps its journal in state schema "bsm". trek does not
/// read it, but must never treat such a database as empty: it stops and says how to move the journal.
/// </summary>
public sealed class LegacyJournalTests : IDisposable
{
    private readonly TestProject project = new();
    private readonly FakeDatabase db = new() { LegacyJournal = true };
    private readonly FakeProvider provider = new();

    public LegacyJournalTests() => project.Change("a", "CREATE A", "DROP A");

    public void Dispose() => project.Dispose();

    private MigrationEngine Engine(string stateSchema = "trek") =>
        new(project.Load(), provider, new DeploymentEnvironment { Name = "test", StateSchema = stateSchema }, new FakeSession(db), new Actor("tester", "host", "1.0"));

    [Fact]
    public async Task Status_stops_with_the_move_script_instead_of_showing_everything_pending()
    {
        var ex = await Assert.ThrowsAsync<MigrationException>(() => Engine().StatusAsync());

        Assert.Contains("Badger SQL Migrate journal (state schema 'bsm')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("    MOVE bsm TO trek;", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'stateSchema: bsm' on environment 'test'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://lonebadger.tech/badger-trek/docs/upgrading/", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_stops_before_creating_a_journal_or_running_anything()
    {
        await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions()));
        await Assert.ThrowsAsync<MigrationException>(() => Engine().DeployAsync(new DeployOptions { DryRun = true }));
        await Assert.ThrowsAsync<MigrationException>(() => Engine().BaselineAsync("a"));
        await Assert.ThrowsAsync<MigrationException>(() => Engine().LogAsync(10));

        Assert.False(db.StateCreated);
        Assert.Empty(db.Objects);
        Assert.Empty(db.Changes);
    }

    [Fact]
    public async Task A_trek_journal_wins_over_a_leftover_bsm_journal()
    {
        db.StateCreated = true;

        Assert.Equal(["a"], (await Engine().DeployAsync(new DeployOptions())).Deployed);
        Assert.True((await Engine().StatusAsync()).UpToDate);
    }

    [Fact]
    public async Task State_schema_bsm_uses_the_old_journal_where_it_is()
    {
        var status = await Engine(stateSchema: "bsm").StatusAsync();

        Assert.Single(status.Changes);
        Assert.False(db.StateCreated);
    }

    [Fact]
    public async Task A_database_without_any_journal_deploys_as_usual()
    {
        db.LegacyJournal = false;

        Assert.Equal(["a"], (await Engine().DeployAsync(new DeployOptions())).Deployed);
    }
}
