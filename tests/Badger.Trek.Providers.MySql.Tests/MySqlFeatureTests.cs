using Badger.Trek.Core;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql.Tests;

[Collection("mysql")]
public sealed class MySqlFeatureTests(MySqlFixture fixture)
{
    private readonly MySqlProvider provider = new(MySqlFixture.Flavor);

    private async Task<TestDatabase> RequireDatabaseAsync()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        return await fixture.CreateDatabaseAsync();
    }

    [SkippableFact]
    public async Task The_journal_lives_in_the_database_as_prefixed_tables()
    {
        await using var db = await RequireDatabaseAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        Assert.False(await provider.StateStore.ExistsAsync(session, "trek", CancellationToken.None));
        await provider.StateStore.EnsureCreatedAsync(session, "trek", CancellationToken.None);
        await provider.StateStore.EnsureCreatedAsync(session, "trek", CancellationToken.None);
        Assert.True(await provider.StateStore.ExistsAsync(session, "trek", CancellationToken.None));

        var tables = (await session.QueryAsync("SELECT table_name AS t FROM information_schema.tables WHERE table_schema = DATABASE() ORDER BY table_name;", null, CancellationToken.None))
            .Select(r => (string)r["t"]!).ToList();
        Assert.Equal(["trek_changes", "trek_events", "trek_meta", "trek_repeatables", "trek_schema_snapshots"], tables);

        var at = new DateTimeOffset(2026, 11, 20, 8, 30, 0, 123, TimeSpan.FromHours(-6));
        await provider.StateStore.InsertChangeAsync(session, "trek", new AppliedChange
        {
            Name = "c1", ScriptHash = "h", ChainId = "c", DeployScript = "SELECT 1;", Flags = 2, DeployedAtUtc = at,
            DeployedBy = "me", Host = "box", ToolVersion = "1", DurationMs = 7, AppliedOutOfOrder = true, Baselined = false,
        }, CancellationToken.None);
        var change = Assert.Single(await provider.StateStore.GetAppliedChangesAsync(session, "trek", CancellationToken.None));
        Assert.Equal(at, change.DeployedAtUtc);
        Assert.Equal(TimeSpan.Zero, change.DeployedAtUtc.Offset);
        Assert.Equal(2, change.Flags);
        Assert.True(change.AppliedOutOfOrder);
        Assert.False(change.Baselined);

        await provider.StateStore.SetMetaAsync(session, "trek", "environment", "a", CancellationToken.None);
        await provider.StateStore.SetMetaAsync(session, "trek", "environment", "b", CancellationToken.None);
        Assert.Equal("b", await provider.StateStore.GetMetaAsync(session, "trek", "environment", CancellationToken.None));

        // The state tables are not part of the schema fingerprint.
        Assert.Equal(string.Empty, await provider.Schema.CaptureAsync(session, "trek", CancellationToken.None));
    }

    [SkippableFact]
    public async Task The_fingerprint_covers_the_schema_and_not_the_database_name()
    {
        await using var a = await RequireDatabaseAsync();
        await using var b = await RequireDatabaseAsync();
        const string schema = """
            CREATE TABLE customer (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, email VARCHAR(200) NOT NULL, UNIQUE KEY ux_email (email));
            CREATE TABLE orders (id INT NOT NULL PRIMARY KEY, customer_id INT NOT NULL, total DECIMAL(12,2) NOT NULL DEFAULT 0,
                CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE,
                CONSTRAINT ck_total CHECK (total >= 0));
            CREATE VIEW big_orders AS SELECT o.id, c.email FROM orders o JOIN customer c ON c.id = o.customer_id WHERE o.total > 100;
            """;
        var dumps = new List<string>();
        foreach (var db in new[] { a, b })
        {
            await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
            foreach (var statement in provider.Parser.Split(schema))
            {
                await session.ExecuteAsync(statement.Text, null, CancellationToken.None);
            }

            dumps.Add(await provider.Schema.CaptureAsync(session, "trek", CancellationToken.None));
        }

        Assert.Equal(dumps[0], dumps[1]);
        Assert.DoesNotContain(a.Name, dumps[0], StringComparison.Ordinal);
        var keys = provider.Schema.Parse(dumps[0]).Select(e => e.Key).ToList();
        Assert.Contains("column orders.total", keys);
        Assert.Contains("index customer.ux_email", keys);
        Assert.Contains("foreign key orders.fk_orders_customer", keys);
        Assert.Contains("check orders.ck_total", keys);
        Assert.Contains("view big_orders", keys);

        await using (var session = await provider.OpenSessionAsync(b.Profile, SessionContext.Empty, CancellationToken.None))
        {
            await session.ExecuteAsync("ALTER TABLE orders MODIFY total DECIMAL(14,2) NOT NULL DEFAULT 0;", null, CancellationToken.None);
            Assert.NotEqual(dumps[0], await provider.Schema.CaptureAsync(session, "trek", CancellationToken.None));
        }
    }

    [SkippableFact]
    public async Task Doctor_describes_the_server()
    {
        await using var db = await RequireDatabaseAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        var facts = (await provider.DescribeAsync(session, CancellationToken.None)).ToDictionary(kv => kv.Key, kv => kv.Value);

        Assert.StartsWith(MySqlFixture.Product + " ", facts["server"], StringComparison.Ordinal);
        Assert.DoesNotContain("not supported", facts["server"], StringComparison.Ordinal);
        Assert.Equal(db.Name, facts["database"]);
    }

    [Fact]
    public void An_environment_needs_a_database()
    {
        var problems = provider.ValidateEnvironment(new Core.Configuration.DeploymentEnvironment { Name = "x", Host = "localhost", Authentication = Core.Configuration.AuthenticationKind.Password, User = "root" });
        Assert.Contains(problems, p => p.Contains("set 'database'", StringComparison.Ordinal));
        Assert.Contains(provider.ValidateEnvironment(new Core.Configuration.DeploymentEnvironment { Name = "x", Host = "h", Database = "d", Authentication = Core.Configuration.AuthenticationKind.Integrated }),
            p => p.Contains("not available for " + MySqlFixture.Product, StringComparison.Ordinal));
        Assert.Throws<MigrationException>(() => MySqlStateStore.Prefix("this_state_schema_name_is_far_too_long_for_mysql_tables"));
    }
}
