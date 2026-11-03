using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Import;

namespace Badger.SqlMigrate.Cli.Tests;

public sealed class ImportCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "bsm-import-cli", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir, params string[] vars) => new(dir, null, null, vars, false, new ConsoleOutput(false, false, true));

    [Fact]
    public async Task Imported_flyway_project_has_a_bsm_yaml_that_resolves()
    {
        var source = Path.Combine(root, "flyway");
        Directory.CreateDirectory(Path.Combine(source, "sql"));
        File.WriteAllText(Path.Combine(source, "flyway.toml"), """
            [flyway]
            environment = "prod"
            [flyway.placeholders]
            appSchema = "sales"
            quote = "say \"hi\" {x}"
            [environments.test]
            url = "jdbc:sqlserver://test-sql:1433;databaseName=AppTest;encrypt=true;trustServerCertificate=true;applicationIntent=ReadWrite;sendTimeAsDatetime=false"
            user = "deploy"
            password = "${env.TEST_DB_PASSWORD}"
            [environments.prod]
            url = "jdbc:sqlserver://prod.database.windows.net;databaseName=App;authentication=ActiveDirectoryDefault;encrypt=strict"
            """);
        File.WriteAllText(Path.Combine(source, "sql", "V1__Init.sql"), "CREATE SCHEMA ${appSchema}; SELECT '${quote}';");
        var target = Path.Combine(root, "bsm");

        Assert.Equal(0, Commands.ImportFlyway(G(target), source, null, [], dryRun: false));

        var config = BsmConfig.Load(target);
        Assert.Equal("prod", config.DefaultEnvironment);
        Assert.Equal("sales", config.Variables["appSchema"]);
        Assert.Equal("say \"hi\" {{x}}", config.Variables["quote"]);

        Environment.SetEnvironmentVariable("TEST_DB_PASSWORD", "from-the-environment");
        var test = config.ResolveEnvironment("test", null);
        Assert.Equal("from-the-environment", test.Password);
        Assert.Equal(("test-sql", 1433, "AppTest", AuthenticationKind.Password, "deploy"), (test.Host, test.Port!.Value, test.Database, test.Authentication!.Value, test.User));
        Assert.Equal(TlsMode.Mandatory, test.Tls.Mode);
        Assert.True(test.Tls.TrustServerCertificate);
        Assert.Equal("ReadWrite", test.ConnectionProperties["Application Intent"]);

        var prod = config.ResolveEnvironment("prod", null);
        Assert.Equal((AuthenticationKind.EntraDefault, TlsMode.Strict), (prod.Authentication!.Value, prod.Tls.Mode));
        Assert.Contains("password: \"${env:TEST_DB_PASSWORD}\"", File.ReadAllText(Path.Combine(target, "bsm.yaml")), StringComparison.Ordinal);

        Assert.Equal(0, Commands.PlanCheck(G(target)));
        Assert.Equal(1, await Commands.Run(G(target), g => Commands.ImportFlyway(g, source, null, [], dryRun: false)));
    }

    [Fact]
    public async Task Imported_dbup_project_without_values_fails_plan_check_until_defined()
    {
        var source = Path.Combine(root, "dbup");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Script0001.sql"), "CREATE SCHEMA [$schema$]; SELECT '$(literal)';");
        var target = Path.Combine(root, "bsm");

        Assert.Equal(0, Commands.ImportDbUp(G(target), source, false, [], false, dryRun: false));
        var yaml = File.ReadAllText(Path.Combine(target, "bsm.yaml"));
        Assert.Contains("# schema: \"...\"", yaml, StringComparison.Ordinal);
        Assert.Contains("sqlcmdSyntax: false", yaml, StringComparison.Ordinal);
        Assert.Equal(1, Commands.PlanCheck(G(target)));

        var withValues = Path.Combine(root, "bsm2");
        Assert.Equal(0, await Commands.Run(G(withValues, "schema=app"), g => Commands.ImportDbUp(g, source, false, [], false, dryRun: false)));
        Assert.Equal(0, Commands.PlanCheck(G(withValues)));
        Assert.Equal("app", BsmConfig.Load(withValues).Variables["schema"]);
    }

    [Fact]
    public void Dry_run_writes_nothing()
    {
        var source = Path.Combine(root, "dbup");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.sql"), "SELECT 1;");
        var target = Path.Combine(root, "bsm");

        Assert.Equal(0, Commands.ImportDbUp(G(target), source, false, [], false, dryRun: true));
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("jdbc:sqlserver://sql01\\INST;databaseName=App;integratedSecurity=true", "sql01\\INST", null, "integrated")]
    [InlineData("jdbc:sqlserver://;serverName=h;portNumber=1500;databaseName=App", "h", 1500, "password")]
    [InlineData("jdbc:sqlserver://h:1433;databaseName=App;authentication=ActiveDirectoryServicePrincipal", "h", 1433, "entraServicePrincipal")]
    [InlineData("jdbc:sqlserver://h;databaseName=App;authentication=ActiveDirectoryMSI;msiClientId=abc", "h", null, "entraManagedIdentity")]
    public void Jdbc_urls_map_to_environments(string url, string host, int? port, string authentication)
    {
        var warnings = new List<string>();
        var settings = JdbcSqlServer.Map(new ConnectionToImport("x", url, "u", "plain-secret"), warnings)!;

        Assert.Equal(host, (string?)settings["host"]);
        Assert.Equal(port, (int?)settings["port"]);
        Assert.Equal("App", (string?)settings["database"]);
        Assert.Equal(authentication, (string?)settings["authentication"]);
        Assert.DoesNotContain("plain-secret", settings.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Non_sql_server_urls_are_reported()
    {
        var warnings = new List<string>();
        Assert.Null(JdbcSqlServer.Map(new ConnectionToImport("pg", "jdbc:postgresql://h/db?password=x", null, null), warnings));
        Assert.Contains(warnings, w => w.Contains("not a SQL Server JDBC URL", StringComparison.Ordinal));
    }
}
