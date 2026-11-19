using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.Sqlite.Tests;

public sealed class SqliteFeatureTests
{
    private readonly SqliteProvider provider = new();

    [Fact]
    public async Task State_tables_use_the_state_schema_as_a_prefix()
    {
        await using var db = new TestDatabase();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        Assert.False(await provider.StateStore.ExistsAsync(session, "bsm", CancellationToken.None));
        await provider.StateStore.EnsureCreatedAsync(session, "bsm", CancellationToken.None);
        await provider.StateStore.EnsureCreatedAsync(session, "bsm", CancellationToken.None);
        Assert.True(await provider.StateStore.ExistsAsync(session, "bsm", CancellationToken.None));

        var tables = (await session.QueryAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'bsm\\_%' ESCAPE '\\' ORDER BY name;", null, CancellationToken.None))
            .Select(r => (string)r["name"]!).ToList();
        Assert.Equal(["bsm_changes", "bsm_events", "bsm_meta", "bsm_repeatables", "bsm_schema_snapshots"], tables);

        var at = new DateTimeOffset(2026, 11, 19, 8, 30, 0, TimeSpan.FromHours(-5));
        await provider.StateStore.InsertChangeAsync(session, "bsm", new AppliedChange
        {
            Name = "c1", ScriptHash = "h", ChainId = "c", DeployScript = "SELECT 1;", Flags = 2, DeployedAtUtc = at,
            DeployedBy = "me", Host = "box", ToolVersion = "1", DurationMs = 7, AppliedOutOfOrder = true, Baselined = false,
        }, CancellationToken.None);
        var change = Assert.Single(await provider.StateStore.GetAppliedChangesAsync(session, "bsm", CancellationToken.None));
        Assert.Equal(at, change.DeployedAtUtc);
        Assert.Equal(TimeSpan.Zero, change.DeployedAtUtc.Offset);
        Assert.Equal(2, change.Flags);
        Assert.True(change.AppliedOutOfOrder);
        Assert.False(change.Baselined);
        Assert.Equal(1, change.Sequence);

        // The state tables are not part of the schema fingerprint.
        Assert.Equal(string.Empty, await provider.Schema.CaptureAsync(session, "bsm", CancellationToken.None));
    }

    [Fact]
    public void Network_settings_and_in_memory_databases_are_rejected()
    {
        Assert.Contains(provider.ValidateEnvironment(new DeploymentEnvironment { Name = "x" }), p => p.Contains("set 'database'", StringComparison.Ordinal));
        Assert.Contains(provider.ValidateEnvironment(new DeploymentEnvironment { Name = "x", Database = ":memory:" }), p => p.Contains("in-memory", StringComparison.Ordinal));
        Assert.Contains(provider.ValidateEnvironment(new DeploymentEnvironment { Name = "x", ConnectionString = "Data Source=app.db;Mode=Memory" }), p => p.Contains("in-memory", StringComparison.Ordinal));
        var networked = provider.ValidateEnvironment(new DeploymentEnvironment { Name = "x", Database = "a.db", Host = "h", Authentication = AuthenticationKind.Password, Tls = new TlsOptions { Mode = TlsMode.Strict } });
        Assert.Equal(3, networked.Count);
        Assert.Empty(provider.ValidateEnvironment(new DeploymentEnvironment { Name = "x", Database = "a.db", ConnectionProperties = new Dictionary<string, string> { ["Foreign Keys"] = "True" } }));
        Assert.Throws<ArgumentException>(() => provider.ResolveEndpoint(new DeploymentEnvironment { Name = "x", Database = "a.db" }));
    }

    [Fact]
    public void Relative_paths_resolve_from_the_working_directory()
    {
        var environment = new DeploymentEnvironment { Name = "x", Database = Path.Combine("data", "app.db") };
        Assert.Equal(Path.GetFullPath(Path.Combine("data", "app.db")), SqliteConnectionFactory.ResolvePath(environment));
        Assert.Equal("app", provider.ResolveDatabaseName(environment));

        var fromConnectionString = new DeploymentEnvironment { Name = "x", ConnectionString = "Data Source=other.db", Database = "wins.db" };
        Assert.Equal(Path.GetFullPath("wins.db"), SqliteConnectionFactory.ResolvePath(fromConnectionString));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "x.db"), SqliteConnectionFactory.ResolvePath(new DeploymentEnvironment { Name = "x", Database = "~/x.db" }));
    }

    [Fact]
    public async Task Doctor_describes_the_file()
    {
        await using var db = new TestDatabase();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        var facts = (await provider.DescribeAsync(session, CancellationToken.None)).ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.StartsWith("SQLite 3.", facts["server"], StringComparison.Ordinal);
        Assert.Equal(db.Path, facts["database"]);
        Assert.Equal("sqlite://" + db.Path, session.Description);
    }

    [Fact]
    public async Task Tables_altered_in_place_fingerprint_like_tables_written_whole()
    {
        await using var a = new TestDatabase();
        await using var b = new TestDatabase();
        // DROP COLUMN arrived in SQLite 3.35; older engines (BSM_SQLITE_LIBRARY) check the rest.
        await ExecAsync(a, SystemSqlite.EngineVersion >= SqliteOptions.DropColumn
            ? "CREATE TABLE t (id INTEGER PRIMARY KEY, old TEXT, \"name\" TEXT);\nALTER TABLE t ADD COLUMN email TEXT DEFAULT 'x';\nALTER TABLE t RENAME COLUMN name TO full_name;\nALTER TABLE t DROP COLUMN old;"
            : "CREATE TABLE t (id INTEGER PRIMARY KEY, \"name\" TEXT);\nALTER TABLE t ADD COLUMN email TEXT DEFAULT 'x';\nALTER TABLE t RENAME COLUMN name TO full_name;");
        await ExecAsync(b, "CREATE TABLE [t](\n  id   INTEGER PRIMARY KEY,\n  full_name TEXT, -- renamed\n  email TEXT DEFAULT 'x'\n)");
        Assert.Equal(await DumpAsync(b), await DumpAsync(a));
    }

    [Fact]
    public async Task A_failed_commit_leaves_the_transaction_for_rollback()
    {
        await using var db = new TestDatabase();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await session.ExecuteAsync("PRAGMA foreign_keys = ON; CREATE TABLE p (id INTEGER PRIMARY KEY); CREATE TABLE c (pid INTEGER REFERENCES p (id) DEFERRABLE INITIALLY DEFERRED);", null, CancellationToken.None);
        await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (42);", null, CancellationToken.None);
        await Assert.ThrowsAsync<MigrationException>(() => session.CommitAsync(CancellationToken.None));
        Assert.True(session.InTransaction);
        await session.RollbackAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1);", null, CancellationToken.None);
    }

    private async Task ExecAsync(TestDatabase db, string sql)
    {
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        foreach (var batch in provider.Parser.Split(sql))
        {
            await session.ExecuteBatchAsync(batch, CancellationToken.None);
        }
    }

    private async Task<string> DumpAsync(TestDatabase db)
    {
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        return await provider.Schema.CaptureAsync(session, "bsm", CancellationToken.None);
    }
}
