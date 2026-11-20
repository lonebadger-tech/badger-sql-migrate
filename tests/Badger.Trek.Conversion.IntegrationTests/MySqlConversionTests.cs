using Badger.Trek.Cli;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Badger.Trek.Conversion.IntegrationTests;

/// <summary>
/// Conversions to and from MySQL: each other engine's sample database becomes a MySQL one, and the MySQL sample becomes
/// each of the others. The converted project deploys, the rows copy, and trek verify certifies the target.
/// </summary>
[Collection("servers")]
public sealed class MySqlConversionTests(Servers servers) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-convert-mysql-it", Guid.NewGuid().ToString("N"));

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

    private static async Task<MySqlConnection> OpenMySqlAsync(string connectionString)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> Scalar(MySqlConnection my, string sql) => await new MySqlCommand(sql, my).ExecuteScalarAsync();

    [SkippableFact]
    public async Task PostgreSql_to_MySql()
    {
        Skip.If(servers.MySqlUnavailableReason is not null, servers.MySqlUnavailableReason);
        var source = await servers.CreatePostgresAsync(Sample("postgresql.sql"));
        var target = await servers.CreateMySqlAsync();
        await ConvertAsync("postgresql", "mysql", source, $"    connectionString: {Quote(target)}\n");

        await using var my = await OpenMySqlAsync(target);

        // Arrays became JSON; text with tabs and quotes survived; the computed column keeps its scale.
        Assert.Equal(2, Convert.ToInt32(await Scalar(my, "SELECT JSON_LENGTH(tags) FROM app_customers WHERE id = 1")));
        Assert.Equal("Chloé \"Tab\there\"", await Scalar(my, "SELECT name FROM app_customers WHERE id = 3"));
        Assert.Equal("line1\nline2", await Scalar(my, "SELECT note FROM app_orders WHERE id = 3"));
        Assert.Equal(35.96m, await Scalar(my, "SELECT total_with_tax FROM app_orders WHERE id = 1"));

        // The enum became an ENUM; the identity continues after the copied rows.
        Assert.Equal(4UL, await Scalar(my, "INSERT INTO app_orders (customer_id, total) VALUES (2, 7.50); SELECT LAST_INSERT_ID();"));
        await Assert.ThrowsAsync<MySqlException>(() => new MySqlCommand("INSERT INTO app_orders (customer_id, total, status) VALUES (1, 1, 'bogus')", my).ExecuteNonQueryAsync());

        // The domain's check carried over; MySQL unique keys allow several NULLs but not duplicates.
        await Assert.ThrowsAsync<MySqlException>(() => new MySqlCommand("INSERT INTO app_orders (customer_id, total) VALUES (1, -1)", my).ExecuteNonQueryAsync());
        await new MySqlCommand("INSERT INTO app_customers (name) VALUES ('no email 1'), ('no email 2')", my).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<MySqlException>(() => new MySqlCommand("INSERT INTO app_customers (name, email) VALUES ('dup', 'ada@example.com')", my).ExecuteNonQueryAsync());

        // The view.
        Assert.Equal(2L, Convert.ToInt64(await Scalar(my, "SELECT orders FROM app_customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task SqlServer_to_MySql()
    {
        Skip.If(servers.MySqlUnavailableReason is not null, servers.MySqlUnavailableReason);
        var source = await servers.CreateSqlServerAsync(Sample("sqlserver.sql"));
        var target = await servers.CreateMySqlAsync();
        await ConvertAsync("sqlserver", "mysql", source, $"    connectionString: {Quote(target)}\n");

        await using var my = await OpenMySqlAsync(target);
        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar(my, "SELECT Notes FROM sales_Customer WHERE CustomerId = 1"));
        Assert.Equal("unicode ✓", await Scalar(my, "SELECT Notes FROM sales_Customer WHERE CustomerId = 3"));

        // Case-insensitive text, like the SQL Server collation.
        Assert.Equal(1L, Convert.ToInt64(await Scalar(my, "SELECT COUNT(*) FROM sales_Customer WHERE Email = 'ADA@EXAMPLE.COM'")));
        await Assert.ThrowsAsync<MySqlException>(() => new MySqlCommand("INSERT INTO sales_Customer (FirstName, LastName, Email) VALUES ('x', 'y', 'Ada@Example.com')", my).ExecuteNonQueryAsync());

        // A view.
        Assert.Equal(1L, Convert.ToInt64(await Scalar(my, "SELECT COUNT(*) FROM sales_CustomerTotals WHERE CustomerId = 1")));
    }

    [SkippableFact]
    public async Task Sqlite_to_MySql()
    {
        Skip.If(servers.MySqlUnavailableReason is not null, servers.MySqlUnavailableReason);
        var source = SqliteDatabase(Sample("sqlite.sql"));
        var target = await servers.CreateMySqlAsync();
        await ConvertAsync("sqlite", "mysql", $"Data Source={source}", $"    connectionString: {Quote(target)}\n");

        await using var my = await OpenMySqlAsync(target);
        Assert.Equal(1L, Convert.ToInt64(await Scalar(my, "SELECT COUNT(*) FROM authors WHERE name = 'ADA LOVELACE'")));
        Assert.Equal(4UL, await Scalar(my, "INSERT INTO posts (author_id, title, words) VALUES (2, 'New', 5); SELECT LAST_INSERT_ID();"));
        await Assert.ThrowsAsync<MySqlException>(() => new MySqlCommand("INSERT INTO posts (author_id, title, words) VALUES (2, 'Bad', -1)", my).ExecuteNonQueryAsync());
    }

    [SkippableFact]
    public async Task MySql_to_PostgreSql()
    {
        Skip.If(servers.MySqlUnavailableReason is not null, servers.MySqlUnavailableReason);
        var source = await servers.CreateMySqlAsync(Sample("mysql.sql"));
        var target = await servers.CreatePostgresAsync();
        await ConvertAsync("mysql", "postgresql", source, $"    connectionString: {Quote(target)}\n");

        await using var pg = new NpgsqlConnection(target);
        await pg.OpenAsync();
        async Task<object?> Scalar(string sql) => await new NpgsqlCommand(sql, pg).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT notes FROM customers WHERE id = 1"));
        Assert.Equal("Chloé \"Tab\"", await Scalar("SELECT name FROM customers WHERE id = 3"));
        Assert.Equal(true, await Scalar("SELECT active FROM customers WHERE id = 1"));
        Assert.Equal("dark", await Scalar("SELECT settings->>'theme' FROM customers WHERE id = 1"));
        Assert.Equal("pro", await Scalar("SELECT tier FROM customers WHERE id = 1"));
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x10 }, await Scalar("SELECT receipt FROM orders WHERE id = 1"));
        Assert.Equal(35.96m, await Scalar("SELECT total_with_tax FROM orders WHERE id = 1"));

        // The ENUM's values are enforced; the identity continues; case-insensitive text like the MySQL collation.
        Assert.Equal(4L, Convert.ToInt64(await Scalar("INSERT INTO customers (name) VALUES ('New') RETURNING id")));
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("INSERT INTO customers (name, tier) VALUES ('x', 'gold')", pg).ExecuteNonQueryAsync());
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM customers WHERE email = 'ADA@EXAMPLE.COM'"));
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("INSERT INTO orders (customer_id, total) VALUES (1, -1)", pg).ExecuteNonQueryAsync());

        // The view.
        Assert.Equal(2L, Convert.ToInt64(await Scalar("SELECT orders FROM customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task MySql_to_SqlServer()
    {
        Skip.If(servers.MySqlUnavailableReason is not null, servers.MySqlUnavailableReason);
        var source = await servers.CreateMySqlAsync(Sample("mysql.sql"));
        var target = await servers.CreateSqlServerAsync();
        await ConvertAsync("mysql", "sqlserver", source, $"    connectionString: {Quote(target)}\n");

        await using var ss = new SqlConnection(target);
        await ss.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqlCommand(sql, ss).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT notes FROM dbo.customers WHERE id = 1"));
        Assert.Equal("unicode ✓", await Scalar("SELECT notes FROM dbo.customers WHERE id = 3"));
        Assert.Equal(true, await Scalar("SELECT active FROM dbo.customers WHERE id = 1"));
        Assert.Equal(35.96m, await Scalar("SELECT total_with_tax FROM dbo.orders WHERE id = 1"));
        Assert.Equal(4m, await Scalar("INSERT INTO dbo.customers (name) VALUES (N'New'); SELECT SCOPE_IDENTITY();"));
        await Assert.ThrowsAsync<SqlException>(() => new SqlCommand("INSERT INTO dbo.customers (name, tier) VALUES (N'x', N'gold')", ss).ExecuteNonQueryAsync());
        Assert.Equal(2, Convert.ToInt32(await Scalar("SELECT orders FROM dbo.customer_totals WHERE id = 1")));
    }

    [SkippableFact]
    public async Task MySql_to_Sqlite()
    {
        Skip.If(servers.MySqlUnavailableReason is not null, servers.MySqlUnavailableReason);
        var source = await servers.CreateMySqlAsync(Sample("mysql.sql"));
        var target = SqliteDatabase();
        await ConvertAsync("mysql", "sqlite", source, $"    database: {Quote(target)}\n");

        await using var lite = new SqliteConnection($"Data Source={target};Pooling=False;Foreign Keys=True");
        await lite.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqliteCommand(sql, lite).ExecuteScalarAsync();

        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT notes FROM customers WHERE id = 1"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM customers WHERE email = 'ADA@EXAMPLE.COM'"));
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteCommand("INSERT INTO customers (name, tier) VALUES ('x', 'gold')", lite).ExecuteNonQueryAsync());
        Assert.Equal(4L, await Scalar("INSERT INTO customers (name) VALUES ('New') RETURNING id"));
        Assert.Equal(2L, await Scalar("SELECT orders FROM customer_totals WHERE id = 1"));
    }
}
