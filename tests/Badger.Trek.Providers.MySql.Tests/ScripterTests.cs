using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.MySql.Scripting;
using Xunit.Abstractions;

namespace Badger.Trek.Providers.MySql.Tests;

/// <summary>
/// 'trek add --from-diff' on MySQL: each scenario builds schema A in one database and B in another, scripts the
/// difference, and checks that the deploy script turns A into B (same fingerprint) and the revert script turns it back.
/// </summary>
[Collection("mysql")]
public sealed class ScripterTests(MySqlFixture fixture, ITestOutputHelper output)
{
    private readonly MySqlProvider provider = new();

    private const string Base = """
        CREATE TABLE customer (
          id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
          email VARCHAR(200) NOT NULL,
          name VARCHAR(100) NULL,
          UNIQUE KEY ux_email (email)
        ) COMMENT = 'people who buy';
        CREATE TABLE orders (
          id INT NOT NULL PRIMARY KEY,
          customer_id INT NOT NULL,
          total DECIMAL(12,2) NOT NULL DEFAULT 0,
          note VARCHAR(50) NULL,
          KEY ix_customer (customer_id),
          CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id),
          CONSTRAINT ck_total CHECK (total >= 0)
        );
        INSERT INTO customer (email, name) VALUES ('a@example.com', 'Ann');
        INSERT INTO orders (id, customer_id, total, note) VALUES (1, 1, 10, 'first');
        """;

    private async Task<(SchemaScript Script, string Before, string After)> RoundTripAsync(string a, string b)
    {
        Skip.If(fixture.AdminConnectionString is null, fixture.UnavailableReason);
        await using var dbA = await fixture.CreateDatabaseAsync();
        await using var dbB = await fixture.CreateDatabaseAsync();
        await using var sa = await provider.OpenSessionAsync(dbA.Profile, SessionContext.Empty, CancellationToken.None);
        await using var sb = await provider.OpenSessionAsync(dbB.Profile, SessionContext.Empty, CancellationToken.None);
        await RunAsync(sa, a);
        await RunAsync(sb, b);
        var before = await provider.Schema.CaptureAsync(sa, "trek", CancellationToken.None);
        var after = await provider.Schema.CaptureAsync(sb, "trek", CancellationToken.None);

        var script = await provider.Scripter.ScriptAsync(sa, sb, new SchemaScriptOptions { StateSchema = "trek" }, CancellationToken.None);
        output.WriteLine("-- deploy\n" + script.Deploy + "\n-- revert\n" + script.Revert + "\n-- summary\n" + string.Join("\n", script.Summary));

        await RunAsync(sa, script.Deploy);
        Assert.Equal(after, await provider.Schema.CaptureAsync(sa, "trek", CancellationToken.None));
        await RunAsync(sa, script.Revert);
        Assert.Equal(before, await provider.Schema.CaptureAsync(sa, "trek", CancellationToken.None));

        var again = await provider.Scripter.ScriptAsync(sa, sa, new SchemaScriptOptions { StateSchema = "trek" }, CancellationToken.None);
        Assert.True(again.IsEmpty);
        return (script, before, after);
    }

