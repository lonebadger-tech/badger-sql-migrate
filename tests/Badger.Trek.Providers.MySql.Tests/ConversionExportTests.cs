using Badger.Trek.Core;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql.Tests;

[Collection("mysql")]
public sealed class ConversionExportTests(MySqlFixture fixture)
{
    private const string Schema = """
        CREATE TABLE customer (
          id INT UNSIGNED NOT NULL AUTO_INCREMENT,
          email VARCHAR(200) NOT NULL COMMENT 'login name',
          name VARCHAR(100) COLLATE utf8mb4_bin NULL DEFAULT 'it''s a \\ test',
          status ENUM('active','closed') NOT NULL DEFAULT 'active',
          active TINYINT(1) NOT NULL DEFAULT 1,
          created DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
          profile JSON NULL,
          token CHAR(36) NOT NULL DEFAULT (uuid()),
          bio TEXT NULL,
          email_lower VARCHAR(200) AS (lower(email)) VIRTUAL,
          PRIMARY KEY (id),
          UNIQUE KEY ux_customer_email (email),
          UNIQUE KEY ux_customer_bio (bio(20)),
          KEY ix_customer_lower ((lower(name))),
          KEY ix_customer_created (created DESC),
          FULLTEXT KEY ft_customer_bio (bio)
        ) COMMENT 'people who buy';

        CREATE TABLE orders (
          customer_id INT UNSIGNED NOT NULL,
          order_no INT NOT NULL,
          total DECIMAL(12,2) NOT NULL DEFAULT 0,
          qty INT NOT NULL DEFAULT 1,
          line_total DECIMAL(14,2) AS (total * qty) STORED,
          PRIMARY KEY (customer_id, order_no),
          CONSTRAINT ck_orders_total CHECK (total >= 0),
          CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE
        );

        CREATE TABLE order_note (
          id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
          customer_id INT UNSIGNED NULL,
          order_no INT NULL,
          CONSTRAINT fk_note_order FOREIGN KEY (customer_id, order_no) REFERENCES orders (customer_id, order_no) ON DELETE SET NULL ON UPDATE RESTRICT
        );

        CREATE TABLE scratch_tmp (id INT PRIMARY KEY);

        CREATE FUNCTION order_tax(amount DECIMAL(12,2)) RETURNS DECIMAL(12,2) DETERMINISTIC RETURN amount * 0.08;

        CREATE VIEW v_customer AS SELECT id, email FROM customer WHERE status = 'active';

        CREATE VIEW big_spenders AS SELECT c.email, order_tax(o.total) AS tax FROM v_customer c JOIN orders o ON o.customer_id = c.id WHERE o.total > 100;

        DELIMITER //
        CREATE PROCEDURE close_customer(IN p_id INT)
        BEGIN
          UPDATE customer SET status = 'closed' WHERE id = p_id;
          SELECT ROW_COUNT();
        END //
        DELIMITER ;

        CREATE TRIGGER trg_orders_bi BEFORE INSERT ON orders FOR EACH ROW SET NEW.qty = GREATEST(NEW.qty, 1);

        CREATE EVENT ev_cleanup ON SCHEDULE EVERY 1 DAY DO DELETE FROM orders WHERE total = 0;
        """;

    private readonly MySqlProvider provider = new(MySqlFixture.Flavor);

