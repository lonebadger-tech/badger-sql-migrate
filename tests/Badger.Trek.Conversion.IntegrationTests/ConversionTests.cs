using System.Text.RegularExpressions;
using Badger.Trek.Cli;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MariaDb;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace Badger.Trek.Conversion.IntegrationTests;

/// <summary>
/// One PostgreSQL and one SQL Server for the run: TREK_TEST_POSTGRES / TREK_TEST_SQLSERVER (connection strings to
/// existing servers) or Testcontainers. When either is unavailable the tests skip. MySQL (TREK_TEST_MYSQL, or a
/// container from TREK_TEST_MYSQL_IMAGE, default mysql:8.4) is separate: when it cannot start only the MySQL tests skip.
/// </summary>
public sealed class Servers : IAsyncLifetime
{
    private PostgreSqlContainer? postgres;
    private MsSqlContainer? sqlServer;
    private MySqlContainer? mySql;

    public string? Postgres { get; private set; }

    public string? SqlServer { get; private set; }

    public string? UnavailableReason { get; private set; }

    public string? MySql { get; private set; }

    /// <summary>Why the MySQL tests skip: no PostgreSQL or SQL Server (the other side of most of them), or no MySQL.</summary>
    public string? MySqlUnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            Postgres = Environment.GetEnvironmentVariable("TREK_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(Postgres))
            {
                postgres = new PostgreSqlBuilder(Environment.GetEnvironmentVariable("TREK_TEST_POSTGRES_IMAGE") is { Length: > 0 } image ? image : "postgres:18").Build();
                await postgres.StartAsync();
                Postgres = postgres.GetConnectionString();
            }

            SqlServer = Environment.GetEnvironmentVariable("TREK_TEST_SQLSERVER");
            if (string.IsNullOrWhiteSpace(SqlServer))
            {
                sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                await sqlServer.StartAsync();
                SqlServer = sqlServer.GetConnectionString();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UnavailableReason = $"database containers could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set TREK_TEST_POSTGRES and TREK_TEST_SQLSERVER.";
        }

        MySqlUnavailableReason = UnavailableReason;
        MariaDbUnavailableReason = UnavailableReason;
        if (UnavailableReason is not null)
        {
            return;
        }

        try
        {
            MySql = Environment.GetEnvironmentVariable("TREK_TEST_MYSQL");
            if (string.IsNullOrWhiteSpace(MySql))
            {
                mySql = new MySqlBuilder(Environment.GetEnvironmentVariable("TREK_TEST_MYSQL_IMAGE") is { Length: > 0 } image ? image : "mysql:8.4")
                    .WithUsername("root").WithPassword("trek-test").Build();
                await mySql.StartAsync();
                MySql = mySql.GetConnectionString();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MySqlUnavailableReason = $"MySQL could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set TREK_TEST_MYSQL.";
        }

        // MariaDB: TREK_TEST_MARIADB, or a container from TREK_TEST_MARIADB_IMAGE (default mariadb:11.4). Its tests
        // also convert to and from MySQL, so they skip when MySQL is unavailable too.
        MariaDbUnavailableReason = MySqlUnavailableReason;
        if (MySqlUnavailableReason is not null)
        {
            return;
        }

        try
        {
            MariaDb = Environment.GetEnvironmentVariable("TREK_TEST_MARIADB");
            if (string.IsNullOrWhiteSpace(MariaDb))
            {
                mariaDb = new MariaDbBuilder(Environment.GetEnvironmentVariable("TREK_TEST_MARIADB_IMAGE") is { Length: > 0 } image ? image : "mariadb:11.4")
                    .WithUsername("root").WithPassword("trek-test").Build();
                await mariaDb.StartAsync();
                MariaDb = mariaDb.GetConnectionString();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MariaDbUnavailableReason = $"MariaDB could not start ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}). Start Docker or set TREK_TEST_MARIADB.";
        }
    }

    public string? MariaDb { get; private set; }

    /// <summary>Why the MariaDB tests skip: no PostgreSQL, SQL Server or MySQL (the other sides), or no MariaDB.</summary>
    public string? MariaDbUnavailableReason { get; private set; }

    private MariaDbContainer? mariaDb;

    public async Task<string> CreateMariaDbAsync(string? script = null)
    {
        var name = "trek_conv_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new MySqlConnection(MariaDb))
        {
            await admin.OpenAsync();
            await new MySqlCommand($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_ai_ci;", admin).ExecuteNonQueryAsync();
        }

        var cs = new MySqlConnectionStringBuilder(MariaDb!) { Database = name, Pooling = false }.ConnectionString;
        if (script is not null)
        {
            await using var connection = new MySqlConnection(cs);
            await connection.OpenAsync();
            await new MySqlCommand(script, connection).ExecuteNonQueryAsync();
        }

        return cs;
    }

    public async Task DisposeAsync()
    {
        if (postgres is not null)
        {
            await postgres.DisposeAsync();
        }

        if (sqlServer is not null)
        {
            await sqlServer.DisposeAsync();
        }

        if (mySql is not null)
        {
            await mySql.DisposeAsync();
        }

        if (mariaDb is not null)
        {
            await mariaDb.DisposeAsync();
        }
    }

    public async Task<string> CreateMySqlAsync(string? script = null)
    {
        var name = "trek_conv_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new MySqlConnection(MySql))
        {
            await admin.OpenAsync();
            await new MySqlCommand($"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;", admin).ExecuteNonQueryAsync();
        }

        var cs = new MySqlConnectionStringBuilder(MySql!) { Database = name, Pooling = false }.ConnectionString;
        if (script is not null)
        {
            await using var connection = new MySqlConnection(cs);
            await connection.OpenAsync();
            await new MySqlCommand(script, connection).ExecuteNonQueryAsync();
        }

        return cs;
    }

    public async Task<string> CreatePostgresAsync(string? script = null)
    {
        var name = "trek_conv_" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new NpgsqlConnection(Postgres))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {name};", admin).ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(Postgres) { Database = name, Pooling = false }.ConnectionString;
        if (script is not null)
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await new NpgsqlCommand(script, connection).ExecuteNonQueryAsync();
        }

        return cs;
    }

    public async Task<string> CreateSqlServerAsync(string? script = null)
    {
        var name = "TrekConv" + Guid.NewGuid().ToString("N")[..10];
        await using (var admin = new SqlConnection(SqlServer))
        {
            await admin.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{name}];", admin).ExecuteNonQueryAsync();
        }

        var cs = new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = name, Pooling = false, TrustServerCertificate = true }.ConnectionString;
        if (script is not null)
        {
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline).Where(b => b.Trim().Length > 0))
            {
                await new SqlCommand(batch, connection).ExecuteNonQueryAsync();
            }
        }

        return cs;
    }
}

