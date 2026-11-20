using Badger.Trek.Core.Plan;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql.Tests;

public sealed class SplitterAndLinterTests
{
    private static List<string> Split(string script) => [.. new MySqlStatementSplitter().Split(script).Select(b => b.Text)];

    private static List<LintDiagnostic> Lint(string script) => [.. new MySqlLinter().Lint(script, new LintContext("c", "deploy", ChangeFlags.None))];

    [Fact]
    public void Statements_end_at_semicolons_outside_quotes_and_comments()
    {
        var batches = Split("""
            CREATE TABLE t (id INT, note VARCHAR(20) DEFAULT 'a;b', `odd;name` INT); -- trailing; comment
            INSERT INTO t (id, note) VALUES (1, "x;y"), (2, 'it''s; \' fine');
            # hash comment; with semicolon
            /* block; comment */ UPDATE t SET id = id --1
            ;
            """);

        Assert.Equal(3, batches.Count);
        Assert.StartsWith("CREATE TABLE t", batches[0], StringComparison.Ordinal);
        Assert.EndsWith("`odd;name` INT)", batches[0], StringComparison.Ordinal);
        Assert.Contains("'it''s; \\' fine'", batches[1], StringComparison.Ordinal);
        Assert.Contains("UPDATE t SET id = id --1", batches[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Delimiter_lines_keep_stored_program_bodies_whole_and_are_not_sent()
    {
        var parsed = new MySqlStatementSplitter().Split("""
            CREATE TABLE audit (id INT);

            DELIMITER //
            CREATE PROCEDURE add_audit(IN v INT)
            BEGIN
                INSERT INTO audit VALUES (v);
                SELECT COUNT(*) FROM audit;
            END //
            DELIMITER ;

            CALL add_audit(1);
            """);

        Assert.Equal(["CREATE TABLE audit (id INT)", "CALL add_audit(1)"], [parsed[0].Text, parsed[2].Text]);
        Assert.StartsWith("CREATE PROCEDURE add_audit(IN v INT)", parsed[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("END", parsed[1].Text, StringComparison.Ordinal);
        Assert.Contains("SELECT COUNT(*) FROM audit;", parsed[1].Text, StringComparison.Ordinal);
        Assert.Equal(4, parsed[1].StartLine);
        Assert.DoesNotContain(parsed, b => b.Text.Contains("DELIMITER", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Comment_only_statements_are_dropped_but_versioned_comments_run()
    {
        var batches = Split("-- just a comment;\n/* and another */;\n/*!80000 SET @x = 1 */;\nSELECT 1;");

        Assert.Equal(["/*!80000 SET @x = 1 */", "SELECT 1"], batches);
    }

    [Fact]
    public void Use_and_client_commands_are_errors()
    {
        var diagnostics = Lint("USE other_db;\nsource seed.sql;\nSELECT 1;");

        Assert.Equal(["TREK009", "TREK005"], diagnostics.Select(d => d.Code));
        Assert.Equal([1, 2], diagnostics.Select(d => d.Line));
    }

    [Fact]
    public void A_stored_program_cut_at_its_first_semicolon_asks_for_a_delimiter()
    {
        var missing = Lint("CREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END;");
        Assert.Contains(missing, d => d.Code == "TREK005" && d.Message.Contains("DELIMITER //", StringComparison.Ordinal));

        Assert.Empty(Lint("DELIMITER //\nCREATE PROCEDURE p() BEGIN SELECT 1; SELECT 2; END //\nDELIMITER ;"));
        Assert.Empty(Lint("CREATE TRIGGER t BEFORE INSERT ON x FOR EACH ROW SET NEW.a = 1;"));
    }

    [Theory]
    [InlineData("8.0.46", "past its end of life")]
    [InlineData("8.3.0", "past its end of life")]
    [InlineData("8.4.9", "")]
    [InlineData("9.1.0", "Innovation release")]
    [InlineData("9.7.0", "")]
    [InlineData("26.7.0", "not tested")]
    [InlineData("10.6.21-MariaDB", "past its end of life")]
    [InlineData("10.11.20-MariaDB-ubu2404", "")]
    [InlineData("11.4.13-MariaDB", "")]
    [InlineData("11.6.2-MariaDB", "short-term release")]
    [InlineData("11.8.10-MariaDB-log", "")]
    [InlineData("12.0.1-MariaDB", "short-term release")]
    [InlineData("12.3.4-MariaDB", "")]
    [InlineData("13.0.0-MariaDB", "not tested")]
    public void Doctor_notes_unsupported_versions(string version, string note)
    {
        var text = MySqlProvider.UnsupportedVersionNote(version);
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
    public void The_fingerprint_leaves_out_the_database_name_mysql_writes_into_definitions()
    {
        Assert.Equal(
            "select `orders`.`id` AS `id` from `orders` join `shared`.`regions`",
            MySqlSchemaInspector.Unqualify("select `shop_prod`.`orders`.`id` AS `id` from `shop_prod`.`orders` join `shared`.`regions`", "shop_prod"));
    }

    [Fact]
    public void Moving_a_journal_is_one_rename()
    {
        Assert.Equal(
            "RENAME TABLE `bsm_meta` TO `trek_meta`, `bsm_changes` TO `trek_changes`, `bsm_repeatables` TO `trek_repeatables`, `bsm_events` TO `trek_events`, `bsm_schema_snapshots` TO `trek_schema_snapshots`;",
            new MySqlStateStore().MoveScript("bsm", "trek"));
    }
}
