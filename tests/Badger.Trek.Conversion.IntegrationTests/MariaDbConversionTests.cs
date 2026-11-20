using Badger.Trek.Cli;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Badger.Trek.Conversion.IntegrationTests;

/// <summary>
/// Conversions to and from MariaDB: each other engine's sample database (MySQL's included) becomes a MariaDB one, and
/// the MariaDB sample becomes each of the others. The converted project deploys, the rows copy, and trek verify
/// certifies the target.
/// </summary>
[Collection("servers")]
public sealed class MariaDbConversionTests(Servers servers) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-convert-mariadb-it", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        MySqlConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir, string? connectionString = null) => new(dir, null, connectionString, [], false, new ConsoleOutput(false, false, true)) { AgentsFile = false };

    private static string Sample(string name) => TestFiles.Sample(name);

    private static string Quote(string s) => TestFiles.YamlQuote(s);

    private string SqliteDatabase(string? script = null)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N")[..8] + ".db");
        if (script is not null)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = script;
            command.ExecuteNonQuery();
        }

        return path;
    }

    private static void Target(string project, string environmentYaml)
    {
        var yaml = Path.Combine(project, "trek.yaml");
        var text = File.ReadAllText(yaml);
        File.WriteAllText(yaml, text[..text.IndexOf("\nenvironments:", StringComparison.Ordinal)] + $"\nenvironments:\n  local:\n{environmentYaml}");
    }

    private async Task<string> ConvertAsync(string from, string to, string source, string environmentYaml)
    {
        var project = Path.Combine(root, "converted");
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(0, await Commands.ConvertSchema(G(empty, source), project, to, from, null, false, [], false, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(project, "CONVERSION.md")));
        Target(project, environmentYaml);
        Assert.Equal(0, Commands.PlanCheck(G(project)));
        Assert.Equal(0, await Commands.Deploy(G(project), null, false, null, null, true, CancellationToken.None));
        Assert.Equal(0, await Commands.ConvertData(G(empty, source), project, null, from, [], false, CancellationToken.None));
        Assert.Equal(0, await Commands.Verify(G(project), CancellationToken.None));
        return project;
    }

    private static async Task<MySqlConnection> OpenAsync(string connectionString)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> Scalar(MySqlConnection my, string sql) => await new MySqlCommand(sql, my).ExecuteScalarAsync();

    private static Task Execute(MySqlConnection my, string sql) => new MySqlCommand(sql, my).ExecuteNonQueryAsync();

    [SkippableFact]
    public async Task PostgreSql_to_MariaDb()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = await servers.CreatePostgresAsync(Sample("postgresql.sql"));
        var target = await servers.CreateMariaDbAsync();
        await ConvertAsync("postgresql", "mariadb", source, $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenAsync(target);
        Assert.Equal(2, Convert.ToInt32(await Scalar(db, "SELECT JSON_LENGTH(tags) FROM app_customers WHERE id = 1")));
        Assert.Equal("Chloé \"Tab\there\"", await Scalar(db, "SELECT name FROM app_customers WHERE id = 3"));
        Assert.Equal("line1\nline2", await Scalar(db, "SELECT note FROM app_orders WHERE id = 3"));
        Assert.Equal(35.96m, await Scalar(db, "SELECT total_with_tax FROM app_orders WHERE id = 1"));
        Assert.Equal(4UL, await Scalar(db, "INSERT INTO app_orders (customer_id, total) VALUES (2, 7.50); SELECT LAST_INSERT_ID();"));
        await Assert.ThrowsAsync<MySqlException>(() => Execute(db, "INSERT INTO app_orders (customer_id, total, status) VALUES (1, 1, 'bogus')"));
        await Assert.ThrowsAsync<MySqlException>(() => Execute(db, "INSERT INTO app_orders (customer_id, total) VALUES (1, -1)"));
        Assert.Equal(2L, Convert.ToInt64(await Scalar(db, "SELECT orders FROM app_customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task SqlServer_to_MariaDb()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = await servers.CreateSqlServerAsync(Sample("sqlserver.sql"));
        var target = await servers.CreateMariaDbAsync();
        await ConvertAsync("sqlserver", "mariadb", source, $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenAsync(target);
        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar(db, "SELECT Notes FROM sales_Customer WHERE CustomerId = 1"));
        Assert.Equal("unicode ✓", await Scalar(db, "SELECT Notes FROM sales_Customer WHERE CustomerId = 3"));
        Assert.Equal(1L, Convert.ToInt64(await Scalar(db, "SELECT COUNT(*) FROM sales_Customer WHERE Email = 'ADA@EXAMPLE.COM'")));
        await Assert.ThrowsAsync<MySqlException>(() => Execute(db, "INSERT INTO sales_Customer (FirstName, LastName, Email) VALUES ('x', 'y', 'Ada@Example.com')"));
        Assert.Equal(1L, Convert.ToInt64(await Scalar(db, "SELECT COUNT(*) FROM sales_CustomerTotals WHERE CustomerId = 1")));
    }

    [SkippableFact]
    public async Task Sqlite_to_MariaDb()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = SqliteDatabase(Sample("sqlite.sql"));
        var target = await servers.CreateMariaDbAsync();
        await ConvertAsync("sqlite", "mariadb", $"Data Source={source}", $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenAsync(target);
        Assert.Equal(1L, Convert.ToInt64(await Scalar(db, "SELECT COUNT(*) FROM authors WHERE name = 'ADA LOVELACE'")));
        Assert.Equal(4UL, await Scalar(db, "INSERT INTO posts (author_id, title, words) VALUES (2, 'New', 5); SELECT LAST_INSERT_ID();"));
        await Assert.ThrowsAsync<MySqlException>(() => Execute(db, "INSERT INTO posts (author_id, title, words) VALUES (2, 'Bad', -1)"));
    }

    [SkippableFact]
    public async Task MySql_to_MariaDb()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = await servers.CreateMySqlAsync(Sample("mysql.sql"));
        var target = await servers.CreateMariaDbAsync();
        await ConvertAsync("mysql", "mariadb", source, $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenAsync(target);
        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar(db, "SELECT notes FROM customers WHERE id = 1"));
        Assert.Equal("dark", await Scalar(db, "SELECT JSON_VALUE(settings, '$.theme') FROM customers WHERE id = 1"));
        Assert.Equal(35.96m, await Scalar(db, "SELECT total_with_tax FROM orders WHERE id = 1"));
        Assert.Equal(4UL, await Scalar(db, "INSERT INTO customers (name) VALUES ('New'); SELECT LAST_INSERT_ID();"));
        await Assert.ThrowsAsync<MySqlException>(() => Execute(db, "INSERT INTO customers (name, tier) VALUES ('x', 'gold')"));
        Assert.Equal(1L, Convert.ToInt64(await Scalar(db, "SELECT COUNT(*) FROM customers WHERE email = 'ADA@EXAMPLE.COM'")));
        Assert.Equal(2L, Convert.ToInt64(await Scalar(db, "SELECT orders FROM customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task MariaDb_to_MySql()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = await servers.CreateMariaDbAsync(Sample("mariadb.sql"));
        var target = await servers.CreateMySqlAsync();
        await ConvertAsync("mariadb", "mysql", source, $"    connectionString: {Quote(target)}\n");

        await using var db = await OpenAsync(target);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", Convert.ToString(await Scalar(db, "SELECT ref FROM customers WHERE id = 1")));
        Assert.Equal("dark", await Scalar(db, "SELECT settings->>'$.theme' FROM customers WHERE id = 1"));
        Assert.Equal(35.96m, await Scalar(db, "SELECT total_with_tax FROM orders WHERE id = 1"));
        Assert.Equal(1000L, Convert.ToInt64(await Scalar(db, "SELECT MIN(ticket) FROM orders")));
        Assert.Equal("dark", await Scalar(db, "SELECT theme FROM customer_totals WHERE id = 1"));
    }

    [SkippableFact]
    public async Task MariaDb_to_PostgreSql()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = await servers.CreateMariaDbAsync(Sample("mariadb.sql"));
        var target = await servers.CreatePostgresAsync();
        await ConvertAsync("mariadb", "postgresql", source, $"    connectionString: {Quote(target)}\n");

        await using var pg = new NpgsqlConnection(target);
        await pg.OpenAsync();
        async Task<object?> Scalar(string sql) => await new NpgsqlCommand(sql, pg).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT notes FROM customers WHERE id = 1"));
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), await Scalar("SELECT ref FROM customers WHERE id = 1"));
        Assert.Equal(true, await Scalar("SELECT active FROM customers WHERE id = 1"));
        Assert.Equal("dark", await Scalar("SELECT settings->>'theme' FROM customers WHERE id = 1"));
        Assert.Equal(35.96m, await Scalar("SELECT total_with_tax FROM orders WHERE id = 1"));

        // The sequence continues after the copied tickets; the identity too; the ENUM's values are enforced.
        Assert.Equal(4L, Convert.ToInt64(await Scalar("INSERT INTO orders (customer_id, total) VALUES (2, 1) RETURNING id")));
        Assert.True(Convert.ToInt64(await Scalar("SELECT ticket FROM orders WHERE id = 4")) > 1002);
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("INSERT INTO customers (name, tier) VALUES ('x', 'gold')", pg).ExecuteNonQueryAsync());
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM customers WHERE email = 'ADA@EXAMPLE.COM'"));
        Assert.Equal("dark", await Scalar("SELECT theme FROM customer_totals WHERE id = 1"));
    }

    [SkippableFact]
    public async Task MariaDb_to_SqlServer()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = await servers.CreateMariaDbAsync(Sample("mariadb.sql"));
        var target = await servers.CreateSqlServerAsync();
        await ConvertAsync("mariadb", "sqlserver", source, $"    connectionString: {Quote(target)}\n");

        await using var ss = new SqlConnection(target);
        await ss.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqlCommand(sql, ss).ExecuteScalarAsync();

        Assert.Equal("unicode ✓", await Scalar("SELECT notes FROM dbo.customers WHERE id = 3"));
        Assert.Equal(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"), await Scalar("SELECT ref FROM dbo.customers WHERE id = 1"));
        Assert.Equal(true, await Scalar("SELECT active FROM dbo.customers WHERE id = 1"));
        Assert.Equal(35.96m, await Scalar("SELECT total_with_tax FROM dbo.orders WHERE id = 1"));
        Assert.Equal(4m, await Scalar("INSERT INTO dbo.customers (name) VALUES (N'New'); SELECT SCOPE_IDENTITY();"));
        await Assert.ThrowsAsync<SqlException>(() => new SqlCommand("INSERT INTO dbo.customers (name, tier) VALUES (N'x', N'gold')", ss).ExecuteNonQueryAsync());
        Assert.Equal(2, Convert.ToInt32(await Scalar("SELECT orders FROM dbo.customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task MariaDb_to_Sqlite()
    {
        Skip.If(servers.MariaDbUnavailableReason is not null, servers.MariaDbUnavailableReason);
        var source = await servers.CreateMariaDbAsync(Sample("mariadb.sql"));
        var target = SqliteDatabase();
        await ConvertAsync("mariadb", "sqlite", source, $"    database: {Quote(target)}\n");

        await using var lite = new SqliteConnection($"Data Source={target};Pooling=False;Foreign Keys=True");
        await lite.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqliteCommand(sql, lite).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT notes FROM customers WHERE id = 1"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM customers WHERE email = 'ADA@EXAMPLE.COM'"));
        Assert.Equal(4L, await Scalar("INSERT INTO customers (name, ref) VALUES ('New', 'x') RETURNING id"));
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteCommand("INSERT INTO customers (name, ref, tier) VALUES ('x', 'y', 'gold')", lite).ExecuteNonQueryAsync());
        Assert.Equal(2L, await Scalar("SELECT orders FROM customer_totals WHERE id = 1"));
    }
}
