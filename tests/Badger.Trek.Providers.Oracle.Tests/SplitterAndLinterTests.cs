using System.Text.Json.Nodes;
using Badger.Trek.Core;
using Badger.Trek.Core.Plan;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Oracle.Tests;

public sealed class SplitterAndLinterTests
{
    private static readonly LintContext Context = new("c", "deploy", ChangeFlags.None);

    private static IReadOnlyList<ScriptBatch> Split(string script) => new OracleStatementSplitter().Split(script.Replace("\r\n", "\n", StringComparison.Ordinal));

    [Fact]
    public void Sql_statements_end_at_semicolons_and_lose_them()
    {
        var batches = Split("""
            CREATE TABLE t (id NUMBER, note VARCHAR2(20) DEFAULT 'a;b');
            -- a comment; with a semicolon
            INSERT INTO t (id, note) VALUES (1, q'[it's; fine]'); INSERT INTO t VALUES (2, 'x');
            /* block; comment */
            COMMENT ON TABLE t IS 'one
            two; three'
            ;
            """);
        Assert.Equal(
            ["CREATE TABLE t (id NUMBER, note VARCHAR2(20) DEFAULT 'a;b')", "INSERT INTO t (id, note) VALUES (1, q'[it's; fine]')", "INSERT INTO t VALUES (2, 'x')", "/* block; comment */\nCOMMENT ON TABLE t IS 'one\ntwo; three'"],
            batches.Select(b => b.Text));
        Assert.Equal([1, 3, 3, 4], batches.Select(b => b.StartLine));
    }

