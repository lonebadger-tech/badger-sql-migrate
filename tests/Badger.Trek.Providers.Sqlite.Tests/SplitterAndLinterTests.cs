using Badger.Trek.Core.Plan;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Sqlite.Tests;

public sealed class SplitterAndLinterTests
{
    private readonly SqliteStatementSplitter splitter = new();
    private readonly SqliteLinter linter = new();

    [Fact]
    public void Statements_split_on_semicolons_outside_strings_comments_and_trigger_bodies()
    {
        const string script = """
            -- leading comment; not a statement
            CREATE TABLE t (id INTEGER, note TEXT DEFAULT 'a;b', "odd;name" TEXT, [x;y] TEXT, `z;w` TEXT);
            /* block; comment */
            CREATE TRIGGER tr AFTER INSERT ON t
            BEGIN
              UPDATE t SET note = CASE WHEN NEW.id > 0 THEN 'pos;' ELSE 'neg' END WHERE id = NEW.id;
              DELETE FROM t WHERE id < 0;
            END;
            CREATE TEMP TRIGGER tr2 BEFORE DELETE ON t BEGIN SELECT 1; END;
            ;
            INSERT INTO t (id) VALUES (1)
            """;
        var batches = splitter.Split(script);

        Assert.Equal(4, batches.Count);
        Assert.StartsWith("CREATE TABLE t", batches[0].Text, StringComparison.Ordinal);
        Assert.Equal(2, batches[0].StartLine);
        Assert.StartsWith("CREATE TRIGGER tr AFTER", batches[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("END;", batches[1].Text, StringComparison.Ordinal);
        Assert.Equal(4, batches[1].StartLine);
        Assert.Equal("CREATE TEMP TRIGGER tr2 BEFORE DELETE ON t BEGIN SELECT 1; END;", batches[2].Text);
        Assert.Equal("INSERT INTO t (id) VALUES (1)", batches[3].Text);
        Assert.Equal(11, batches[3].StartLine);
    }

    [Fact]
    public void Comment_only_scripts_have_no_statements()
    {
        Assert.Empty(splitter.Split("-- nothing here\n/* or here */\n"));
    }

    [Fact]
    public void Dot_commands_are_reported_not_run()
    {
        var batches = splitter.Split(".mode column\nSELECT 1;\n.read other.sql\n");
        Assert.Equal(["SELECT 1;"], batches.Select(b => b.Text));

        var diagnostics = linter.Lint(".mode column\nSELECT 1;\n.read other.sql\n", Context());
        Assert.Equal([("TREK005", 1), ("TREK005", 3)], diagnostics.Select(d => (d.Code, d.Line)));
    }

    [Fact]
    public void A_dot_inside_a_statement_is_not_a_command()
    {
        var batches = splitter.Split("SELECT 1\n.5 AS x;");
        Assert.Single(batches);
        Assert.Empty(linter.Lint("SELECT 1\n.5 AS x;", Context()));
    }

    [Theory]
    [InlineData("VACUUM;", "TREK001")]
    [InlineData("ATTACH DATABASE 'x.db' AS x;", "TREK001")]
    [InlineData("PRAGMA journal_mode = WAL;", "TREK001")]
    [InlineData("PRAGMA foreign_keys = OFF;", "TREK001")]
    [InlineData("BEGIN;", "TREK003")]
    [InlineData("COMMIT;", "TREK003")]
    [InlineData("ROLLBACK;", "TREK003")]
    public void Statements_that_cannot_run_in_trek_transaction_are_errors(string sql, string code)
    {
        var diagnostics = linter.Lint("-- note\n" + sql, Context());
        var d = Assert.Single(diagnostics);
        Assert.Equal(code, d.Code);
        Assert.Equal(LintSeverity.Error, d.Severity);
        Assert.Equal(2, d.Line);
        Assert.Empty(linter.Lint(sql, Context(ChangeFlags.NoTransaction)));
    }

    [Theory]
    [InlineData("SAVEPOINT a; ROLLBACK TO a; RELEASE a;")]
    [InlineData("PRAGMA defer_foreign_keys = ON;")]
    [InlineData("CREATE TRIGGER tr AFTER INSERT ON t BEGIN UPDATE t SET x = 1; END;")]
    public void Statements_that_work_in_a_transaction_pass(string sql)
    {
        Assert.Empty(linter.Lint(sql, Context()));
    }

    [Fact]
    public void Normalization_ignores_layout_and_identifier_quoting()
    {
        Assert.Equal(
            SqliteLexer.Normalize("CREATE INDEX ix ON t (a, b)"),
            SqliteLexer.Normalize("CREATE  INDEX \"ix\"\n  ON [t](a,`b`) -- comment"));
        Assert.Equal("CREATE VIEW \"my view\" AS SELECT 'a  b'", SqliteLexer.Normalize("CREATE VIEW \"my view\" AS  SELECT 'a  b'"));
    }

    private static LintContext Context(ChangeFlags flags = ChangeFlags.None) => new("change", "deploy", flags);

    [Fact]
    public void Sql_newer_than_the_minimum_version_is_a_warning()
    {
        const string script = """
            CREATE TABLE t (id INTEGER PRIMARY KEY, a TEXT, b TEXT, c TEXT GENERATED ALWAYS AS (upper(a))) STRICT;
            ALTER TABLE t DROP COLUMN b;
            INSERT INTO t (a) VALUES ('RETURNING ->> iif(') RETURNING id;
            UPDATE t SET a = s.a FROM (SELECT 1 AS id, 'x' AS a) AS s WHERE s.id = t.id;
            -- iif(x) in a comment is fine
            UPDATE t SET a = (SELECT a FROM t AS u WHERE u.id = t.id);
            SELECT a ->> '$.x', "iif(" FROM t;
            """;

        var warnings = linter.Lint(script, new LintContext("x", "deploy", ChangeFlags.None)).Where(d => d.Code == "TREK008").ToList();

        Assert.All(warnings, w => Assert.Equal(LintSeverity.Warning, w.Severity));
        Assert.Equal(
            [(1, "A generated column"), (1, "A STRICT table"), (2, "ALTER TABLE ... DROP COLUMN"), (3, "RETURNING"), (4, "UPDATE ... FROM"), (7, "The -> and ->> operators")],
            warnings.Select(w => (w.Line, w.Message[..w.Message.IndexOf(" needs SQLite", StringComparison.Ordinal)])));
        Assert.Contains("needs SQLite 3.35 or later, but sqlite.minVersion in trek.yaml is 3.26", warnings[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_higher_minimum_version_allows_newer_sql()
    {
        var newer = new SqliteLinter(new SqliteOptions { MinVersion = new Version(3, 37) });

        var codes = newer.Lint("ALTER TABLE t DROP COLUMN b;\nCREATE TABLE s (id INTEGER) STRICT;\nSELECT unixepoch();", new LintContext("x", "deploy", ChangeFlags.None))
            .Where(d => d.Code == "TREK008").Select(d => d.Message).ToList();

        Assert.Equal(["unixepoch() needs SQLite 3.38 or later, but sqlite.minVersion in trek.yaml is 3.37. Rewrite it for older engines, or raise sqlite.minVersion if every program that opens this database has SQLite 3.38 or later."], codes);
    }
}
