using Badger.Trek.Conversion.MySql;
using Badger.Trek.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.Trek.Conversion.Tests;

/// <summary>
/// MariaDB as a conversion target and source: the MySQL dialect with MariaDB's collations, types, sequences and
/// syntax; MySQL to MariaDB and back by way of PostgreSQL.
/// </summary>
public class MariaDbTests(ITestOutputHelper output)
{
    private void Dump(ConversionResult result)
    {
        foreach (var c in result.Project.Changes)
        {
            output.WriteLine($"=== {c.Name}\n{c.Deploy}\n--- revert\n{c.Revert}");
        }

        foreach (var r in result.Project.Repeatables)
        {
            output.WriteLine($"=== repeatable/{r.Path}\n{r.Script}");
        }

        output.WriteLine(result.Report);
    }

    private static string Change(ConversionResult result, string name) => result.Project.Changes.Single(c => c.Name == name).Deploy;

    private static string Revert(ConversionResult result, string name) => result.Project.Changes.Single(c => c.Name == name).Revert ?? string.Empty;

    /// <summary>Adds columns and indexes to the schema's table at <paramref name="table"/>.</summary>
    private static void Extend(PortableSchema schema, int table, IEnumerable<PortableColumn>? columns = null, IEnumerable<PortableIndex>? indexes = null)
    {
        var t = schema.Tables[table];
        schema.Tables[table] = t with { Columns = [.. t.Columns, .. columns ?? []], Indexes = [.. t.Indexes, .. indexes ?? []] };
    }

    private string ToMariaDb(string pg, string? targetType = null)
    {
        var result = new PgToMySqlTranslator(Schemas.ToMariaDb()).Expression(pg, new ExpressionSite("public.orders", targetType));
        output.WriteLine(result);
        return result;
    }

    private string FromMariaDb(string mariadb)
    {
        var result = new MySqlToPgTranslator(Schemas.MariaDbToPg()).Expression(mariadb, new ExpressionSite("shop.orders"));
        output.WriteLine(result);
        return result;
    }

    /// <summary>The PostgreSQL test schema with what MariaDB converts differently: uuid, inet, a sequence and expression indexes.</summary>
    private static PortableSchema PostgresForMariaDb()
    {
        var s = Schemas.Postgres();
        Extend(s, 0,
            [
                new PortableColumn("ref", "uuid", false) { Default = "gen_random_uuid()" },
                new PortableColumn("client_ip", "inet", true),
                new PortableColumn("order_no", "bigint", false) { Default = "nextval('order_seq'::regclass)" },
                new PortableColumn("doc", "jsonb", true),
            ],
            [
                new PortableIndex("orders_lower_name_idx", false, [new PortableIndexColumn("lower(customer_name)", true, false)], [], null, null),
                new PortableIndex("orders_channel_created_idx", true, [new PortableIndexColumn("(doc ->> 'channel'::text)", true, false), new PortableIndexColumn("created", false, true)], [], null, null),
                new PortableIndex("orders_day_idx", false, [new PortableIndexColumn("((created)::date)", true, false)], [], null, null),
            ]);
        s.Sequences.Add(new PortableSequence(new PortableName("public", "order_seq"), "bigint", 1, 1, 1, long.MaxValue, false));
        return s;
    }

    // ------------------------------------------------------------------ context

    [Theory]
    [InlineData("mariadb", "MariaDB")]
    [InlineData("MariaDB", "MariaDB")]
    [InlineData("mysql", "MySQL")]
    public void MariaDb_is_the_mysql_dialect_with_its_own_provider_id(string provider, string display)
    {
        var ctx = new ConversionContext(Schemas.Postgres(), new ConversionOptions { TargetProvider = provider });
        Assert.Equal(Dialect.MySql, ctx.Target);
        Assert.Equal(display == "MariaDB", ctx.TargetMariaDb);
        Assert.Equal(provider.ToLowerInvariant(), ctx.TargetProvider);
        Assert.Equal(display, ctx.TargetDisplay);
        Assert.Equal(Dialect.MySql, ConversionContext.DialectOf(provider));
        Assert.Equal(provider.ToLowerInvariant(), ConversionContext.ProviderOf(Dialect.MySql, ctx.TargetMariaDb));
    }