    [Fact]
    public void Plsql_blocks_and_stored_programs_end_at_a_slash_line_and_keep_their_end()
    {
        var batches = Split("""
            CREATE OR REPLACE PROCEDURE add_order(p_total IN NUMBER) AS
              v_id NUMBER;
            BEGIN
              INSERT INTO orders (total) VALUES (p_total) RETURNING id INTO v_id;
              COMMIT;
            END;
            /
            CREATE OR REPLACE EDITIONABLE PACKAGE BODY shop AS
              FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END;
            END shop;
            /
            BEGIN
              DBMS_OUTPUT.PUT_LINE('a; / b');
            END;
            /
            CREATE TYPE money AS OBJECT (amount NUMBER, currency CHAR(3));
            /
            CREATE TRIGGER orders_bi BEFORE INSERT ON orders FOR EACH ROW
            BEGIN
              :NEW.created := SYSTIMESTAMP;
            END;
            /
            SELECT 1 FROM dual
            /
            """);
        Assert.Equal(6, batches.Count);
        Assert.EndsWith("COMMIT;\nEND;", batches[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("CREATE OR REPLACE EDITIONABLE PACKAGE BODY", batches[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("END shop;", batches[1].Text, StringComparison.Ordinal);
        Assert.Contains("'a; / b'", batches[2].Text, StringComparison.Ordinal);
        Assert.Equal("CREATE TYPE money AS OBJECT (amount NUMBER, currency CHAR(3));", batches[3].Text);
        Assert.Contains(":NEW.created := SYSTIMESTAMP;", batches[4].Text, StringComparison.Ordinal);
        Assert.Equal("SELECT 1 FROM dual", batches[5].Text);
        Assert.Equal([1, 8, 12, 16, 18, 23], batches.Select(b => b.StartLine));
    }

    [Fact]
    public void Sqlplus_settings_are_not_sent_and_exec_becomes_a_block()
    {
        var batches = Split("""
            SET DEFINE OFF
            PROMPT Creating tables...
            REM old comment
            WHENEVER SQLERROR EXIT FAILURE
            CREATE TABLE r_and_d (name VARCHAR2(10) DEFAULT 'R&D');
            EXEC dbms_stats.gather_table_stats(USER, 'R_AND_D');
            EXECUTE refresh_all
            """);
        Assert.Equal(
            ["CREATE TABLE r_and_d (name VARCHAR2(10) DEFAULT 'R&D')", "BEGIN dbms_stats.gather_table_stats(USER, 'R_AND_D'); END;", "BEGIN refresh_all; END;"],
            batches.Select(b => b.Text));
    }

    [Fact]
    public void Lint_reports_sqlplus_commands_current_schema_and_a_missing_slash()
    {
        var linter = new OracleLinter();
        var diagnostics = linter.Lint("""
            @other_script.sql
            CONNECT app/secret@db
            ALTER SESSION SET CURRENT_SCHEMA = other;
            CREATE OR REPLACE PROCEDURE p AS
            BEGIN
              NULL;
            END;
            CREATE TABLE t (id NUMBER);
            """, Context);
        Assert.Equal(["TREK005", "TREK005", "TREK009", "TREK005"], diagnostics.Select(d => d.Code));
        Assert.Contains("CREATE", diagnostics[3].Message, StringComparison.Ordinal);
        Assert.All(diagnostics, d => Assert.Equal(LintSeverity.Error, d.Severity));
    }

    [Fact]
    public void Lint_warns_about_26ai_syntax_only_while_min_version_is_19()
    {
        const string Script = """
            CREATE TABLE IF NOT EXISTS flags (id NUMBER PRIMARY KEY, active BOOLEAN DEFAULT TRUE, embedding VECTOR(3, FLOAT32));
            DROP VIEW IF EXISTS old_view;
            CREATE OR REPLACE PROCEDURE p AS v BOOLEAN := TRUE; BEGIN NULL; END;
            /
            INSERT INTO flags (id) VALUES (1);
            """;
        var older = new OracleLinter(OracleOptions.Default).Lint(Script, Context);
        Assert.All(older, d => Assert.Equal("TREK008", d.Code));
        Assert.Contains(older, d => d.Line == 1 && d.Message.StartsWith("IF [NOT] EXISTS", StringComparison.Ordinal));
        Assert.Contains(older, d => d.Line == 1 && d.Message.StartsWith("A BOOLEAN column", StringComparison.Ordinal));
        Assert.Contains(older, d => d.Line == 1 && d.Message.StartsWith("The VECTOR type", StringComparison.Ordinal));
        Assert.Contains(older, d => d.Line == 2);
        Assert.DoesNotContain(older, d => d.Line >= 3);

        Assert.Empty(new OracleLinter(OracleOptions.Parse(new JsonObject { ["minVersion"] = "26" })).Lint(Script, Context));
    }

    [Theory]
    [InlineData("19", 19)]
    [InlineData("19c", 19)]
    [InlineData("26ai", 23)]
    [InlineData("23", 23)]
    public void Min_version_accepts_19_and_26(string value, int expected) =>
        Assert.Equal(expected, OracleOptions.Parse(new JsonObject { ["minVersion"] = value }).MinVersion);

    [Fact]
    public void Min_version_rejects_other_releases_and_settings()
    {
        Assert.Throws<MigrationException>(() => OracleOptions.Parse(new JsonObject { ["minVersion"] = "21" }));
        Assert.Throws<MigrationException>(() => OracleOptions.Parse(new JsonObject { ["other"] = "x" }));
    }

    [Theory]
    [InlineData("19.29.0.0.0", "")]
    [InlineData("23.26.0.0.0", "")]
    [InlineData("21.3.0.0.0", "innovation release")]
    [InlineData("12.2.0.1.0", "past Premier Support")]
    [InlineData("29.1.0.0.0", "not tested")]
    public void Doctor_notes_unsupported_versions(string version, string note)
    {
        var text = OracleProvider.UnsupportedVersionNote(version);
        if (note.Length == 0)
        {
            Assert.Equal(string.Empty, text);
        }
        else
        {
            Assert.Contains(note, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_fingerprint_leaves_out_the_schema_name_in_definitions()
    {
        Assert.Equal(
            "SELECT o.id FROM orders o JOIN OTHER.regions r ON r.id = o.region_id",
            OracleSchemaInspector.Unqualify("SELECT o.id FROM \"SHOP_PROD\".orders o JOIN OTHER.regions r ON r.id = o.region_id", "SHOP_PROD"));
        Assert.Equal("SELECT id FROM orders", OracleSchemaInspector.Unqualify("SELECT id FROM shop_prod.orders", "SHOP_PROD"));
    }

    [Fact]
    public void Environments_need_a_service_and_take_only_the_schema_setting()
    {
        var provider = new OracleProvider();
        Assert.Contains(provider.ValidateEnvironment(new Core.Configuration.DeploymentEnvironment { Name = "x", Host = "db", Authentication = Core.Configuration.AuthenticationKind.Password, User = "app" }),
            p => p.Contains("service name", StringComparison.Ordinal));
        Assert.Contains(provider.ValidateEnvironment(new Core.Configuration.DeploymentEnvironment { Name = "x", Host = "db", Database = "PDB1", Authentication = Core.Configuration.AuthenticationKind.EntraDefault, User = "app" }),
            p => p.Contains("not available for Oracle", StringComparison.Ordinal));
        Assert.Contains(provider.ValidateEnvironment(new Core.Configuration.DeploymentEnvironment { Name = "x", Host = "db", Database = "PDB1", ProviderSettings = new JsonObject { ["owner"] = "x" } }),
            p => p.Contains("unknown Oracle setting 'owner'", StringComparison.Ordinal));
        Assert.Empty(provider.ValidateEnvironment(new Core.Configuration.DeploymentEnvironment { Name = "x", Host = "db", Database = "PDB1", Authentication = Core.Configuration.AuthenticationKind.Password, User = "app", ProviderSettings = new JsonObject { ["schema"] = "shop" } }));
        Assert.Equal("SHOP", provider.ResolveDatabaseName(new Core.Configuration.DeploymentEnvironment { Name = "x", User = "app", ProviderSettings = new JsonObject { ["schema"] = "shop" } }));
        Assert.Equal("APP", provider.ResolveDatabaseName(new Core.Configuration.DeploymentEnvironment { Name = "x", User = "app" }));
    }

    [Theory]
    [InlineData("User Id=a;Data Source=db.example.com:1522/SALES", "db.example.com", 1522)]
    [InlineData("User Id=a;Data Source=db.example.com/SALES", "db.example.com", 1521)]
    [InlineData("User Id=a;Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=adb.example.com)(PORT=1522))(CONNECT_DATA=(SERVICE_NAME=x)))", "adb.example.com", 1522)]
    public void The_endpoint_comes_from_ezconnect_or_a_descriptor(string connectionString, string host, int port) =>
        Assert.Equal(new Endpoint(host, port), new OracleProvider().ResolveEndpoint(new Core.Configuration.DeploymentEnvironment { Name = "x", ConnectionString = connectionString }));
}
