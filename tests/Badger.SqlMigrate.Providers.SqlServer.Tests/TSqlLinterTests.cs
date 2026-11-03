using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;

namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

public class TSqlLinterTests
{
    private readonly TSqlLinter linter = new();

    [Theory]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY SIMPLE;")]
    [InlineData("CREATE FULLTEXT CATALOG ftc;")]
    [InlineData("RECONFIGURE;")]
    public void Non_transactional_statements_require_notx(string sql)
    {
        var d = Assert.Single(linter.Lint(sql, new LintContext("c", "deploy", ChangeFlags.None)));
        Assert.Equal(LintSeverity.Error, d.Severity);
        Assert.Equal("BSM001", d.Code);

        Assert.Empty(linter.Lint(sql, new LintContext("c", "deploy", ChangeFlags.NoTransaction)));
    }

    [Fact]
    public void Use_statement_is_an_error()
    {
        Assert.Contains(linter.Lint("USE master;", new LintContext("c", "deploy", ChangeFlags.None)), d => d.Code == "BSM002");
    }

    [Fact]
    public void Explicit_transaction_is_a_warning()
    {
        var d = Assert.Single(linter.Lint("BEGIN TRAN; SELECT 1; COMMIT;", new LintContext("c", "deploy", ChangeFlags.None)));
        Assert.Equal(LintSeverity.Warning, d.Severity);
    }

    [Fact]
    public void Sqlcmd_variables_do_not_break_parsing_and_go_is_understood()
    {
        Assert.Empty(linter.Lint("CREATE TABLE [$(Schema)].t (id int);\nGO\nSELECT N'$(Env)';", new LintContext("c", "deploy", ChangeFlags.None)));
    }

    [Fact]
    public void Parse_errors_are_warnings_not_errors()
    {
        var d = Assert.Single(linter.Lint("CREATE TABLE (", new LintContext("c", "deploy", ChangeFlags.None)));
        Assert.Equal(LintSeverity.Warning, d.Severity);
    }
}

public class TemporalLintTests
{
    private readonly TSqlLinter linter = new();

    private IReadOnlyList<LintDiagnostic> Lint(string sql) => linter.Lint(sql, new LintContext("c", "deploy", ChangeFlags.None));

    [Fact]
    public void Turning_versioning_off_and_not_back_on_is_a_warning()
    {
        var d = Assert.Single(Lint("ALTER TABLE dbo.Price SET (SYSTEM_VERSIONING = OFF);\nALTER TABLE dbo.Price ADD Note nvarchar(100) NULL;"));
        Assert.Equal(("BSM004", LintSeverity.Warning, 1), (d.Code, d.Severity, d.Line));
        Assert.Contains("dbo.Price", d.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ALTER TABLE dbo.Price SET (SYSTEM_VERSIONING = OFF);\nALTER TABLE dbo.Price ADD Note nvarchar(100) NULL;\nALTER TABLE dbo.PriceHistory ADD Note nvarchar(100) NULL;\nALTER TABLE [dbo].[Price] SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PriceHistory));")]
    [InlineData("ALTER TABLE Price SET (SYSTEM_VERSIONING = OFF);\nDROP TABLE dbo.Price, dbo.PriceHistory;")]
    [InlineData("CREATE TABLE dbo.T (Id int PRIMARY KEY, F datetime2 GENERATED ALWAYS AS ROW START, U datetime2 GENERATED ALWAYS AS ROW END, PERIOD FOR SYSTEM_TIME (F, U)) WITH (SYSTEM_VERSIONING = ON);")]
    public void Versioning_restored_or_table_dropped_is_fine(string sql) => Assert.Empty(Lint(sql));
}
