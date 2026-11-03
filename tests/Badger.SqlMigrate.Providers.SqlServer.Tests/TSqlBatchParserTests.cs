using Badger.SqlMigrate.Providers.SqlServer;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

public class TSqlBatchParserTests
{
    private readonly TSqlBatchParser parser = new();

    [Fact]
    public void Splits_on_go_lines_case_insensitively_with_trailing_comment()
    {
        var batches = parser.Split("CREATE TABLE a (id int);\nGO\nCREATE TABLE b (id int);\n  go  -- done\nSELECT 1;");

        Assert.Equal(["CREATE TABLE a (id int);", "CREATE TABLE b (id int);", "SELECT 1;"], batches.Select(b => b.Text));
        Assert.Equal([1, 3, 5], batches.Select(b => b.StartLine));
    }

    [Fact]
    public void Supports_go_repeat_count()
    {
        var batch = Assert.Single(parser.Split("INSERT t DEFAULT VALUES;\nGO 5\n"));
        Assert.Equal(5, batch.RepeatCount);
    }

    [Fact]
    public void Ignores_go_inside_block_comments_including_nested()
    {
        var batches = parser.Split("/* outer\n/* inner */\nGO\n*/\nSELECT 1;\nGO\nSELECT 2;");
        Assert.Equal(2, batches.Count);
        Assert.Contains("GO", batches[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignores_go_inside_multiline_strings_and_brackets()
    {
        var batches = parser.Split("SELECT 'it''s\nGO\nstill a string';\nSELECT [weird\nGO\nname];\nGO\nSELECT 2;");
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Does_not_split_on_identifiers_starting_with_go()
    {
        var batch = Assert.Single(parser.Split("SELECT 1\nGOTO label\nGOOD"));
        Assert.Contains("GOTO", batch.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_comments_do_not_open_strings()
    {
        var batches = parser.Split("-- it's a comment\nSELECT 1;\nGO\nSELECT 2;");
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void Empty_batches_are_dropped()
    {
        Assert.Single(parser.Split("GO\n\nGO\nSELECT 1;\nGO\n\n"));
    }
}

public class SchemaInspectorParseTests
{
    [Fact]
    public void Groups_module_lines_and_keys_entries_by_object()
    {
        var dump = string.Join('\n',
            "column|dbo.Customer|1|Id|int|4|10|0|0|0||||<system>|0|0|||",
            "column|dbo.Customer|2|Email|nvarchar|200|0|0|1|0|SQL_Latin1_General_CP1_CI_AS|||<system>|0|0|||",
            "index|dbo.Customer|<system>|CLUSTERED|1|1|0||Id",
            "module|V |dbo.vCustomer|CREATE VIEW dbo.vCustomer",
            "AS",
            "SELECT Id FROM dbo.Customer",
            "object|U |dbo.Customer",
            "partitioned|dbo.Sale|PK_Sale|psSaleDate|SaleDate",
            "pfunction|pfSaleDate|date|3|10|0||RIGHT|2026-01-01",
            "pscheme|psSaleDate|pfSaleDate|PRIMARY,PRIMARY",
            "schema|sales",
            "temporal|dbo.Price|SYSTEM_VERSIONED_TEMPORAL_TABLE|ValidFrom|ValidTo|dbo.PriceHistory||");

        var entries = new SqlServerSchemaInspector().Parse(dump);

        Assert.Equal(
            ["column dbo.Customer.Id", "column dbo.Customer.Email", "index dbo.Customer.<system>", "module dbo.vCustomer", "object dbo.Customer", "partitioned dbo.Sale.PK_Sale", "pfunction pfSaleDate", "pscheme psSaleDate", "schema sales", "temporal dbo.Price"],
            entries.Select(e => e.Key));
        Assert.Equal("module|V |dbo.vCustomer|CREATE VIEW dbo.vCustomer\nAS\nSELECT Id FROM dbo.Customer", entries[3].Text);
        Assert.Empty(new SqlServerSchemaInspector().Parse(string.Empty));
    }
}
