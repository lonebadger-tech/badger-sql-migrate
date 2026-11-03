using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.PostgreSql;

namespace Badger.SqlMigrate.Providers.PostgreSql.Tests;

[Collection("postgresql")]
public sealed class PostgreSqlFeatureTests(PostgreSqlFixture fixture)
{
    private readonly PostgreSqlProvider provider = new();

    private const string Schema = """
        CREATE SCHEMA app;
        CREATE TYPE app.mood AS ENUM ('happy', 'sad');
        CREATE DOMAIN app.email AS text CHECK (VALUE LIKE '%@%');
        CREATE TABLE app.customer (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            name text NOT NULL,
            email app.email,
            mood app.mood NOT NULL DEFAULT 'happy',
            tags text[] NOT NULL DEFAULT '{}',
            updated_at timestamptz NOT NULL DEFAULT now(),
            name_upper text GENERATED ALWAYS AS (upper(name)) STORED,
            CONSTRAINT customer_email_key UNIQUE (email));
        CREATE TABLE app.sale (
            sold_on date NOT NULL,
            id bigint NOT NULL,
            customer_id bigint NOT NULL REFERENCES app.customer (id) ON DELETE CASCADE,
            amount numeric(12, 2) NOT NULL CHECK (amount >= 0),
            PRIMARY KEY (sold_on, id)) PARTITION BY RANGE (sold_on);
        CREATE TABLE app.sale_2026 PARTITION OF app.sale FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
        CREATE INDEX sale_customer_idx ON app.sale (customer_id);
        CREATE FUNCTION app.touch() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            NEW.updated_at := now();  -- keep the timestamp current; semicolons inside the body
            RETURN NEW;
        END;
        $$;
        CREATE TRIGGER customer_touch BEFORE UPDATE ON app.customer FOR EACH ROW EXECUTE FUNCTION app.touch();
        CREATE VIEW app.customer_sales AS SELECT c.id, c.name, sum(s.amount) AS total FROM app.customer c LEFT JOIN app.sale s ON s.customer_id = c.id GROUP BY c.id, c.name;
        ALTER TABLE app.customer ENABLE ROW LEVEL SECURITY;
        CREATE POLICY customer_happy ON app.customer FOR SELECT USING (mood = 'happy');
        CREATE SEQUENCE app.ticket AS integer START WITH 100 INCREMENT BY 10;
        DO $$ BEGIN RAISE NOTICE 'schema ready'; END $$;
        """;

    private const string Revert = """
        DROP VIEW app.customer_sales;
        DROP TABLE app.sale;
        DROP TABLE app.customer;
        DROP FUNCTION app.touch();
        DROP SEQUENCE app.ticket;
        DROP DOMAIN app.email;
        DROP TYPE app.mood;
        DROP SCHEMA app;
        """;

    [SkippableFact]
    public async Task A_realistic_schema_deploys_reverts_and_fingerprints_the_same_everywhere()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        await using var a = await fixture.CreateDatabaseAsync();
        await using var b = await fixture.CreateDatabaseAsync();
        using var project = new ProjectDir();
        project.Change("app-schema", Schema, Revert);
        project.Change("sale-amount-index !notx", "CREATE INDEX CONCURRENTLY sale_2026_amount_idx ON app.sale_2026 (amount);", "DROP INDEX CONCURRENTLY app.sale_2026_amount_idx;");

        var messages = new List<string>();
        string dump;
        await using (var e = await EngineAsync(a, project, messages))
        {
            Assert.Equal(["app-schema", "sale-amount-index"], (await e.Engine.DeployAsync(new DeployOptions())).Deployed);
            dump = (await e.Engine.GetRecordedSchemaAsync())!.Dump;
            Assert.True((await e.Engine.VerifyAsync()).Ok);
        }

