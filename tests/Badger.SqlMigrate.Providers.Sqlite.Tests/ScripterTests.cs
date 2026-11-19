using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.Sqlite.Scripting;

namespace Badger.SqlMigrate.Providers.Sqlite.Tests;

/// <summary>
/// Round trips: deploy must turn database A into a schema-identical copy of B, and revert must turn it back, with
/// the rows kept. "Identical" is the schema fingerprint dump, so this also proves the generated SQL is complete.
/// </summary>
public sealed class ScripterTests
{
    private readonly SqliteProvider provider = new();

    private const string Base = """
        CREATE TABLE customer (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            email TEXT,
            old INTEGER,
            code TEXT NOT NULL DEFAULT 'ABC');
        CREATE TABLE "order" (
            id INTEGER PRIMARY KEY,
            customer_id INTEGER NOT NULL CONSTRAINT fk_order_customer REFERENCES customer (id),
            total REAL NOT NULL DEFAULT 0 CHECK (total >= 0),
            note TEXT);
        CREATE INDEX ix_order_customer ON "order" (customer_id);
        CREATE TABLE gone (id INTEGER NOT NULL, payload TEXT);
        CREATE TABLE tagged (id INTEGER PRIMARY KEY, label TEXT);
        CREATE VIEW v_customer AS SELECT id, name FROM customer;
        CREATE VIEW v_orders AS SELECT o.id, c.name FROM "order" o JOIN v_customer c ON c.id = o.customer_id;
        CREATE TRIGGER tr_order AFTER INSERT ON "order" BEGIN UPDATE "order" SET note = 'new' WHERE id = NEW.id; END;
        CREATE TRIGGER tr_customer AFTER UPDATE ON customer BEGIN UPDATE "order" SET note = 'customer changed' WHERE customer_id = NEW.id; END;
        CREATE VIRTUAL TABLE doc USING fts5 (body);
        INSERT INTO customer (id, name, email, old, code) VALUES (1, 'Ann', 'ann@example.com', 7, 'XYZ'), (2, 'Bob', NULL, NULL, 'ABC');
        INSERT INTO "order" (id, customer_id, total) VALUES (10, 1, 5), (11, 2, 7.5);
        INSERT INTO tagged (id, label) VALUES (1, 'a');
        """;

    // Rename + add + drop + retype on customer (a rebuild of a table "order" refers to), an added column on tagged,
    // a dropped table, a changed index, a new table and view, and a changed trigger.
    private const string Edits = """
        CREATE TABLE customer_new (
            id INTEGER PRIMARY KEY,
            full_name TEXT NOT NULL,
            email TEXT COLLATE NOCASE,
            code TEXT NOT NULL DEFAULT 'ABC',
            phone TEXT);
        INSERT INTO customer_new (id, full_name, email, code) SELECT id, name, email, code FROM customer;
        DROP VIEW v_orders;
        DROP VIEW v_customer;
        DROP TABLE customer;
        ALTER TABLE customer_new RENAME TO customer;
        CREATE VIEW v_customer AS SELECT id, full_name AS name FROM customer;
        CREATE VIEW v_orders AS SELECT o.id, c.name FROM "order" o JOIN v_customer c ON c.id = o.customer_id;
        CREATE TRIGGER tr_customer AFTER UPDATE ON customer BEGIN UPDATE "order" SET note = 'customer changed' WHERE customer_id = NEW.id; END;
        ALTER TABLE tagged ADD COLUMN color TEXT NOT NULL DEFAULT 'red';
        DROP TABLE gone;
        DROP INDEX ix_order_customer;
        CREATE INDEX ix_order_customer ON "order" (customer_id, total);
        CREATE TABLE region (code TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE) WITHOUT ROWID;
        CREATE INDEX ix_customer_email ON customer (email) WHERE email IS NOT NULL;
        DROP TRIGGER tr_order;
        CREATE TRIGGER tr_order AFTER INSERT ON "order" BEGIN UPDATE "order" SET note = 'created' WHERE id = NEW.id; END;
        """;

