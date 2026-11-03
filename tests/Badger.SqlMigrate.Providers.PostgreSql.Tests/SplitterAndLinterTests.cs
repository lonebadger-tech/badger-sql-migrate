using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.PostgreSql;

namespace Badger.SqlMigrate.Providers.PostgreSql.Tests;

public class SplitterTests
{
    private readonly PostgreSqlStatementSplitter splitter = new();

    [Fact]
    public void Splits_at_semicolons_outside_strings_identifiers_comments_and_dollar_quotes()
    {
        const string script = """
            -- leading comment; not a statement
            CREATE TABLE "semi;colon" (note text DEFAULT 'a;b', esc text DEFAULT E'it\'s;');
            /* block ; /* nested ; */ still comment ; */
            CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$
            BEGIN
              RETURN 1; -- inside the body
            END;
            $$;
            DO $body$ BEGIN PERFORM 1; END $body$;
            SELECT $1::int AS p;
            SELECT 'no semicolon at the end'
            """;

        var batches = splitter.Split(script);

        Assert.Equal(5, batches.Count);
        Assert.StartsWith("-- leading comment; not a statement\nCREATE TABLE \"semi;colon\"", batches[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("esc text DEFAULT E'it\\'s;');", batches[0].Text, StringComparison.Ordinal);
        Assert.StartsWith("/* block ; /* nested ; */ still comment ; */\nCREATE FUNCTION f()", batches[1].Text, StringComparison.Ordinal);
        Assert.EndsWith("END;\n$$;", batches[1].Text, StringComparison.Ordinal);
        Assert.Equal("DO $body$ BEGIN PERFORM 1; END $body$;", batches[2].Text);
        Assert.Equal("SELECT $1::int AS p;", batches[3].Text);
        Assert.Equal("SELECT 'no semicolon at the end'", batches[4].Text);
        Assert.Equal([2, 4, 9, 10, 11], batches.Select(b => b.StartLine));
    }

    [Fact]
    public void Comment_only_and_empty_scripts_have_no_statements()
    {
        Assert.Empty(splitter.Split("-- nothing here\n/* or here */\n  ;\n"));
        Assert.Empty(splitter.Split(string.Empty));
    }
}

public class LinterTests
{
    private readonly PostgreSqlLinter linter = new();

    private IReadOnlyList<LintDiagnostic> Lint(string sql, ChangeFlags flags = ChangeFlags.None) => linter.Lint(sql, new LintContext("c", "deploy", flags));

    [Theory]
    [InlineData("CREATE INDEX CONCURRENTLY ix ON t (a);")]
    [InlineData("create unique index concurrently ix on t (a);")]
    [InlineData("DROP INDEX CONCURRENTLY ix;")]
    [InlineData("REINDEX TABLE CONCURRENTLY t;")]
    [InlineData("VACUUM ANALYZE t;")]
    [InlineData("ALTER SYSTEM SET work_mem = '64MB';")]
    public void Statements_that_refuse_a_transaction_need_notx(string sql)
    {
        var d = Assert.Single(Lint(sql));
        Assert.Equal(("BSM001", LintSeverity.Error), (d.Code, d.Severity));
        Assert.Empty(Lint(sql, ChangeFlags.NoTransaction));
    }

    [Theory]
    [InlineData("BEGIN;")]
    [InlineData("COMMIT;")]
    [InlineData("START TRANSACTION;")]
    [InlineData("ROLLBACK;")]
    public void Transaction_control_inside_a_transactional_change_is_an_error(string sql)
    {
        Assert.Equal("BSM003", Assert.Single(Lint(sql)).Code);
        Assert.Empty(Lint(sql, ChangeFlags.NoTransaction));
    }

    [Fact]
    public void Function_bodies_savepoints_and_ordinary_statements_are_fine()
    {
        Assert.Empty(Lint("""
            CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $$ BEGIN COMMIT; END $$;
            DO $$ BEGIN RAISE NOTICE 'hi'; END $$;
            SAVEPOINT s1;
            ROLLBACK TO SAVEPOINT s1;
            CREATE INDEX ix ON t (a);
            """));
    }

    [Fact]
    public void Psql_meta_commands_copy_from_stdin_and_enum_values_are_reported()
    {
        var d = Lint("\\set ON_ERROR_STOP on\nSELECT 1;\nCOPY t (a) FROM STDIN;\nALTER TYPE mood ADD VALUE 'meh';");
        Assert.Equal(["BSM005", "BSM006", "BSM007"], d.Select(x => x.Code));
        Assert.Equal(1, d[0].Line);
        Assert.Equal(LintSeverity.Warning, d[2].Severity);
    }
}

public class ConnectionTests
{
    private static async Task<Npgsql.NpgsqlConnectionStringBuilder> Build(Core.Configuration.DeploymentEnvironment environment, SessionContext? context = null) =>
        (await PostgreSqlConnectionFactory.BuildAsync(environment, context ?? SessionContext.Empty, CancellationToken.None)).Builder;

    [Fact]
    public async Task Host_user_tls_and_connection_properties_are_applied()
    {
        var b = await Build(new()
        {
            Name = "p",
            Host = "db.example.com",
            Port = 6432,
            Database = "app",
            Authentication = Core.Configuration.AuthenticationKind.Password,
            User = "deploy",
            Password = "secret",
            Tls = new Core.Configuration.TlsOptions { Mode = Core.Configuration.TlsMode.Strict },
            ConnectionProperties = new Dictionary<string, string> { ["Search Path"] = "app,public", ["Timeout"] = "40" },
        });

        Assert.Equal(("db.example.com", 6432, "app", "deploy", "secret"), (b.Host, b.Port, b.Database, b.Username, b.Password));
        Assert.Equal(Npgsql.SslMode.VerifyFull, b.SslMode);
        Assert.Equal("app,public", b.SearchPath);
        Assert.Equal(40, b.Timeout);
        Assert.Equal("Badger SQL Migrate", b.ApplicationName);
    }

    [Fact]
    public async Task Entra_tokens_become_the_password_and_require_tls()
    {
        var b = await Build(
            new() { Name = "p", Host = "x.postgres.database.azure.com", Database = "app", User = "deployers", Authentication = Core.Configuration.AuthenticationKind.EntraDefault },
            new SessionContext { Tokens = new FixedTokens() });

        Assert.Equal(("deployers", "token-for:" + PostgreSqlConnectionFactory.EntraScope), (b.Username, b.Password));
        Assert.Equal(Npgsql.SslMode.Require, b.SslMode);
    }

    [Fact]
    public async Task Ssh_tunnel_rewrites_the_endpoint()
    {
        var b = await Build(new() { Name = "p", Host = "db.internal", Database = "app" }, new SessionContext { EndpointOverride = new Endpoint("127.0.0.1", 50001) });
        Assert.Equal(("127.0.0.1", 50001), (b.Host, b.Port));
    }

    [Fact]
    public void Validation_reports_sql_server_only_settings()
    {
        var problems = new PostgreSqlProvider().ValidateEnvironment(new()
        {
            Name = "p",
            Host = "h",
            Authentication = Core.Configuration.AuthenticationKind.EntraIntegrated,
            ConnectionProperties = new Dictionary<string, string> { ["Not A Keyword"] = "1" },
            ProviderSettings = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{ "alwaysEncrypted": {} }""")!,
        });

        Assert.Contains(problems, p => p.Contains("'EntraIntegrated' is not available for PostgreSQL", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("connectionProperties 'Not A Keyword'", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("unknown PostgreSQL setting 'alwaysEncrypted'", StringComparison.Ordinal));
    }

    [Fact]
    public void Endpoint_and_database_come_from_the_connection_string()
    {
        var environment = new Core.Configuration.DeploymentEnvironment { Name = "p", ConnectionString = "Host=pg.example.com;Port=5433;Database=shop;Username=u" };
        Assert.Equal(new Endpoint("pg.example.com", 5433), PostgreSqlConnectionFactory.ResolveEndpoint(environment));
        Assert.Equal("shop", PostgreSqlConnectionFactory.ResolveDatabaseName(environment));
    }

    private sealed class FixedTokens : IAccessTokenProvider
    {
        public string DefaultScope => "unused";

        public Task<AccessTokenResult> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken) =>
            Task.FromResult(new AccessTokenResult("token-for:" + scopes[0], DateTimeOffset.UtcNow.AddHours(1)));
    }
}