    [Fact]
    public void A_mariadb_source_is_known_as_such()
    {
        var ctx = Schemas.MariaDbToPg();
        Assert.Equal(Dialect.MySql, ctx.Source);
        Assert.True(ctx.SourceMariaDb);
        Assert.Equal("mariadb", ctx.SourceProvider);
        Assert.Equal("MariaDB", ctx.SourceDisplay);
    }

    [Fact]
    public void Mariadb_to_mariadb_is_refused_but_mysql_and_mariadb_convert_to_each_other()
    {
        var ex = Assert.Throws<Core.MigrationException>(() => new ConversionContext(Schemas.MariaDb(), new ConversionOptions { TargetProvider = "mariadb" }));
        Assert.Contains("already MariaDB", ex.Message, StringComparison.Ordinal);
        Assert.Throws<Core.MigrationException>(() => new ConversionContext(Schemas.MySql(), new ConversionOptions { TargetProvider = "mysql" }));
        Assert.IsType<ChainTranslator>(ConversionProject.TranslatorFor(new ConversionContext(Schemas.MySql(), new ConversionOptions { TargetProvider = "mariadb" })));
        Assert.IsType<ChainTranslator>(ConversionProject.TranslatorFor(new ConversionContext(Schemas.MariaDb(), new ConversionOptions { TargetProvider = "mysql" })));
    }

    // ------------------------------------------------------------------ PostgreSQL → MariaDB: DDL