    private async Task RunAsync(IDatabaseSession session, string script)
    {
        foreach (var batch in provider.Parser.Split(script))
        {
            await session.ExecuteAsync(batch.Text, null, CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Columns_are_added_in_position_dropped_renamed_retyped_and_moved()
    {
        var (script, _, _) = await RoundTripAsync(Base, Base
            .Replace("  name VARCHAR(100) NULL,", "  phone VARCHAR(30) NULL,\n  display_name VARCHAR(100) NULL,", StringComparison.Ordinal)
            .Replace("  total DECIMAL(12,2) NOT NULL DEFAULT 0,\n  note VARCHAR(50) NULL,", "  note VARCHAR(50) NULL,\n  total DECIMAL(14,2) NOT NULL DEFAULT 0,", StringComparison.Ordinal)
            .Replace("('a@example.com', 'Ann')", "('a@example.com', NULL)", StringComparison.Ordinal)
            .Replace("(email, name)", "(email, display_name)", StringComparison.Ordinal));

        Assert.Contains("+ column customer.phone", script.Summary);
        Assert.Contains("~ column customer.name renamed to display_name", script.Summary);
        Assert.Contains(script.Summary, s => s.StartsWith("~ column orders.total: decimal(12,2)", StringComparison.Ordinal));
        Assert.Contains("RENAME COLUMN `name` TO `display_name`", script.Deploy, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN `phone` varchar(30) DEFAULT NULL AFTER `email`", script.Deploy, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Indexes_checks_and_a_dropped_column_mark_data_loss()
    {
        var (script, _, _) = await RoundTripAsync(Base, Base
            .Replace("  UNIQUE KEY ux_email (email)", "  KEY ix_name (name)", StringComparison.Ordinal)
            .Replace("  note VARCHAR(50) NULL,\n", string.Empty, StringComparison.Ordinal)
            .Replace("CHECK (total >= 0)", "CHECK (total >= 0 AND total < 1000000)", StringComparison.Ordinal)
            .Replace("(id, customer_id, total, note) VALUES (1, 1, 10, 'first')", "(id, customer_id, total) VALUES (1, 1, 10)", StringComparison.Ordinal));

        Assert.Contains("- index customer.ux_email", script.Summary);
        Assert.Contains("+ index customer.ix_name", script.Summary);
        Assert.Contains("~ check orders.ck_total", script.Summary);
        Assert.Contains("- column orders.note", script.Summary);
        Assert.True(script.DataLoss);
    }

    [SkippableFact]
    public async Task New_tables_with_foreign_keys_in_a_cycle_and_a_dropped_referenced_table()
    {
        var (script, _, _) = await RoundTripAsync(Base, """
            CREATE TABLE customer (
              id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
              email VARCHAR(200) NOT NULL,
              name VARCHAR(100) NULL,
              favourite_store INT NULL,
              UNIQUE KEY ux_email (email)
            ) COMMENT = 'people who buy';
            CREATE TABLE store (
              id INT NOT NULL PRIMARY KEY,
              manager_id INT NULL,
              CONSTRAINT fk_store_manager FOREIGN KEY (manager_id) REFERENCES customer (id)
            );
            ALTER TABLE customer ADD CONSTRAINT fk_customer_store FOREIGN KEY (favourite_store) REFERENCES store (id);
            """);

        Assert.Contains("- table orders", script.Summary);
        Assert.Contains("+ table store", script.Summary);
        Assert.Contains("+ foreign key customer.fk_customer_store", script.Summary);
        Assert.True(script.DataLoss);
    }

    [SkippableFact]
    public async Task Views_routines_triggers_and_events_are_dropped_and_created_again()
    {
        const string programs = """
            CREATE VIEW big_orders AS SELECT id, total FROM orders WHERE total > 100;
            CREATE VIEW big_order_count AS SELECT COUNT(*) AS n FROM big_orders;
            CREATE FUNCTION with_tax(amount DECIMAL(12,2)) RETURNS DECIMAL(12,2) DETERMINISTIC RETURN amount * 1.2;
            DELIMITER //
            CREATE PROCEDURE add_order(IN cid INT, IN amount DECIMAL(12,2))
            BEGIN
              INSERT INTO orders (id, customer_id, total) SELECT COALESCE(MAX(id), 0) + 1, cid, amount FROM orders;
              SELECT LAST_INSERT_ID();
            END //
            DELIMITER ;
            CREATE TRIGGER orders_total BEFORE INSERT ON orders FOR EACH ROW SET NEW.total = ROUND(NEW.total, 2);
            CREATE EVENT purge_notes ON SCHEDULE EVERY 1 DAY DISABLE DO UPDATE orders SET note = NULL WHERE note = '';
            """;
        var (script, _, _) = await RoundTripAsync(Base, Base + programs);

        Assert.Contains("+ view big_order_count", script.Summary);
        Assert.Contains("+ procedure add_order", script.Summary);
        Assert.Contains("+ trigger orders_total", script.Summary);
        Assert.Contains("+ event purge_notes", script.Summary);
        Assert.Contains("DELIMITER //", script.Deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFINER=", script.Deploy, StringComparison.Ordinal);
        Assert.True(script.Deploy.IndexOf("big_orders` AS", StringComparison.Ordinal) < script.Deploy.IndexOf("big_order_count` AS", StringComparison.Ordinal));

        // Changing a view's definition drops and re-creates it.
        var (changed, _, _) = await RoundTripAsync(Base + programs, Base + programs.Replace("total > 100", "total > 500", StringComparison.Ordinal));
        Assert.Contains("~ view big_orders", changed.Summary);
    }

    [SkippableFact]
    public async Task Table_options_change_with_alter_table()
    {
        var (script, _, _) = await RoundTripAsync(Base, Base.Replace("COMMENT = 'people who buy'", "COMMENT = 'customers' COLLATE = utf8mb4_bin", StringComparison.Ordinal));

        Assert.Contains("~ table customer options", script.Summary);
    }

    [Fact]
    public void Show_create_table_is_parsed_line_by_line_without_auto_increment_counters()
    {
        var table = MySqlModel.ParseTable("orders", """
            CREATE TABLE `orders` (
              `id` int NOT NULL AUTO_INCREMENT,
              `customer_id` int NOT NULL,
              `total` decimal(12,2) NOT NULL DEFAULT '0.00',
              PRIMARY KEY (`id`),
              KEY `ix_customer` (`customer_id`),
              CONSTRAINT `fk_orders_customer` FOREIGN KEY (`customer_id`) REFERENCES `customer` (`id`) ON DELETE CASCADE,
              CONSTRAINT `ck_total` CHECK ((`total` >= 0))
            ) ENGINE=InnoDB AUTO_INCREMENT=42 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """);

        Assert.Equal(["id", "customer_id", "total"], table.Columns.Select(c => c.Name));
        Assert.Equal(["PRIMARY", "ix_customer"], table.Indexes.Select(i => i.Name));
        Assert.Equal("customer", Assert.Single(table.Constraints, c => c.ForeignKey).References);
        Assert.Equal("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", table.Options);
        Assert.DoesNotContain("FOREIGN KEY", table.CreateWithoutForeignKeys(), StringComparison.Ordinal);
    }

    [Fact]
    public void Statements_become_portable_and_repeatables_own_what_they_create()
    {
        Assert.Equal(
            "CREATE ALGORITHM=UNDEFINED SQL SECURITY DEFINER VIEW `v` AS select `t`.`id` AS `id` from `t`",
            MySqlModel.Portable("CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`%` SQL SECURITY DEFINER VIEW `v` AS select `shop`.`t`.`id` AS `id` from `shop`.`t`", "shop"));

        var owned = MySqlScripter.OwnedObjects(["CREATE OR REPLACE VIEW big_orders AS SELECT 1;", "DROP PROCEDURE IF EXISTS p;\nDELIMITER //\nCREATE DEFINER=CURRENT_USER PROCEDURE `add_order`() BEGIN END //"]);
        Assert.Equal(["procedure add_order", "view big_orders"], owned.Order(StringComparer.Ordinal));
    }
}