    private async Task<TestDatabase> RequireDatabaseAsync()
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        return await fixture.CreateDatabaseAsync();
    }

    private async Task RunAsync(IDatabaseSession session, string script)
    {
        foreach (var statement in provider.Parser.Split(script))
        {
            await session.ExecuteAsync(statement.Text, null, CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task The_exporter_reads_every_feature_of_the_database()
    {
        Skip.If(MySqlFixture.Flavor == MySqlFlavor.MariaDb, "MySQL features (functional indexes, expression defaults on CHAR); MariaDB has its own test.");
        await using var db = await RequireDatabaseAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, Schema);
        await provider.StateStore.EnsureCreatedAsync(session, "trek", CancellationToken.None);

        var schema = await provider.Exporter.ExportAsync(session, new ExportOptions { StateSchema = "trek", Excluded = n => n.Name == "scratch_tmp" }, CancellationToken.None);
        var n = db.Name;

        Assert.Equal("mysql", schema.SourceProvider);
        Assert.Equal(n, schema.DefaultSchema);
        Assert.Equal([n], schema.Schemas);
        Assert.Equal("utf8mb4_0900_ai_ci", schema.Collation);
        Assert.True(schema.CaseInsensitive);
        Assert.Equal(["customer", "order_note", "orders"], schema.Tables.Select(t => t.Name.Name));
        Assert.All(schema.Tables, t => Assert.Equal(n, t.Name.Schema));

        var customer = schema.Tables.Single(t => t.Name.Name == "customer");
        Assert.Equal("people who buy", customer.Comment);
        Assert.Equal(["id", "email", "name", "status", "active", "created", "profile", "token", "bio", "email_lower"], customer.Columns.Select(c => c.Name));
        PortableColumn C(string name) => customer.Columns.Single(c => c.Name == name);

        Assert.Equal("int unsigned", C("id").Type);
        Assert.Equal(new PortableIdentity(1, 1, false), C("id").Identity);
        Assert.False(C("id").Nullable);
        Assert.Null(C("id").Default);
        Assert.Equal("varchar(200)", C("email").Type);
        Assert.Equal("login name", C("email").Comment);
        Assert.Null(C("email").Collation);
        Assert.Equal("'it''s a \\\\ test'", C("name").Default);
        Assert.Equal("utf8mb4_bin", C("name").Collation);
        Assert.True(C("name").Nullable);
        Assert.Equal("enum('active','closed')", C("status").Type);
        Assert.Equal("'active'", C("status").Default);
        Assert.Equal("tinyint(1)", C("active").Type);
        Assert.Equal("1", C("active").Default);
        Assert.Equal("datetime(6)", C("created").Type);
        Assert.Equal("CURRENT_TIMESTAMP(6)", C("created").Default);
        Assert.Equal("json", C("profile").Type);
        Assert.Null(C("profile").Default);
        Assert.Equal("char(36)", C("token").Type);
        Assert.Equal("uuid()", C("token").Default);
        Assert.Equal("varchar(200)", C("email_lower").Type);
        Assert.Equal("lower(`email`)", C("email_lower").Computed);
        Assert.False(C("email_lower").ComputedStored);
        Assert.Null(C("email_lower").Default);

        Assert.Null(customer.PrimaryKey!.Name);
        Assert.Equal(["id"], customer.PrimaryKey.Columns);
        var unique = Assert.Single(customer.Uniques);
        Assert.Equal("ux_customer_email", unique.Name);
        Assert.Equal(["email"], unique.Columns);
        var indexes = customer.Indexes.ToDictionary(i => i.Name);
        Assert.Equal(["ft_customer_bio", "ix_customer_created", "ix_customer_lower", "ux_customer_bio"], indexes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("FULLTEXT", indexes["ft_customer_bio"].Method);
        Assert.Equal([new PortableIndexColumn("bio", false, false)], indexes["ft_customer_bio"].Columns);
        Assert.Null(indexes["ix_customer_created"].Method);
        Assert.Equal([new PortableIndexColumn("created", false, true)], indexes["ix_customer_created"].Columns);
        var expression = Assert.Single(indexes["ix_customer_lower"].Columns);
        Assert.True(expression.IsExpression);
        Assert.Contains("lower(`name`)", expression.Text, StringComparison.Ordinal);
        Assert.True(indexes["ux_customer_bio"].Unique);
        Assert.Contains(schema.Warnings, w => w.Contains("ux_customer_bio", StringComparison.Ordinal) && w.Contains("prefix", StringComparison.Ordinal));

        var orders = schema.Tables.Single(t => t.Name.Name == "orders");
        Assert.Equal(["customer_id", "order_no"], orders.PrimaryKey!.Columns);
        Assert.Equal("decimal(12,2)", orders.Columns.Single(c => c.Name == "total").Type);
        Assert.Equal("0.00", orders.Columns.Single(c => c.Name == "total").Default);
        var lineTotal = orders.Columns.Single(c => c.Name == "line_total");
        Assert.True(lineTotal.ComputedStored);
        Assert.Equal("(`total` * `qty`)", lineTotal.Computed);
        var check = Assert.Single(orders.Checks);
        Assert.Equal("ck_orders_total", check.Name);
        Assert.Contains("`total` >= 0", check.Expression, StringComparison.Ordinal);
        var fk = Assert.Single(orders.ForeignKeys);
        Assert.Equal("fk_orders_customer", fk.Name);
        Assert.Equal(["customer_id"], fk.Columns);
        Assert.Equal(new PortableName(n, "customer"), fk.Referenced);
        Assert.Equal(["id"], fk.ReferencedColumns);
        Assert.Equal("CASCADE", fk.OnDelete);
        Assert.Equal("NO ACTION", fk.OnUpdate);

        var note = schema.Tables.Single(t => t.Name.Name == "order_note");
        var composite = Assert.Single(note.ForeignKeys);
        Assert.Equal(["customer_id", "order_no"], composite.Columns);
        Assert.Equal(["customer_id", "order_no"], composite.ReferencedColumns);
        Assert.Equal(new PortableName(n, "orders"), composite.Referenced);
        Assert.Equal("SET NULL", composite.OnDelete);
        Assert.Equal("RESTRICT", composite.OnUpdate);
        Assert.Equal(new PortableIdentity(1, 1, false), note.Columns[0].Identity);

        // Code: functions, then views with their dependencies first, then procedures and triggers.
        Assert.Equal(
            [(PortableCodeKind.Function, "order_tax"), (PortableCodeKind.View, "v_customer"), (PortableCodeKind.View, "big_spenders"), (PortableCodeKind.Procedure, "close_customer"), (PortableCodeKind.Trigger, "trg_orders_bi")],
            schema.Code.Select(c => (c.Kind, c.Name.Name)));
        Assert.All(schema.Code, c =>
        {
            Assert.Equal("sql", c.Language);
            Assert.Equal(n, c.Name.Schema);
            Assert.DoesNotContain("DEFINER=", c.Definition, StringComparison.Ordinal);
            Assert.DoesNotContain(n, c.Definition, StringComparison.Ordinal);
            Assert.StartsWith("CREATE", c.Definition, StringComparison.Ordinal);
        });
        var bigSpenders = schema.Code.Single(c => c.Name.Name == "big_spenders");
        Assert.Equal([$"{n}.v_customer", $"{n}.order_tax"], bigSpenders.DependsOn.Order(StringComparer.Ordinal).Reverse());
        Assert.Empty(schema.Code.Single(c => c.Name.Name == "v_customer").DependsOn);
        var procedure = schema.Code.Single(c => c.Kind == PortableCodeKind.Procedure);
        Assert.Contains("SELECT ROW_COUNT();", procedure.Definition, StringComparison.Ordinal);
        Assert.EndsWith("END", procedure.Definition, StringComparison.Ordinal);
        var trigger = schema.Code.Single(c => c.Kind == PortableCodeKind.Trigger);
        Assert.Equal(new PortableName(n, "orders"), trigger.Table);
        Assert.Contains("BEFORE INSERT ON `orders`", trigger.Definition, StringComparison.Ordinal);

        Assert.Contains($"event {n}.ev_cleanup is not converted.", schema.Warnings);
        Assert.DoesNotContain(schema.Tables, t => t.Name.Name.StartsWith("trek_", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task The_exporter_reads_mariadb_json_uuid_sequences_and_defaults()
    {
        Skip.If(MySqlFixture.Flavor != MySqlFlavor.MariaDb, "MariaDB only.");
        await using var db = await RequireDatabaseAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, """
            CREATE SEQUENCE invoice_no START WITH 5000 INCREMENT BY 10;
            CREATE TABLE item (
              id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
              ref UUID NOT NULL DEFAULT uuid(),
              addr INET6 NULL,
              doc JSON NULL,
              label VARCHAR(20) NOT NULL DEFAULT 'it''s',
              qty INT NOT NULL DEFAULT 0,
              note TEXT NULL,
              made DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
              invoice BIGINT NOT NULL DEFAULT NEXTVAL(invoice_no),
              doubled INT AS (qty * 2) PERSISTENT,
              CONSTRAINT ck_item_qty CHECK (qty >= 0)
            );
            CREATE TABLE other (id INT PRIMARY KEY, qty INT, CONSTRAINT ck_item_qty CHECK (qty < 10));
            """);

        var schema = await provider.Exporter.ExportAsync(session, new ExportOptions { StateSchema = "trek", Excluded = _ => false }, CancellationToken.None);
        Assert.Equal("mariadb", schema.SourceProvider);
        var item = schema.Tables.Single(t => t.Name.Name == "item");
        PortableColumn C(string name) => item.Columns.Single(c => c.Name == name);

        Assert.Equal("uuid", C("ref").Type);
        Assert.Equal("uuid()", C("ref").Default);
        Assert.Equal("inet6", C("addr").Type);
        Assert.Equal("json", C("doc").Type);
        Assert.Null(C("doc").Default);
        Assert.Equal("'it''s'", C("label").Default);
        Assert.Equal("0", C("qty").Default);
        Assert.Null(C("note").Default);
        Assert.Null(C("id").Default);
        Assert.Equal("current_timestamp(3)", C("made").Default);
        Assert.Contains("nextval(", C("invoice").Default!, StringComparison.OrdinalIgnoreCase);
        Assert.True(C("doubled").ComputedStored);
        Assert.Contains("`qty` * 2", C("doubled").Computed!, StringComparison.Ordinal);

        // JSON's json_valid check is not a check of its own; checks with the same name on two tables stay apart.
        var check = Assert.Single(item.Checks);
        Assert.Equal("ck_item_qty", check.Name);
        Assert.Contains(">= 0", check.Expression, StringComparison.Ordinal);
        Assert.Contains("< 10", Assert.Single(schema.Tables.Single(t => t.Name.Name == "other").Checks).Expression, StringComparison.Ordinal);

        var sequence = Assert.Single(schema.Sequences);
        Assert.Equal("invoice_no", sequence.Name.Name);
        Assert.Equal(5000, sequence.Start);
        Assert.Equal(10, sequence.Increment);
        Assert.False(sequence.Cycle);
        Assert.DoesNotContain(schema.Tables, t => t.Name.Name == "invoice_no");
    }

    private static async IAsyncEnumerable<object?[]> Rows(IEnumerable<object?[]> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    private static async Task<List<object?[]>> ReadAllAsync(IDataTransfer data, IDatabaseSession session, PortableName table)
    {
        var columns = await data.ColumnsAsync(session, table, CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in data.ReadAsync(session, table, columns, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    [SkippableFact]
    public async Task Rows_of_every_type_survive_a_write_and_a_read()
    {
        await using var db = await RequireDatabaseAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, """
            CREATE TABLE parent (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, label VARCHAR(20) NOT NULL);
            CREATE TABLE sample (
              id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
              parent_id INT NULL,
              s VARCHAR(100) NULL,
              t TEXT NULL,
              n DECIMAL(12,2) NULL,
              f DOUBLE NULL,
              b TINYINT(1) NULL,
              big BIGINT NULL,
              u INT UNSIGNED NULL,
              d DATE NULL,
              dt DATETIME(6) NULL,
              ts TIMESTAMP(6) NULL,
              tm TIME(6) NULL,
              g CHAR(36) NULL,
              gb BINARY(16) NULL,
              bin VARBINARY(64) NULL,
              j JSON NULL,
              e ENUM('x','y') NULL,
              bit1 BIT(1) NULL,
              twice INT AS (parent_id * 2) VIRTUAL,
              CONSTRAINT fk_sample_parent FOREIGN KEY (parent_id) REFERENCES parent (id)
            );
            """);
        var data = provider.Data!;
        var parent = new PortableName(db.Name, "parent");
        var sample = new PortableName(db.Name, "sample");
        var columns = await data.ColumnsAsync(session, sample, CancellationToken.None);
        Assert.Equal(["id", "parent_id", "s", "t", "n", "f", "b", "big", "u", "d", "dt", "ts", "tm", "g", "gb", "bin", "j", "e", "bit1"], columns.Select(c => c.Name));
        Assert.Equal("tinyint(1)", columns.Single(c => c.Name == "b").Type);
        Assert.Equal(MySqlFixture.Flavor == MySqlFlavor.MariaDb ? "int(10) unsigned" : "int unsigned", columns.Single(c => c.Name == "u").Type);

        var guid = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        var at = new DateTimeOffset(2026, 11, 20, 8, 30, 15, TimeSpan.FromHours(-6)).AddTicks(1234560);
        object?[] full =
        [
            7, 1, "héllo ✓ 日本 \U0001F600", "back\\slash 'quote' \"dq\"\nline", 12.34m, 1.5, true, long.MaxValue, 4_000_000_000u,
            new DateTime(2026, 1, 2), new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Unspecified), at, new TimeSpan(0, 13, 14, 15, 500),
            guid, guid, new byte[] { 0, 1, 127, 128, 255, 92, 39 }, """{"a": [1, 2], "b": "x\\y"}""", "y", true,
        ];
        object?[] empty = [8, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null];

        await data.BeginLoadAsync(session, [parent, sample], CancellationToken.None);
        Assert.Equal(2, await data.WriteAsync(session, sample, columns, Rows([full, empty]), CancellationToken.None));
        var parentColumns = await data.ColumnsAsync(session, parent, CancellationToken.None);
        Assert.Equal(1203, await data.WriteAsync(session, parent, parentColumns, Rows(Enumerable.Range(1, 1203).Select(i => new object?[] { i, "p" + i })), CancellationToken.None));
        await data.EndLoadAsync(session, [parent, sample], CancellationToken.None);
        Assert.False(session.InTransaction);

        Assert.Equal(1203, await data.CountAsync(session, parent, CancellationToken.None));
        var rows = await ReadAllAsync(data, session, sample);
        Assert.Equal(2, rows.Count);
        var r = rows.Single(x => Equals(x[0], 7));
        Assert.Equal(1, r[1]);
        Assert.Equal(full[2], r[2]);
        Assert.Equal(full[3], r[3]);
        Assert.Equal(12.34m, r[4]);
        Assert.Equal(1.5, r[5]);
        Assert.Equal(true, r[6]);
        Assert.Equal(long.MaxValue, r[7]);
        Assert.Equal(4_000_000_000u, r[8]);
        Assert.Equal(new DateTime(2026, 1, 2), r[9]);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, 678), r[10]);
        var ts = Assert.IsType<DateTime>(r[11]);
        Assert.Equal(DateTimeKind.Utc, ts.Kind);
        Assert.Equal(at.UtcDateTime, ts);
        Assert.Equal(new TimeSpan(0, 13, 14, 15, 500), r[12]);
        Assert.Equal(guid.ToString(), r[13]);
        Assert.Equal(guid.ToByteArray(bigEndian: true), r[14]);
        Assert.Equal(full[15], r[15]);
        Assert.Equal(full[16], r[16]);
        Assert.Equal("y", r[17]);
        Assert.Equal(true, r[18]);
        Assert.All(rows.Single(x => Equals(x[0], 8)).Skip(1), Assert.Null);

        // Inside an open transaction the rows come back the same way.
        await session.BeginTransactionAsync(CancellationToken.None);
        var inTransaction = (await ReadAllAsync(data, session, sample)).Single(x => Equals(x[0], 7));
        await session.RollbackAsync(CancellationToken.None);
        Assert.Equal(r, inTransaction);

        // The session's time zone is left as it was.
        Assert.Equal("SYSTEM", (await session.QueryAsync("SELECT @@session.time_zone AS z;", null, CancellationToken.None))[0]["z"]);

        // AUTO_INCREMENT continues after the loaded values.
        await session.ExecuteAsync("INSERT INTO parent (label) VALUES ('next');", null, CancellationToken.None);
        Assert.Equal(1204L, Convert.ToInt64((await session.QueryAsync("SELECT MAX(id) AS m FROM parent;", null, CancellationToken.None))[0]["m"], System.Globalization.CultureInfo.InvariantCulture));

        // Text in a CHAR(36) column that is not a GUID, and zero dates (read as NULL).
        await session.ExecuteAsync("SET SESSION sql_mode = ''; INSERT INTO sample (id, g, d, dt) VALUES (9, 'not a guid', '0000-00-00', '0000-00-00 00:00:00');", null, CancellationToken.None);
        var odd = (await ReadAllAsync(data, session, sample)).Single(x => Equals(x[0], 9));
        Assert.Equal("not a guid", odd[13]);
        Assert.Null(odd[9]);
        Assert.Null(odd[10]);

        // Truncate empties tables that others reference, in any order, and keeps foreign key checks on.
        await data.TruncateAsync(session, [parent, sample], CancellationToken.None);
        Assert.Equal(0, await data.CountAsync(session, parent, CancellationToken.None));
        Assert.Equal(0, await data.CountAsync(session, sample, CancellationToken.None));
        Assert.Equal(1L, Convert.ToInt64((await session.QueryAsync("SELECT @@session.foreign_key_checks AS v;", null, CancellationToken.None))[0]["v"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task Ending_the_load_fails_for_a_row_without_its_parent()
    {
        await using var db = await RequireDatabaseAsync();
        await using var session = await provider.OpenSessionAsync(db.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(session, """
            CREATE TABLE parent (a INT NOT NULL, b INT NOT NULL, PRIMARY KEY (a, b));
            CREATE TABLE child (id INT NOT NULL PRIMARY KEY, a INT NULL, b INT NULL, CONSTRAINT fk_child_parent FOREIGN KEY (a, b) REFERENCES parent (a, b));
            """);
        var data = provider.Data!;
        var parent = new PortableName(db.Name, "parent");
        var child = new PortableName(db.Name, "child");
        var columns = await data.ColumnsAsync(session, child, CancellationToken.None);

        await data.BeginLoadAsync(session, [parent, child], CancellationToken.None);
        await data.WriteAsync(session, parent, await data.ColumnsAsync(session, parent, CancellationToken.None), Rows([[1, 1]]), CancellationToken.None);
        await data.WriteAsync(session, child, columns, Rows([[1, 1, 1], [2, null, 5], [3, 1, 2]]), CancellationToken.None);
        var error = await Assert.ThrowsAsync<MigrationException>(() => data.EndLoadAsync(session, [parent, child], CancellationToken.None));
        Assert.Contains("child", error.Message, StringComparison.Ordinal);
        Assert.Contains("fk_child_parent", error.Message, StringComparison.Ordinal);
        Assert.Contains("1 row(s)", error.Message, StringComparison.Ordinal);
        Assert.True(session.InTransaction);
        await session.RollbackAsync(CancellationToken.None);
        Assert.Equal(0, await data.CountAsync(session, child, CancellationToken.None));
    }
}