    [Fact]
    public void PostgreSql_to_mariadb_tables_use_mariadb_collations_types_and_sequences()
    {
        var result = ConversionProject.Build(PostgresForMariaDb(), new ConversionOptions { TargetProvider = "mariadb" });
        Dump(result);
        var tables = Change(result, "convert-tables");
        Assert.Contains(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_uca1400_as_cs;", tables, StringComparison.Ordinal);
        Assert.DoesNotContain("0900", tables, StringComparison.Ordinal);
        Assert.Contains("`id` BIGINT AUTO_INCREMENT NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`ref` UUID DEFAULT (UUID()) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`client_ip` INET6", tables, StringComparison.Ordinal);
        Assert.Contains("`doc` JSON", tables, StringComparison.Ordinal);
        Assert.Contains("`order_no` BIGINT DEFAULT (NEXTVAL(`order_seq`)) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`created` DATETIME(6) DEFAULT CURRENT_TIMESTAMP(6) NOT NULL", tables, StringComparison.Ordinal);

        // MariaDB's sequences stop one short of the 64-bit range.
        Assert.Equal("CREATE SEQUENCE `order_seq` START WITH 1 INCREMENT BY 1 MINVALUE 1 MAXVALUE 9223372036854775806 NOCYCLE;\n", Change(result, "convert-sequences"));
        Assert.Equal("DROP SEQUENCE `order_seq`;\n", Revert(result, "convert-sequences"));
        Assert.DoesNotContain(result.Context.Issues, i => i.Message.Contains("no sequences", StringComparison.Ordinal));
        Assert.Contains(result.Context.Issues, i => i.Message.Contains("inet became INET6", StringComparison.Ordinal));
    }

    [Fact]
    public void An_expression_index_becomes_an_indexed_virtual_column_on_mariadb()
    {
        var result = ConversionProject.Build(PostgresForMariaDb(), new ConversionOptions { TargetProvider = "mariadb" });
        var indexes = Change(result, "convert-indexes");
        output.WriteLine(indexes);
        Assert.Contains("ALTER TABLE `orders` ADD COLUMN `orders_lower_name_idx_expr` VARCHAR(255) GENERATED ALWAYS AS (LOWER(`customer_name`)) VIRTUAL;\n\nCREATE INDEX `orders_lower_name_idx` ON `orders` (`orders_lower_name_idx_expr`);", indexes, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN `orders_channel_created_idx_expr` VARCHAR(255) GENERATED ALWAYS AS ((JSON_UNQUOTE(JSON_EXTRACT(`doc`, '$.channel')))) VIRTUAL;", indexes, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX `orders_channel_created_idx` ON `orders` (`orders_channel_created_idx_expr`, `created` DESC);", indexes, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN `orders_day_idx_expr` DATE GENERATED ALWAYS AS ((CAST(`created` AS DATE))) VIRTUAL;", indexes, StringComparison.Ordinal);
        Assert.DoesNotContain("CHAR(255)))", indexes, StringComparison.Ordinal);

        // Reverted index first, then its column.
        var revert = Revert(result, "convert-indexes");
        Assert.True(revert.IndexOf("DROP INDEX `orders_lower_name_idx` ON `orders`;", StringComparison.Ordinal) < revert.IndexOf("DROP COLUMN `orders_lower_name_idx_expr`;", StringComparison.Ordinal), revert);
        Assert.Contains(result.Context.Issues, i => i.Message.StartsWith("index orders_lower_name_idx: MariaDB has no expression indexes", StringComparison.Ordinal));
    }

    [Fact]
    public void Mysql_keeps_its_expression_indexes_collations_and_lack_of_sequences()
    {
        var result = ConversionProject.Build(PostgresForMariaDb(), new ConversionOptions { TargetProvider = "mysql" });
        Assert.Contains("COLLATE=utf8mb4_0900_as_cs", Change(result, "convert-tables"), StringComparison.Ordinal);
        Assert.Contains("`ref` CHAR(36)", Change(result, "convert-tables"), StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX `orders_lower_name_idx` ON `orders` ((CAST(LOWER(`customer_name`) AS CHAR(255))));", Change(result, "convert-indexes"), StringComparison.Ordinal);
        Assert.DoesNotContain(result.Project.Changes, c => c.Name == "convert-sequences");
        Assert.Contains(result.Context.Issues, i => i.Message.Contains("MySQL has no sequences", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_expression_index_name_gives_a_column_name_of_at_most_64_characters()
    {
        var s = Schemas.Postgres();
        var name = "orders_" + new string('x', 52) + "_idx";
        Extend(s, 0, indexes: [new PortableIndex(name, false, [new PortableIndexColumn("upper(customer_name)", true, false), new PortableIndexColumn("lower(customer_name)", true, false)], [], null, null)]);
        var indexes = Change(ConversionProject.Build(s, new ConversionOptions { TargetProvider = "mariadb" }), "convert-indexes");
        output.WriteLine(indexes);
        var columns = System.Text.RegularExpressions.Regex.Matches(indexes, "ADD COLUMN `([^`]+)`").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, columns.Count);
        Assert.All(columns, c => Assert.True(c.Length <= 64, c));
        Assert.NotEqual(columns[0], columns[1]);
    }

    // ------------------------------------------------------------------ PostgreSQL → MariaDB: expressions

    [Theory]
    [InlineData("(doc ->> 'name'::text)", "(JSON_UNQUOTE(JSON_EXTRACT(`doc`, '$.name')))")]
    [InlineData("((doc -> 'customer'::text) ->> 'email'::text)", "(JSON_UNQUOTE(JSON_EXTRACT(`doc`, '$.customer.email')))")]
    [InlineData("(doc -> 'lines'::text)", "(JSON_EXTRACT(`doc`, '$.lines'))")]
    [InlineData("(customer_name ~ '^A'::text)", "(`customer_name` REGEXP '(?-i)^A')")]
    [InlineData("(customer_name ~* '^a'::text)", "(`customer_name` REGEXP '(?i)^a')")]
    [InlineData("(customer_name !~ '^A'::text)", "(`customer_name` NOT REGEXP '(?-i)^A')")]
    [InlineData("(customer_name ~ status)", "(`customer_name` REGEXP CONCAT('(?-i)', `status`))")]
    [InlineData("regexp_replace(customer_name, '[0-9]+'::text, '#'::text, 'g'::text)", "REGEXP_REPLACE(`customer_name`, '(?-i)[0-9]+', '#')")]
    [InlineData("regexp_replace(customer_name, 'a'::text, 'b'::text, 'gi'::text)", "REGEXP_REPLACE(`customer_name`, '(?i)a', 'b')")]
    [InlineData("nextval('order_seq'::regclass)", "NEXTVAL(`order_seq`)")]
    [InlineData("nextval('sales.\"Order_Seq\"'::regclass)", "NEXTVAL(`sales_Order_Seq`)")]
    [InlineData("currval('order_seq'::regclass)", "LASTVAL(`order_seq`)")]
    [InlineData("setval('order_seq'::regclass, 10)", "SETVAL(`order_seq`, 10)")]
    [InlineData("setval('order_seq'::regclass, 10, false)", "SETVAL(`order_seq`, 10, 0)")]
    [InlineData("(updated IS NOT DISTINCT FROM created)", "(`updated` <=> `created`)")]
    [InlineData("clock_timestamp()", "SYSDATE(6)")]
    [InlineData("('{}'::jsonb)", "('{}')")]
    [InlineData("gen_random_uuid()", "UUID()")]
    public void Expressions_in_mariadb(string pg, string expected) => Assert.Equal(expected, ToMariaDb(pg));

    [Theory]
    [InlineData("regexp_replace(customer_name, 'a'::text, 'b'::text)", "replaces every match")]
    [InlineData("(customer_name ~~ 'a%'::text)", "MariaDB's LIKE ignores case")]
    [InlineData("initcap(customer_name)", "check that MariaDB has it")]
    public void What_mariadb_cannot_do_faithfully_is_flagged(string pg, string message)
    {
        var sql = ToMariaDb(pg);
        Assert.Contains("REVIEW trek convert", sql, StringComparison.Ordinal);
        Assert.Contains(message, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_in_mariadb_has_no_values_row_lateral_or_derived_column_list()
    {
        var view = new PortableCode(PortableCodeKind.View, new PortableName("public", "codes"),
            "CREATE VIEW public.codes AS\n SELECT v.code, l.n FROM (VALUES (1,'a'::text), (2,'b'::text)) v(code, label), LATERAL (SELECT count(*) AS n FROM public.orders o WHERE o.id = v.code) l;");
        var script = new PgToMySqlTranslator(Schemas.ToMariaDb()).Code(view, [view]).Script;
        output.WriteLine(script);

        // MariaDB 10.11 takes no v(code, label) after a derived table: the names go on a CTE inside it.
        Assert.Contains("FROM (WITH `v`(`code`, `label`) AS (VALUES (1, 'a'), (2, 'b')) SELECT * FROM `v`) AS `v`, (SELECT", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ROW(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LATERAL", script.Split('\n').Last(l => l.Length > 0), StringComparison.Ordinal);
        Assert.Contains("LATERAL is not supported in MariaDB", script, StringComparison.Ordinal);

        var mysql = new PgToMySqlTranslator(Schemas.ToMySql()).Code(view, [view]).Script;
        Assert.Contains("(VALUES ROW(1, 'a'), ROW(2, 'b'))", mysql, StringComparison.Ordinal);
        Assert.Contains("LATERAL (SELECT", mysql, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_arrayagg_keeps_its_order_by_in_mariadb()
    {
        var view = new PortableCode(PortableCodeKind.View, new PortableName("public", "names"),
            "CREATE VIEW public.names AS\n SELECT json_agg(o.customer_name ORDER BY o.created) AS names FROM public.orders o;");
        var script = new PgToMySqlTranslator(Schemas.ToMariaDb()).Code(view, [view]).Script;
        output.WriteLine(script);
        Assert.Contains("JSON_ARRAYAGG(`o`.`customer_name` ORDER BY `o`.`created`) AS `names`", script, StringComparison.Ordinal);
        Assert.StartsWith("-- Converted by trek convert from PostgreSQL view public.names. Check it before deploying.", script, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ MariaDB → PostgreSQL

    [Theory]
    [InlineData("current_timestamp()", "CURRENT_TIMESTAMP")]
    [InlineData("current_timestamp(6)", "CURRENT_TIMESTAMP")]
    [InlineData("uuid()", "gen_random_uuid()")]
    [InlineData("nextval(`order_seq`)", "nextval('public.order_seq')")]
    [InlineData("lastval(`order_seq`)", "currval('public.order_seq')")]
    [InlineData("nextval(`shop`.`order_seq`)", "nextval('public.order_seq')")]
    [InlineData("`o`.`s` regexp '^f'", "o.s ~* '^f'")]
    [InlineData("setval(`order_seq`,100)", "setval('public.order_seq', 100)")]
    [InlineData("json_value(`doc`,'$.channel')", "(doc ->> 'channel')")]
    [InlineData("json_value(`doc`,'$.a.b')", "(doc #>> '{a,b}')")]
    [InlineData("`price` * `qty`", "price * qty")]
    [InlineData("if(`paid`,'yes','no')", "CASE WHEN paid THEN 'yes' ELSE 'no' END")]
    [InlineData("ifnull(`updated`,`created`)", "coalesce(updated, created)")]
    public void Mariadb_expressions_in_postgresql(string mariadb, string expected) => Assert.Equal(expected, FromMariaDb(mariadb));

    [Fact]
    public void MariaDb_to_postgresql()
    {
        var result = ConversionProject.Build(Schemas.MariaDb(), new ConversionOptions { TargetProvider = "postgresql" });
        Dump(result);
        var tables = Change(result, "convert-tables");
        Assert.Contains("id integer GENERATED BY DEFAULT AS IDENTITY (START WITH 1 INCREMENT BY 1) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("paid boolean DEFAULT false NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("flags smallint DEFAULT 0 NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("ref uuid DEFAULT gen_random_uuid() NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("doc jsonb,", tables, StringComparison.Ordinal);
        Assert.Contains("client_ip inet,", tables, StringComparison.Ordinal);
        Assert.Contains("updated timestamp(0),", tables, StringComparison.Ordinal);
        Assert.Contains("email varchar(200) COLLATE public.trek_ci,", tables, StringComparison.Ordinal);
        Assert.Contains("order_no bigint DEFAULT nextval('public.order_seq') NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("created timestamp(6) DEFAULT CURRENT_TIMESTAMP NOT NULL", tables, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("CREATE SEQUENCE public.order_seq AS bigint START WITH 1000 INCREMENT BY 1 MINVALUE 1 MAXVALUE 9223372036854775806 NO CYCLE;", Change(result, "convert-sequences"), StringComparison.Ordinal);
        Assert.StartsWith("# Conversion from MariaDB to PostgreSQL", result.Report, StringComparison.Ordinal);

        var view = Assert.Single(result.Project.Repeatables).Script;
        Assert.StartsWith("-- Converted by trek convert from MariaDB view shop.customer_orders.", view, StringComparison.Ordinal);
        Assert.Contains("SELECT c.id AS id, c.name AS name, count(o.id) AS orders, (o.doc ->> 'channel') AS channel FROM public.customers AS c LEFT JOIN public.orders AS o ON (o.customer_id = c.id) GROUP BY c.id, c.name, (o.doc ->> 'channel');", view, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ MySQL ⇄ MariaDB

    [Fact]
    public void MySql_to_mariadb_by_way_of_postgresql()
    {
        var schema = Schemas.MySql();
        Extend(schema, 1, [new PortableColumn("code", "varchar(20)", true) { Collation = "utf8mb4_0900_as_cs" }, new PortableColumn("token", "varbinary(16)", true)]);
        Extend(schema, 0, [new PortableColumn("big", "bigint unsigned", true)]);
        schema.Code.Add(new PortableCode(PortableCodeKind.View, new PortableName("shop", "big_orders"),
            "CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`%` SQL SECURITY DEFINER VIEW `big_orders` AS select `t`.`id` AS `id`,(`t`.`doc` ->> '$.name') AS `name` from `orders` `t` where regexp_like(`t`.`customer_name`,'^A','c')"));
        var result = ConversionProject.Build(schema, new ConversionOptions { TargetProvider = "mariadb" });
        Dump(result);
        var tables = Change(result, "convert-tables");
        Assert.Contains("COLLATE=utf8mb4_uca1400_as_ci", tables, StringComparison.Ordinal);
        Assert.Contains("`status` enum('new','paid','shipped') DEFAULT ('new') NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`paid` TINYINT(1) DEFAULT (0) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`created` datetime(6) DEFAULT CURRENT_TIMESTAMP(6) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`total` decimal(12,2) GENERATED ALWAYS AS ((`price` * `qty`)) STORED", tables, StringComparison.Ordinal);
        Assert.Contains("`ref` char(36) DEFAULT (UUID()) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`doc` JSON", tables, StringComparison.Ordinal);
        Assert.Contains("`big` BIGINT UNSIGNED", tables, StringComparison.Ordinal);
        Assert.Contains("`code` varchar(20) COLLATE utf8mb4_uca1400_as_cs", tables, StringComparison.Ordinal);
        Assert.Contains("`token` varbinary(16)", tables, StringComparison.Ordinal);
        Assert.StartsWith("# Conversion from MySQL to MariaDB", result.Report, StringComparison.Ordinal);

        var view = result.Project.Repeatables.Single(r => r.Path.Contains("big_orders", StringComparison.Ordinal)).Script;
        Assert.StartsWith("-- Converted by trek convert from MySQL view shop.big_orders (by way of PostgreSQL).", view, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW `big_orders` AS", view, StringComparison.Ordinal);
        Assert.Contains("JSON_UNQUOTE(JSON_EXTRACT(`t`.`doc`, '$.name'))", view, StringComparison.Ordinal);
        Assert.Contains("`t`.`customer_name` REGEXP '(?-i)^A'", view, StringComparison.Ordinal);
    }

    [Fact]
    public void MariaDb_to_mysql_by_way_of_postgresql()
    {
        var result = ConversionProject.Build(Schemas.MariaDb(), new ConversionOptions { TargetProvider = "mysql" });
        Dump(result);
        var tables = Change(result, "convert-tables");
        Assert.Contains("COLLATE=utf8mb4_0900_as_ci", tables, StringComparison.Ordinal);
        Assert.Contains("`id` INT AUTO_INCREMENT NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`flags` TINYINT DEFAULT (0) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`paid` TINYINT(1) DEFAULT (0) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`ref` CHAR(36) DEFAULT (UUID()) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("`client_ip` VARCHAR(45),", tables, StringComparison.Ordinal);
        Assert.Contains("`doc` JSON,", tables, StringComparison.Ordinal);
        Assert.Contains("`updated` datetime,", tables, StringComparison.Ordinal);
        Assert.Contains("`created` datetime(6) DEFAULT CURRENT_TIMESTAMP(6) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT `orders_qty` CHECK (`qty` > 0)", tables, StringComparison.Ordinal);
        Assert.Contains("`order_no` BIGINT DEFAULT (NULL /* REVIEW trek convert: MySQL has no sequences", tables, StringComparison.Ordinal);
        Assert.Contains(result.Context.Issues, i => i.Object == "shop.order_seq" && i.Message.StartsWith("MySQL has no sequences", StringComparison.Ordinal));
        Assert.StartsWith("# Conversion from MariaDB to MySQL", result.Report, StringComparison.Ordinal);

        var view = Assert.Single(result.Project.Repeatables).Script;
        Assert.StartsWith("-- Converted by trek convert from MariaDB view shop.customer_orders (by way of PostgreSQL).", view, StringComparison.Ordinal);
        Assert.Contains("SELECT `c`.`id` AS `id`, `c`.`name` AS `name`, COUNT(`o`.`id`) AS `orders`, (`o`.`doc` ->> '$.channel') AS `channel` FROM `customers` AS `c` LEFT JOIN `orders` AS `o` ON (`o`.`customer_id` = `c`.`id`)", view, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ MariaDB → SQL Server, SQLite

    [Fact]
    public void MariaDb_to_sql_server()
    {
        var result = ConversionProject.Build(Schemas.MariaDb(), new ConversionOptions { TargetProvider = "sqlserver" });
        Dump(result);
        var tables = Change(result, "convert-tables");
        Assert.Contains("ref uniqueidentifier CONSTRAINT DF_orders_ref DEFAULT (NEWID()) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("client_ip varchar(45) NULL", tables, StringComparison.Ordinal);
        Assert.Contains("id int IDENTITY(1, 1) NOT NULL", tables, StringComparison.Ordinal);
        Assert.Contains("CREATE SEQUENCE dbo.order_seq AS bigint START WITH 1000 INCREMENT BY 1", Change(result, "convert-sequences"), StringComparison.Ordinal);
        Assert.Contains("NEXT VALUE FOR dbo.order_seq", tables, StringComparison.Ordinal);
        Assert.DoesNotContain("DEFAULT (NULL)", tables, StringComparison.Ordinal);
        Assert.Contains("VIEW dbo.customer_orders", Assert.Single(result.Project.Repeatables).Script, StringComparison.Ordinal);
    }

    [Fact]
    public void MariaDb_to_sqlite()
    {
        var result = ConversionProject.Build(Schemas.MariaDb(), new ConversionOptions { TargetProvider = "sqlite" });
        Dump(result);
        var tables = Change(result, "convert-tables");
        Assert.Contains("ref TEXT", tables, StringComparison.Ordinal);
        Assert.Contains("client_ip TEXT", tables, StringComparison.Ordinal);
        Assert.Contains("id INTEGER PRIMARY KEY AUTOINCREMENT", tables, StringComparison.Ordinal);
        Assert.Contains(result.Context.Issues, i => i.Object == "shop.order_seq" && i.Message.StartsWith("SQLite has no sequences", StringComparison.Ordinal));
        Assert.Contains("CREATE VIEW customer_orders AS", Assert.Single(result.Project.Repeatables).Script, StringComparison.Ordinal);
    }
}