        Assert.Contains(messages, m => m.Contains("schema ready", StringComparison.Ordinal));
        foreach (var expected in new[]
        {
            "type|app.mood|enum|happy,sad",
            "type|app.email|domain|text|false||email_check CHECK ((VALUE ~~ '%@%'::text))",
            "table|app.sale|p|p|RANGE (sold_on)",
            "table|app.sale_2026|r|p||app.sale|FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')",
            "|name_upper|text|false|upper(name)||s|",
            "constraint|app.sale|sale_customer_id_fkey|FOREIGN KEY (customer_id) REFERENCES app.customer(id) ON DELETE CASCADE",
            "policy|app.customer|customer_happy|r|true|public|(mood = 'happy'::app.mood)|",
            "trigger|app.customer|customer_touch|CREATE TRIGGER customer_touch BEFORE UPDATE ON app.customer FOR EACH ROW EXECUTE FUNCTION app.touch()",
            "index|app.sale_2026|sale_2026_amount_idx|CREATE INDEX sale_2026_amount_idx ON app.sale_2026 USING btree (amount)",
            "sequence|app.ticket|integer|100|10|",
        })
        {
            Assert.Contains(expected, dump, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("customer_id_seq", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("bsm.", dump, StringComparison.Ordinal);

        await using (var e = await EngineAsync(b, project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
            Assert.Equal(dump, (await e.Engine.GetRecordedSchemaAsync())!.Dump);

            Assert.Equal(["sale-amount-index", "app-schema"], (await e.Engine.RevertAsync(new RevertOptions { All = true })).Reverted);
            Assert.DoesNotContain("app.", await e.Engine.CaptureSchemaAsync(), StringComparison.Ordinal);
            await e.Engine.DeployAsync(new DeployOptions());
            Assert.Equal(dump, await e.Engine.CaptureSchemaAsync());
        }
    }

    [SkippableFact]
    public async Task Errors_name_the_statement_line_and_roll_back()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        await using var db = await fixture.CreateDatabaseAsync();
        using var project = new ProjectDir();
        project.Change("broken", "CREATE TABLE t1 (id int);\n\nCREATE TABLE t2 (id int);\nSELECT nope FROM t1;", "DROP TABLE t2;\nDROP TABLE t1;");

        await using var e = await EngineAsync(db, project);
        var ex = await Assert.ThrowsAsync<MigrationException>(() => e.Engine.DeployAsync(new DeployOptions()));
        Assert.Contains("broken (deploy) failed in the batch starting at line 4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("column \"nope\" does not exist", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("t1", await e.Engine.CaptureSchemaAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Search_path_is_left_as_it_was_after_capturing()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        await using var db = await fixture.CreateDatabaseAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await session.ExecuteAsync("SET search_path = app, public;", null, CancellationToken.None);
        await provider.Schema.CaptureAsync(session, "bsm", CancellationToken.None);
        Assert.Equal("app, public", (await session.QueryAsync("SHOW search_path;", null, CancellationToken.None))[0]["search_path"]);
    }

    private async Task<EngineHandle> EngineAsync(TestDatabase db, ProjectDir project, List<string>? messages = null)
    {
        var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        var log = new ListLog(messages ?? []);
        var engine = new MigrationEngine(MigrationProject.Load(project.Root, provider.Id), provider, db.Profile, session, new Actor("tests", "host", "0.0.0"), log);
        return new EngineHandle(engine, session);
    }

    private sealed record EngineHandle(MigrationEngine Engine, IDatabaseSession Session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class ListLog(List<string> messages) : IMigrationLog
    {
        public void Info(string message) => messages.Add(message);

        public void Success(string message) => messages.Add(message);

        public void Warn(string message) => messages.Add(message);

        public void Detail(string message) => messages.Add(message);
    }

    private sealed class ProjectDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "bsm-pg", Guid.NewGuid().ToString("N"));

        public ProjectDir()
        {
            Directory.CreateDirectory(Path.Combine(Root, MigrationProject.ChangesFolder));
            File.WriteAllText(Path.Combine(Root, "bsm.plan"), string.Empty);
        }

        public void Change(string planLine, string deploy, string revert)
        {
            var name = planLine.Split(' ')[0];
            var dir = Path.Combine(Root, MigrationProject.ChangesFolder, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MigrationProject.DeployFile), deploy);
            File.WriteAllText(Path.Combine(dir, MigrationProject.RevertFile), revert);
            File.AppendAllText(Path.Combine(Root, "bsm.plan"), planLine + "\n");
        }

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
