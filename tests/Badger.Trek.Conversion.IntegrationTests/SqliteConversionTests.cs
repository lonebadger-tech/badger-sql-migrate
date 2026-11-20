using Badger.Trek.Cli;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Badger.Trek.Conversion.IntegrationTests;

/// <summary>Conversions to and from SQLite: a temporary database file on one side, a container on the other.</summary>
[Collection("servers")]
public sealed class SqliteConversionTests(Servers servers) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-convert-sqlite-it", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir, string? connectionString = null) => new(dir, null, connectionString, [], false, new ConsoleOutput(false, false, true)) { AgentsFile = false };

    private static string Sample(string name) => TestFiles.Sample(name);

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
        Target(project, environmentYaml);
        Assert.Equal(0, Commands.PlanCheck(G(project)));
        Assert.Equal(0, await Commands.Deploy(G(project), null, false, null, null, true, CancellationToken.None));
        Assert.Equal(0, await Commands.ConvertData(G(empty, source), project, null, from, [], false, CancellationToken.None));
        Assert.Equal(0, await Commands.Verify(G(project), CancellationToken.None));
        return project;
    }

    private static string Quote(string s) => TestFiles.YamlQuote(s);

    [SkippableFact]
    public async Task Sqlite_to_PostgreSql()
    {
        Skip.If(servers.UnavailableReason is not null, servers.UnavailableReason);
        var source = SqliteDatabase(Sample("sqlite.sql"));
        var target = await servers.CreatePostgresAsync();
        await ConvertAsync("sqlite", "postgresql", $"Data Source={source}", $"    connectionString: {Quote(target)}\n");

        await using var pg = new NpgsqlConnection(target);
        await pg.OpenAsync();
        async Task<object?> Scalar(string sql) => await new NpgsqlCommand(sql, pg).ExecuteScalarAsync();
        Assert.Equal("Notes on sql engines; Draft", await Scalar("SELECT titles FROM author_stats WHERE id = 1"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM authors WHERE name = 'ADA LOVELACE'"));
        Assert.Equal(4L, await Scalar("INSERT INTO posts (author_id, title, words) VALUES (2, 'New', 5) RETURNING id"));
        Assert.Equal(4L, await Scalar("SELECT count(*) FROM post_log"));
        await new NpgsqlCommand("UPDATE posts SET title = 'Renamed' WHERE id = 1", pg).ExecuteNonQueryAsync();
        Assert.Equal(true, await Scalar("SELECT updated_at IS NOT NULL FROM posts WHERE id = 1"));
        var error = await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("UPDATE posts SET words = 1 WHERE id = 1", pg).ExecuteNonQueryAsync());
        Assert.Contains("words cannot go down", error.MessageText, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Sqlite_to_SqlServer()
    {
        Skip.If(servers.UnavailableReason is not null, servers.UnavailableReason);
        var source = SqliteDatabase(Sample("sqlite.sql"));
        var target = await servers.CreateSqlServerAsync();
        await ConvertAsync("sqlite", "sqlserver", $"Data Source={source}", $"    connectionString: {Quote(target)}\n");

        await using var ss = new SqlConnection(target);
        await ss.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqlCommand(sql, ss).ExecuteScalarAsync();
        Assert.Equal("Notes on sql engines; Draft", await Scalar("SELECT titles FROM dbo.author_stats WHERE id = 1"));
        Assert.Equal(4m, await Scalar("INSERT INTO dbo.posts (author_id, title, words) VALUES (2, N'New', 5); SELECT SCOPE_IDENTITY();"));
        Assert.Equal(4, await Scalar("SELECT COUNT(*) FROM dbo.post_log"));
        await new SqlCommand("UPDATE dbo.posts SET title = N'Renamed' WHERE id = 1", ss).ExecuteNonQueryAsync();
        Assert.Equal(1, await Scalar("SELECT CASE WHEN updated_at IS NOT NULL THEN 1 ELSE 0 END FROM dbo.posts WHERE id = 1"));
        var error = await Assert.ThrowsAsync<SqlException>(() => new SqlCommand("UPDATE dbo.posts SET words = 1 WHERE id = 1", ss).ExecuteNonQueryAsync());
        Assert.Contains("words cannot go down", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task PostgreSql_to_Sqlite()
    {
        Skip.If(servers.UnavailableReason is not null, servers.UnavailableReason);
        var source = await servers.CreatePostgresAsync(Sample("postgresql.sql"));
        var target = SqliteDatabase();
        await ConvertAsync("postgresql", "sqlite", source, $"    database: {Quote(target)}\n");

        await using var lite = new SqliteConnection($"Data Source={target};Pooling=False;Foreign Keys=True");
        await lite.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqliteCommand(sql, lite).ExecuteScalarAsync();
        Assert.Equal("[\"vip\",\"early\"]", await Scalar("SELECT tags FROM app_customers WHERE id = 1"));
        Assert.Equal(35.96, await Scalar("SELECT total_with_tax FROM app_orders WHERE id = 1"));
        Assert.Equal(4L, await Scalar("INSERT INTO app_orders (customer_id, total) VALUES (2, 7.5) RETURNING id"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM app_order_log WHERE order_id = 4 AND event = 'created'"));
        await new SqliteCommand("UPDATE app_orders SET note = 'x' WHERE id = 2", lite).ExecuteNonQueryAsync();
        Assert.Equal(1L, await Scalar("SELECT updated_at IS NOT NULL FROM app_orders WHERE id = 2"));
        await Assert.ThrowsAsync<SqliteException>(() => new SqliteCommand("INSERT INTO app_orders (customer_id, total, status) VALUES (1, 1, 'bogus')", lite).ExecuteNonQueryAsync());
    }

    [SkippableFact]
    public async Task SqlServer_to_Sqlite()
    {
        Skip.If(servers.UnavailableReason is not null, servers.UnavailableReason);
        var source = await servers.CreateSqlServerAsync(Sample("sqlserver.sql"));
        var target = SqliteDatabase();
        await ConvertAsync("sqlserver", "sqlite", source, $"    database: {Quote(target)}\n");

        await using var lite = new SqliteConnection($"Data Source={target};Pooling=False;Foreign Keys=True");
        await lite.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqliteCommand(sql, lite).ExecuteScalarAsync();
        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar("SELECT Notes FROM sales_Customer WHERE CustomerId = 1"));
        Assert.Equal("Ada Lovelace", await Scalar("SELECT Name FROM sales_ActiveCustomer WHERE CustomerId = 1"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM sales_Customer WHERE Email = 'ADA@EXAMPLE.COM'"));
        await new SqliteCommand("UPDATE sales_Order SET Total = Total + 5 WHERE OrderId = 100", lite).ExecuteNonQueryAsync();
        Assert.Equal(2L, await Scalar("SELECT count(*) FROM sales_OrderAudit"));
    }
}