    [Fact]
    public async Task Deploy_turns_a_into_b_and_revert_turns_it_back()
    {
        await using var a = new TestDatabase();
        await using var b = new TestDatabase();
        await RunAsync(a, Base);
        await RunAsync(b, Base);
        await RunAsync(b, Edits, foreignKeysOff: true);
        var before = await DumpAsync(a);
        var target = await DumpAsync(b);

        var script = await ScriptAsync(a, b);

        Assert.Contains("~ column main.customer.name renamed to full_name", script.Summary);
        Assert.Contains("- column main.customer.old", script.Summary);
        Assert.Contains("+ column main.customer.phone", script.Summary);
        Assert.Contains("~ table main.customer (rebuilt)", script.Summary);
        Assert.Contains("+ column main.tagged.color", script.Summary);
        Assert.Contains("- table main.gone", script.Summary);
        Assert.Contains("+ table main.region", script.Summary);
        Assert.Contains("~ index main.ix_order_customer", script.Summary);
        Assert.Contains("~ trigger main.tr_order", script.Summary);
        Assert.True(script.DataLoss);
        Assert.Contains("ALTER TABLE tagged ADD COLUMN color TEXT NOT NULL DEFAULT 'red';", script.Deploy, StringComparison.Ordinal);
        Assert.StartsWith("PRAGMA foreign_keys = OFF;", script.Deploy, StringComparison.Ordinal);

        // The linter makes the generated change !notx, as 'bsm add --from-diff' does.
        Assert.Contains(provider.Linter.Lint(script.Deploy, new LintContext("x", "deploy", ChangeFlags.None)), d => d.Code == "BSM001");
        Assert.Empty(provider.Linter.Lint(script.Deploy, new LintContext("x", "deploy", ChangeFlags.NoTransaction)));

        await RunAsync(a, script.Deploy, script: script);
        Assert.Equal(target, await DumpAsync(a));
        Assert.Equal(["1|Ann|ann@example.com|XYZ|", "2|Bob||ABC|"], await RowsAsync(a, "SELECT id || '|' || full_name || '|' || coalesce(email, '') || '|' || code || '|' || coalesce(phone, '') AS r FROM customer ORDER BY id"));
        Assert.Equal(["10|1", "11|2"], await RowsAsync(a, "SELECT id || '|' || customer_id AS r FROM \"order\" ORDER BY id"));
        Assert.Equal(["1|red"], await RowsAsync(a, "SELECT id || '|' || color AS r FROM tagged"));
        Assert.Equal(string.Empty, (await ScriptAsync(a, b)).Deploy);

        await RunAsync(a, script.Revert, script: script);
        Assert.Equal(before, await DumpAsync(a));
        Assert.Equal(["1|Ann", "2|Bob"], await RowsAsync(a, "SELECT id || '|' || name AS r FROM customer ORDER BY id"));
        Assert.Equal(["Ann", "Bob"], await RowsAsync(a, "SELECT name AS r FROM v_orders ORDER BY id"));
    }