[CollectionDefinition("servers")]
public sealed class ServersCollection : ICollectionFixture<Servers>
{
}

/// <summary>
/// Converts a sample database each way and proves the result: the converted project deploys, the rows copy with matching
/// counts, trek verify certifies the target, and the translated routines and triggers behave like the originals.
/// </summary>
[Collection("servers")]
public sealed class ConversionTests(Servers servers) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "trek-convert-it", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static GlobalOptions G(string dir, string? connectionString = null) => new(dir, null, connectionString, [], false, new ConsoleOutput(false, false, true)) { AgentsFile = false };

    private static string Sample(string name) => TestFiles.Sample(name);

    /// <summary>Points the converted project's local environment at the test database.</summary>
    private static void Target(string project, string connectionString)
    {
        var yaml = Path.Combine(project, "trek.yaml");
        var text = File.ReadAllText(yaml);
        text = text[..text.IndexOf("\nenvironments:", StringComparison.Ordinal)] + $"\nenvironments:\n  local:\n    connectionString: {TestFiles.YamlQuote(connectionString)}\n";
        File.WriteAllText(yaml, text);
    }

    private async Task<string> ConvertAsync(string provider, string to, string source, string target, string? names = null)
    {
        Skip.If(servers.UnavailableReason is not null, servers.UnavailableReason);
        var project = Path.Combine(root, "converted");
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(0, await Commands.ConvertSchema(G(empty, source), project, to, provider, names, false, [], false, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(project, "CONVERSION.md")));
        Target(project, target);
        Assert.Equal(0, Commands.PlanCheck(G(project)));
        Assert.Equal(0, await Commands.Deploy(G(project), null, false, null, null, true, CancellationToken.None));
        Assert.Equal(0, await Commands.ConvertData(G(empty, source), project, null, provider, [], false, CancellationToken.None));
        Assert.Equal(0, await Commands.Verify(G(project), CancellationToken.None));
        return project;
    }

    [SkippableFact]
    public async Task SqlServer_to_PostgreSql()
    {
        Skip.If(servers.UnavailableReason is not null, servers.UnavailableReason);
        var source = await servers.CreateSqlServerAsync(Sample("sqlserver.sql"));
        var target = await servers.CreatePostgresAsync();
        await ConvertAsync("sqlserver", "postgresql", source, target, names: "snake");

        await using var pg = new NpgsqlConnection(target);
        await pg.OpenAsync();
        async Task<T> Scalar<T>(string sql) => (T)(await new NpgsqlCommand(sql, pg).ExecuteScalarAsync())!;

        // Data, including text that needs escaping.
        Assert.Equal("tab\tand\nnewline \\ backslash", await Scalar<string>("SELECT notes FROM sales.customer WHERE customer_id = 1"));
        Assert.Equal("unicode ✓", await Scalar<string>("SELECT notes FROM sales.customer WHERE customer_id = 3"));

        // Translated procedure: identity and default sequence continue after the copied rows; RETURNING replaced SCOPE_IDENTITY.
        Assert.Equal(102L, await Scalar<long>("CALL sales.place_order(1, 'Sprocket', 2, 5.00, NULL)"));
        Assert.Equal(5002L, await Scalar<long>("SELECT invoice_number FROM sales.\"order\" WHERE order_id = 102"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM sales.order_line WHERE order_id = 102"));

        // Statement trigger with transition tables.
        await new NpgsqlCommand("UPDATE sales.\"order\" SET total = total + 10 WHERE order_id = 100", pg).ExecuteNonQueryAsync();
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM sales.order_audit"));

        // Case-insensitive text, like the SQL Server collation.
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM sales.customer WHERE email = 'ADA@EXAMPLE.COM'"));
        await Assert.ThrowsAsync<PostgresException>(() => new NpgsqlCommand("INSERT INTO sales.customer (first_name, last_name, email) VALUES ('x', 'y', 'Ada@Example.com')", pg).ExecuteNonQueryAsync());

        // Views and table functions.
        Assert.Equal("Ada Lovelace", await Scalar<string>("SELECT name FROM sales.active_customer WHERE customer_id = 1"));
        Assert.Equal(2L, await Scalar<long>("SELECT count(*) FROM sales.big_orders(30)"));
        Assert.Equal(1L, await Scalar<long>("SELECT count(*) FROM sales.orders_for(3)"));
    }

    [SkippableFact]
    public async Task PostgreSql_to_SqlServer()
    {
        Skip.If(servers.UnavailableReason is not null, servers.UnavailableReason);
        var source = await servers.CreatePostgresAsync(Sample("postgresql.sql"));
        var target = await servers.CreateSqlServerAsync();
        await ConvertAsync("postgresql", "sqlserver", source, target);

        await using var ss = new SqlConnection(target);
        await ss.OpenAsync();
        async Task<object?> Scalar(string sql) => await new SqlCommand(sql, ss).ExecuteScalarAsync();

        // Arrays became JSON; text with tabs and quotes survived; computed column keeps its scale.
        Assert.Equal("[\"vip\",\"early\"]", await Scalar("SELECT tags FROM app.customers WHERE id = 1"));
        Assert.Equal("Chloé \"Tab\there\"", await Scalar("SELECT name FROM app.customers WHERE id = 3"));
        Assert.Equal(35.96m, await Scalar("SELECT total_with_tax FROM app.orders WHERE id = 1"));

        // Scalar and inline table-valued functions.
        Assert.Equal("Ada Lovelace <ada@example.com>", await Scalar("SELECT app.display_name(name, email) FROM app.customers WHERE id = 1"));
        Assert.Equal(2, await Scalar("SELECT app.order_count(1)"));
        Assert.Equal(3, await Scalar("SELECT COUNT(*) FROM app.orders_since('2000-01-01')"));

        // A function that writes became a procedure with an OUTPUT result, and raises the same error.
        await using (var call = new SqlCommand("app.mark_paid", ss) { CommandType = System.Data.CommandType.StoredProcedure })
        {
            call.Parameters.AddWithValue("@p_order", 2L);
            var result = call.Parameters.Add("@result", System.Data.SqlDbType.Int);
            result.Direction = System.Data.ParameterDirection.Output;
            await call.ExecuteNonQueryAsync();
            Assert.Equal(1, result.Value);
            var error = await Assert.ThrowsAsync<SqlException>(() => call.ExecuteNonQueryAsync());
            Assert.Contains("order 2 is not open", error.Message, StringComparison.Ordinal);
        }

        // BEFORE row trigger's NEW changes are written back; statement trigger logs inserts; identity continues.
        await new SqlCommand("UPDATE app.orders SET note = N'touched' WHERE id = 3", ss).ExecuteNonQueryAsync();
        Assert.NotNull(await Scalar("SELECT updated_at FROM app.orders WHERE id = 3"));
        Assert.Equal(4m, await Scalar("INSERT INTO app.orders (customer_id, total) VALUES (2, 7.50); SELECT SCOPE_IDENTITY();"));
        Assert.Equal(1, await Scalar("SELECT COUNT(*) FROM app.order_log WHERE order_id = 4 AND event = N'created'"));

        // Cursor loop procedure.
        await new SqlCommand("EXEC app.ship_all", ss).ExecuteNonQueryAsync();
        Assert.Equal(2, await Scalar("SELECT COUNT(*) FROM app.orders WHERE status = N'shipped'"));

        // Unique email allows several NULLs (filtered index) but not duplicates.
        await new SqlCommand("INSERT INTO app.customers (name) VALUES (N'no email 1'), (N'no email 2')", ss).ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<SqlException>(() => new SqlCommand("INSERT INTO app.customers (name, email) VALUES (N'dup', N'ada@example.com')", ss).ExecuteNonQueryAsync());
    }
}
