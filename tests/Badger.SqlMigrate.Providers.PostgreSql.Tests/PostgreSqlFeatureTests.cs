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

    private const string Extras = """
        CREATE TABLE app.note (id int PRIMARY KEY, body text, a int, b int) WITH (fillfactor = 70);
        ALTER TABLE app.note ALTER COLUMN body SET STORAGE EXTERNAL;
        COMMENT ON TABLE app.note IS 'Free-form notes';
        COMMENT ON COLUMN app.note.body IS 'Markdown';
        CREATE STATISTICS app.note_ab (dependencies) ON a, b FROM app.note;
        CREATE RULE note_no_delete AS ON DELETE TO app.note DO INSTEAD NOTHING;
        CREATE FUNCTION app.add_int(int, int) RETURNS int LANGUAGE sql IMMUTABLE AS 'SELECT $1 + $2';
        CREATE AGGREGATE app.total(int) (SFUNC = app.add_int, STYPE = int, INITCOND = '0');
        CREATE OPERATOR app.=== (LEFTARG = int, RIGHTARG = int, FUNCTION = app.add_int);
        CREATE TYPE app.money2 AS (amount numeric, currency char(3));
        CREATE FUNCTION app.money2_text(app.money2) RETURNS text LANGUAGE sql AS 'SELECT ($1).amount || '' '' || ($1).currency';
        CREATE CAST (app.money2 AS text) WITH FUNCTION app.money2_text(app.money2);
        CREATE COLLATION app.german (provider = libc, locale = 'C');
        CREATE TEXT SEARCH CONFIGURATION app.plain (COPY = pg_catalog.simple);
        CREATE PUBLICATION note_changes FOR TABLE app.note;
        CREATE FOREIGN DATA WRAPPER dummy_fdw;
        CREATE SERVER archive FOREIGN DATA WRAPPER dummy_fdw OPTIONS (region 'eu');
        CREATE USER MAPPING FOR bsm_test_reader SERVER archive;
        GRANT USAGE ON SCHEMA app TO bsm_test_reader;
        GRANT SELECT ON app.customer TO bsm_test_reader;
        GRANT SELECT (body) ON app.note TO bsm_test_reader;
        GRANT EXECUTE ON FUNCTION app.add_int(int, int) TO bsm_test_reader;
        ALTER DEFAULT PRIVILEGES IN SCHEMA app GRANT SELECT ON TABLES TO bsm_test_reader;
        CREATE FUNCTION app.log_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN RAISE NOTICE 'ddl'; END $$;
        CREATE EVENT TRIGGER log_ddl ON ddl_command_end EXECUTE FUNCTION app.log_ddl();
        """;

    [SkippableFact]
    public async Task The_fingerprint_covers_privileges_comments_storage_and_less_common_objects()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        await EnsureRoleAsync();
        await using var a = await fixture.CreateDatabaseAsync();
        await using var b = await fixture.CreateDatabaseAsync();
        using var project = new ProjectDir();
        project.Change("app-schema", Schema, Revert);
        project.Change("extras", Extras, "SELECT 1;");

        string dump;
        await using (var e = await EngineAsync(a, project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
            dump = await e.Engine.CaptureSchemaAsync();
        }

        foreach (var expected in new[]
        {
            "grant|table app.customer|bsm_test_reader|SELECT",
            "grant|table column app.note.body|bsm_test_reader|SELECT",
            "grant|function app.add_int(integer,integer)|bsm_test_reader|EXECUTE",
            "grant|schema app|bsm_test_reader|USAGE",
            "default privileges|",
            "comment|table app.note|Free-form notes",
            "comment|table column app.note.body|Markdown",
            "fillfactor=70",
            "|body|text|false||||||e",
            "statistics|app.note_ab|CREATE STATISTICS app.note_ab (dependencies) ON a, b FROM app.note",
            "rule|app.note|note_no_delete|CREATE RULE note_no_delete AS",
            "aggregate|app.total(integer)|n|app.add_int|integer|-|",
            "operator|app.===(integer, integer)|app.add_int|integer|",
            "cast|app.money2 AS text|app.money2_text(app.money2)|e|f",
            "collation|app.german|c|true|C|C",
            "text search configuration|app.plain|default|",
            "publication|note_changes|false|true,true,true,true|app.note",
            "foreign data wrapper|dummy_fdw|-|-|",
            "server|archive|dummy_fdw|||region=eu",
            "user mapping|archive|bsm_test_reader",
            "event trigger|log_ddl|ddl_command_end|app.log_ddl()|O|",
        })
        {
            Assert.Contains(expected, dump, StringComparison.Ordinal);
        }

        await using (var e = await EngineAsync(b, project))
        {
            await e.Engine.DeployAsync(new DeployOptions());
            Assert.Equal(dump, await e.Engine.CaptureSchemaAsync());

            // A grant made outside bsm is drift.
            await e.Session.ExecuteAsync("GRANT INSERT ON app.customer TO bsm_test_reader;", null, CancellationToken.None);
            var verify = await e.Engine.VerifyAsync();
            Assert.False(verify.Ok);
            var diff = SchemaDiff.Compare(provider.Schema.Parse((await e.Engine.GetRecordedSchemaAsync())!.Dump), provider.Schema.Parse(await e.Engine.CaptureSchemaAsync()));
            Assert.Equal("grant table app.customer to bsm_test_reader INSERT", Assert.Single(diff).Key);
            await e.Session.ExecuteAsync("DROP EVENT TRIGGER log_ddl;", null, CancellationToken.None);
        }

        await using (var e = await EngineAsync(a, project))
        {
            await e.Session.ExecuteAsync("DROP EVENT TRIGGER log_ddl;", null, CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Privileges_and_job_maintained_partitions_can_be_left_out()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        await EnsureRoleAsync();
        await using var db = await fixture.CreateDatabaseAsync();
        using var project = new ProjectDir();
        project.Change("app-schema", Schema, Revert);
        var settings = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{ "schemaFingerprint": { "partitionBoundaries": "ignore", "privileges": "ignore" } }""")!;
        var environment = db.Profile with { ProviderSettings = settings };
        Assert.Empty(provider.ValidateEnvironment(environment with { Host = "x" }));

        await using var session = await provider.OpenSessionAsync(environment, SessionContext.Empty, CancellationToken.None);
        var engine = new MigrationEngine(MigrationProject.Load(project.Root, provider.Id), provider, environment, session, new Actor("tests", "host", "0.0.0"));
        await engine.DeployAsync(new DeployOptions());
        var before = await engine.CaptureSchemaAsync();
        Assert.Contains("table|app.sale|p|", before, StringComparison.Ordinal);
        Assert.DoesNotContain("app.sale_2026", before, StringComparison.Ordinal);

        // What a partition-maintenance job and a DBA do outside bsm: neither is drift with these settings.
        await session.ExecuteAsync("CREATE TABLE app.sale_2027 PARTITION OF app.sale FOR VALUES FROM ('2027-01-01') TO ('2028-01-01');", null, CancellationToken.None);
        await session.ExecuteAsync("GRANT SELECT ON app.sale TO bsm_test_reader;", null, CancellationToken.None);
        Assert.True((await engine.VerifyAsync()).Ok);
        Assert.Equal(before, await engine.CaptureSchemaAsync());

        Assert.Contains(provider.ValidateEnvironment(environment with { Host = "x", ProviderSettings = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{ "schemaFingerprint": { "privileges": "sometimes" } }""")! }),
            p => p.Contains("invalid schemaFingerprint settings", StringComparison.Ordinal));
    }

    private async Task EnsureRoleAsync()
    {
        await using var admin = new Npgsql.NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var create = new Npgsql.NpgsqlCommand("DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'bsm_test_reader') THEN CREATE ROLE bsm_test_reader; END IF; END $$;", admin);
        await create.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Psql_scripts_run_with_variables_conditionals_copy_data_echo_and_gexec()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        await using var db = await fixture.CreateDatabaseAsync();
        using var project = new ProjectDir();
        project.Write("data/customers.csv", "id,name,region\n1,Ada,eu\n2,Grace,us\n");
        project.Change("seed", """
            \set ON_ERROR_STOP on
            \set tbl customer
            CREATE TABLE :"tbl" (id int PRIMARY KEY, name text NOT NULL, region text NOT NULL);
            \if :is_prod
            INSERT INTO :"tbl" VALUES (0, 'prod only', :'region');
            \else
            \copy customer (id, name, region) from 'data/customers.csv' with (format csv, header)
            \endif
            COPY customer (id, name, region) FROM STDIN;
            9	inline; not split	:region
            \.
            \echo seeded :tbl in :region
            SELECT format('CREATE INDEX %I ON customer (%I)', 'customer_' || c || '_idx', c) FROM unnest(ARRAY['name', 'region']) c
            \gexec
            """, "DROP TABLE customer;");
        var profile = db.Profile with { Variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["region"] = "eu", ["is_prod"] = "off" } };

        var messages = new List<string>();
        await using (var e = await EngineAsync(db, project, messages, profile))
        {
            Assert.Equal(["seed"], (await e.Engine.DeployAsync(new DeployOptions())).Deployed);
            var rows = await e.Session.QueryAsync("SELECT string_agg(id || ':' || name || ':' || region, ',' ORDER BY id) AS r FROM customer;", null, CancellationToken.None);
            Assert.Equal("1:Ada:eu,2:Grace:us,9:inline; not split::region", rows[0]["r"]);
            var indexes = await e.Session.QueryAsync("SELECT count(*) AS n FROM pg_indexes WHERE tablename = 'customer' AND indexname LIKE 'customer_%_idx';", null, CancellationToken.None);
            Assert.Equal(2L, indexes[0]["n"]);
            Assert.True((await e.Engine.VerifyAsync()).Ok);
            Assert.True((await e.Engine.StatusAsync()).Certified);
        }

        Assert.Contains(messages, m => m.Contains("seeded customer in eu", StringComparison.Ordinal));

        // The data file is part of the change: the journal holds the expanded script, so revert needs nothing on disk.
        File.Delete(Path.Combine(project.Root, "data", "customers.csv"));
        await using (var e = await EngineAsync(db, new ProjectDir(), profile: profile))
        {
            Assert.Equal(["seed"], (await e.Engine.RevertAsync(new RevertOptions { All = true })).Reverted);
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

    private async Task<EngineHandle> EngineAsync(TestDatabase db, ProjectDir project, List<string>? messages = null, DeploymentEnvironment? profile = null)
    {
        var session = await provider.OpenSessionAsync(profile ?? db.Profile, SessionContext.Empty, CancellationToken.None);
        var log = new ListLog(messages ?? []);
        var engine = new MigrationEngine(MigrationProject.Load(project.Root, provider.Id, includes: provider.Includes), provider, profile ?? db.Profile, session, new Actor("tests", "host", "0.0.0"), log);
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

        public void Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
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