    [Fact]
    public async Task Simple_column_changes_use_alter_table_in_a_transaction()
    {
        await using var a = new TestDatabase();
        await using var b = new TestDatabase();
        await RunAsync(a, "CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT, b TEXT);\nCREATE VIEW v AS SELECT id, a FROM t;\nINSERT INTO t VALUES (1, 'x', 'y');");
        await RunAsync(b, "CREATE TABLE t (id INTEGER PRIMARY KEY, a2 TEXT, b TEXT);\nCREATE VIEW v AS SELECT id, a2 FROM t;");

        var script = await ScriptAsync(a, b);
        Assert.Contains("ALTER TABLE t RENAME COLUMN a TO a2;", script.Deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE", script.Deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("PRAGMA", script.Deploy, StringComparison.Ordinal);
        Assert.False(script.DataLoss);

        await RunAsync(a, script.Deploy, script: script);
        Assert.Equal(await DumpAsync(b), await DumpAsync(a));
        Assert.Equal(["x"], await RowsAsync(a, "SELECT a2 AS r FROM v"));
        await RunAsync(a, script.Revert, script: script);
        Assert.Equal(["x"], await RowsAsync(a, "SELECT a AS r FROM v"));
    }

    [Fact]
    public async Task Repeatable_objects_and_journal_tables_are_left_out()
    {
        await using var a = new TestDatabase();
        await using var b = new TestDatabase();
        await RunAsync(a, "CREATE TABLE t (id INTEGER NOT NULL);\nCREATE VIEW v AS SELECT id FROM t;");
        await RunAsync(b, "CREATE TABLE t (id INTEGER NOT NULL, name INTEGER);\nCREATE VIEW v AS SELECT id, name FROM t;\nCREATE TABLE flyway_schema_history (installed_rank INTEGER NOT NULL);\nCREATE TABLE bsm_changes (x INTEGER);");

        await using var from = await SessionAsync(a);
        await using var to = await SessionAsync(b);
        var script = await provider.Scripter.ScriptAsync(from, to, new SchemaScriptOptions
        {
            StateSchema = "bsm",
            Exclude = ["*.flyway_schema_history"],
            RepeatableScripts = ["DROP VIEW IF EXISTS v;\nCREATE VIEW \"v\" AS SELECT 1 AS x;"],
        }, CancellationToken.None);

        Assert.Equal(["+ column main.t.name"], script.Summary);
        Assert.Equal("ALTER TABLE t ADD COLUMN name INTEGER;\n", script.Deploy);
    }

    [Fact]
    public void Repeatable_scripts_own_the_views_and_triggers_they_create()
    {
        var owned = SqliteScripter.OwnedObjects([
            "DROP VIEW IF EXISTS vA; CREATE VIEW vA AS SELECT 1;",
            "create temp view if not exists main.\"v B\" as select 1",
            "CREATE TRIGGER IF NOT EXISTS [tr] AFTER INSERT ON t BEGIN SELECT 1; END;",
            "-- CREATE TABLE not_owned (id int)",
        ]);
        Assert.True(owned.SetEquals(["view vA", "view v B", "trigger tr"]), string.Join(", ", owned));
    }

    private async Task<SchemaScript> ScriptAsync(TestDatabase from, TestDatabase to)
    {
        await using var a = await SessionAsync(from);
        await using var b = await SessionAsync(to);
        return await provider.Scripter.ScriptAsync(a, b, new SchemaScriptOptions { StateSchema = "bsm" }, CancellationToken.None);
    }

    private async Task<string> DumpAsync(TestDatabase db)
    {
        await using var session = await SessionAsync(db);
        return await provider.Schema.CaptureAsync(session, "bsm", CancellationToken.None);
    }

    private async Task<List<string>> RowsAsync(TestDatabase db, string sql)
    {
        await using var session = await SessionAsync(db);
        return (await session.QueryAsync(sql, null, CancellationToken.None)).Select(r => (string)r["r"]!).ToList();
    }

    /// <summary>Runs a script as a change would: in one transaction, unless it manages its own (foreign keys off).</summary>
    private async Task RunAsync(TestDatabase db, string sql, bool foreignKeysOff = false, SchemaScript? script = null)
    {
        await using var session = await SessionAsync(db);
        var own = sql.Contains("PRAGMA foreign_keys = OFF", StringComparison.Ordinal);
        if (foreignKeysOff)
        {
            await session.ExecuteAsync("PRAGMA foreign_keys = OFF;", null, CancellationToken.None);
        }

        if (!own)
        {
            await session.BeginTransactionAsync(CancellationToken.None);
        }

        foreach (var batch in provider.Parser.Split(sql))
        {
            try
            {
                await session.ExecuteBatchAsync(batch, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"{ex.Message}\n--- statement ---\n{batch.Text}\n--- script ---\n{sql}{(script is null ? string.Empty : "\n--- summary ---\n" + string.Join('\n', script.Summary))}", ex);
            }
        }

        if (!own)
        {
            await session.CommitAsync(CancellationToken.None);
        }
    }

    private Task<IDatabaseSession> SessionAsync(TestDatabase db) => provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
}
