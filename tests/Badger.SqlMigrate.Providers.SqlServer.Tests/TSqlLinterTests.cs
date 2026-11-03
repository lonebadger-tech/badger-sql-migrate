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
