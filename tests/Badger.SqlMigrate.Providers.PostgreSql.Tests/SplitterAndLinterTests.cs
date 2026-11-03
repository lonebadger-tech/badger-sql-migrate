using Badger.SqlMigrate.Core;
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
    public void Unsupported_psql_commands_are_reported_and_supported_ones_are_not()
    {
        var d = Lint("\\set ON_ERROR_STOP on\n\\timing on\nSELECT 1;\nCOPY t (a) FROM STDIN;\n1\n\\.\n\\c otherdb\n\\! rm -rf /\nSELECT 2 \\gset\nALTER TYPE mood ADD VALUE 'meh';");
        Assert.Equal(["BSM005", "BSM005", "BSM005", "BSM007"], d.Select(x => x.Code));
        Assert.Equal([7, 8, 9, 10], d.Select(x => x.Line));
        Assert.Contains("\\connect would switch databases", d[0].Message, StringComparison.Ordinal);
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

public class PsqlSyntaxTests
{
    private readonly PostgreSqlStatementSplitter splitter = new();

    private IReadOnlyList<ScriptBatch> Split(string script, Dictionary<string, string>? vars = null) => splitter.Split(script, vars ?? []);

    [Fact]
    public void Variables_interpolate_outside_strings_and_bodies_and_bsm_variables_are_available()
    {
        var batches = Split("""
            \set table customer
            \set owner 'O''Brien'
            SELECT :'owner' AS who, :"table" AS t, :table.id, 'keep :table', now()::date, a[1:2], :{?table} AS defined, :{?nope} AS missing, :undefined;
            CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $$ SELECT :table $$;
            SELECT :'schema';
            \unset table
            SELECT :table;
            """, new() { ["schema"] = "calc_test" });

        Assert.Equal("SELECT 'O''Brien' AS who, \"customer\" AS t, customer.id, 'keep :table', now()::date, a[1:2], TRUE AS defined, FALSE AS missing, :undefined;", batches[0].Text);
        Assert.Equal("CREATE FUNCTION f() RETURNS int LANGUAGE sql AS $$ SELECT :table $$;", batches[1].Text);
        Assert.Equal("SELECT 'calc_test';", batches[2].Text);
        Assert.Equal("SELECT :table;", batches[3].Text);
        Assert.Equal("E'a\\\\b'", PostgreSqlStatementSplitter.QuoteLiteral("a\\b"));
    }

    [Fact]
    public void Conditionals_choose_branches()
    {
        const string script = """
            \if :is_prod
              SELECT 'prod';
            \elif :{?seed}
              SELECT 'seed';
              \if false
                SELECT 'never';
              \else
                SELECT 'nested else';
              \endif
            \else
              SELECT 'other';
            \endif
            SELECT 'always';
            """;

        Assert.Equal(["SELECT 'prod';", "SELECT 'always';"], Split(script, new() { ["is_prod"] = "on" }).Select(b => b.Text));
        Assert.Equal(["SELECT 'seed';", "SELECT 'nested else';", "SELECT 'always';"], Split(script, new() { ["is_prod"] = "off", ["seed"] = "" }).Select(b => b.Text));
        Assert.Equal(["SELECT 'other';", "SELECT 'always';"], Split(script, new() { ["is_prod"] = "no" }).Select(b => b.Text));

        var ex = Assert.Throws<MigrationException>(() => Split(script));
        Assert.Contains("line 1: \\if needs a boolean", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\\if without \\endif", Assert.Throws<MigrationException>(() => Split("\\if true\nSELECT 1;")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_data_echo_gexec_and_g()
    {
        var batches = Split("""
            CREATE TABLE t (a int, b text);
            COPY t (a, b) FROM STDIN WITH (FORMAT csv);
            1,one; not a statement
            2,"two
            lines"
            \.
            \echo loaded :rows rows
            SELECT format('CREATE TABLE %I (id int)', 'gen_' || n) FROM generate_series(1, 2) n
            \gexec
            SELECT 3
            \g
            \pset format aligned
            SELECT 4;
            """, new() { ["rows"] = "2" });

        Assert.Equal(6, batches.Count);
        Assert.Equal("COPY t (a, b) FROM STDIN WITH (FORMAT csv);", batches[1].Text);
        Assert.Equal("1,one; not a statement\n2,\"two\nlines\"\n", batches[1].CopyData);
        Assert.Equal("DO $bsm_echo$ BEGIN RAISE NOTICE '%', 'loaded 2 rows'; END $bsm_echo$;", batches[2].Text);
        Assert.True(batches[3].ExecuteResults);
        Assert.Equal("SELECT 3", batches[4].Text);
        Assert.Equal(("SELECT 4;", 13), (batches[5].Text, batches[5].StartLine));
    }

    [Theory]
    [InlineData("\\c other", "\\connect would switch databases")]
    [InlineData("\\o out.txt", "act outside the database")]
    [InlineData("\\prompt 'Name?' name", "ask for input")]
    [InlineData("SELECT 1 \\gset", "\\gset sets variables")]
    [InlineData("\\set x `date`", "backquoted shell commands")]
    [InlineData("\\copy t to 'out.csv'", "writing files on the client")]
    public void Commands_that_act_outside_the_script_are_errors(string script, string message) =>
        Assert.Contains(message, Assert.Throws<MigrationException>(() => Split(script)).Message, StringComparison.Ordinal);
}

public sealed class PsqlIncludeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bsm-psql", Guid.NewGuid().ToString("N"));
    private readonly PostgreSqlIncludes includes = new();

    public PsqlIncludeTests() => Directory.CreateDirectory(root);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Expand(string relative, HashSet<string>? included = null) =>
        includes.Expand(File.ReadAllText(Path.Combine(root, relative)), Path.Combine(root, relative), root, included ?? []);

    [Fact]
    public void Includes_resolve_from_the_project_or_the_script_and_nest()
    {
        Write("shared/types.sql", "CREATE TYPE mood AS ENUM ('happy');\n\\ir grants.sql\n");
        Write("shared/grants.sql", "GRANT USAGE ON TYPE mood TO PUBLIC;\n");
        Write("changes/a/part.sql", "CREATE TABLE a (m mood);\n");
        Write("changes/a/deploy.sql", "\\i shared/types.sql\n\\ir 'part.sql'\nSELECT 1;\n");
        var included = new HashSet<string>();

        Assert.Equal("CREATE TYPE mood AS ENUM ('happy');\nGRANT USAGE ON TYPE mood TO PUBLIC;\nCREATE TABLE a (m mood);\nSELECT 1;\n", Expand("changes/a/deploy.sql", included));
        Assert.Equal(3, included.Count);
    }

    [Fact]
    public void Copy_from_a_file_inlines_the_data()
    {
        Write("data/customers.csv", "id,name\r\n1,Ada\r\n2,Grace");
        Write("changes/seed/deploy.sql", "\\copy customer (id, name) from 'data/customers.csv' with (format csv, header)\n\\copy t from stdin\n3\n\\.\n");

        Assert.Equal("COPY customer (id, name) FROM STDIN with (format csv, header);\nid,name\n1,Ada\n2,Grace\n\\.\nCOPY t FROM STDIN;\n3\n\\.\n", Expand("changes/seed/deploy.sql"));
    }

    [Fact]
    public void Cycles_files_outside_the_project_and_missing_files_are_errors()
    {
        Write("a.sql", "\\i b.sql\n");
        Write("b.sql", "\\i a.sql\n");
        Write("outside.sql", "\\i ../elsewhere.sql\n");
        Write("missing.sql", "\\i nope.sql\n");

        Assert.Contains("includes itself", Assert.Throws<MigrationException>(() => Expand("a.sql")).Message, StringComparison.Ordinal);
        Assert.Contains("outside the project folder", Assert.Throws<MigrationException>(() => Expand("outside.sql")).Message, StringComparison.Ordinal);
        Assert.Contains("'nope.sql' does not exist", Assert.Throws<MigrationException>(() => Expand("missing.sql")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Included_files_are_part_of_the_hash_and_the_bundle()
    {
        Write("bsm.plan", "seed\n");
        Write("data/rows.csv", "1\n");
        Write("changes/seed/deploy.sql", "\\copy t from 'data/rows.csv'\n");
        Write("changes/seed/revert.sql", "DELETE FROM t;\n");
        var provider = new PostgreSqlProvider();

        var first = Core.Project.MigrationProject.Load(root, provider.Id, includes: provider.Includes);
        Assert.Empty(first.Problems);
        Assert.Equal(["data/rows.csv"], first.IncludedFiles);
        Assert.Contains("COPY t FROM STDIN;\n1\n\\.", first.Changes[0].DeployScript, StringComparison.Ordinal);

        Write("data/rows.csv", "2\n");
        Assert.NotEqual(first.Changes[0].ScriptHash, Core.Project.MigrationProject.Load(root, provider.Id, includes: provider.Includes).Changes[0].ScriptHash);

        var bundle = Path.Combine(root, "out", "b.zip");
        var manifest = Core.Bundles.ProjectBundle.Create(root, provider.Id, bundle, includeConfig: false, "1.0", provider.Includes);
        Assert.Contains(manifest.Files, f => f.Path == "data/rows.csv");
        Core.Bundles.ProjectBundle.Extract(bundle, Path.Combine(root, "extracted"), _ => provider.Includes);

        Write("changes/seed/deploy.sql", "\\i nope.sql\n");
        Assert.Contains(Core.Project.MigrationProject.Load(root, provider.Id, includes: provider.Includes).Problems, p => p.Contains("changes/seed/deploy.sql: line 1: 'nope.sql' does not exist", StringComparison.Ordinal));
    }
}

