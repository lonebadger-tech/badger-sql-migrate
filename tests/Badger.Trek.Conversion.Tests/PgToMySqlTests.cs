using Badger.Trek.Conversion.MySql;
using Badger.Trek.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.Trek.Conversion.Tests;

/// <summary>PostgreSQL expressions and views written as MySQL 8, and SQL Server to MySQL by way of PostgreSQL.</summary>
public class PgToMySqlTests(ITestOutputHelper output)
{
    private string Expr(string pg, string? targetType = null)
    {
        var result = new PgToMySqlTranslator(Schemas.ToMySql()).Expression(pg, new ExpressionSite("public.orders", targetType));
        output.WriteLine(result);
        return result;
    }

    private TranslatedCode Translate(PortableCode code)
    {
        var result = new PgToMySqlTranslator(Schemas.ToMySql()).Code(code, [code]);
        output.WriteLine(result.Script);
        return result;
    }

    private static PortableCode View(string name, string body) =>
        new(PortableCodeKind.View, new PortableName("public", name), $"CREATE VIEW public.{name} AS\n{body}");

    [Theory]
    [InlineData("now()", "CURRENT_TIMESTAMP")]
    [InlineData("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP")]
    [InlineData("'new'::public.order_status", "'new'")]
    [InlineData("false", "FALSE")]
    [InlineData("'it''s a \\ test'::text", "'it''s a \\\\ test'")]
    [InlineData("(total >= (0)::numeric)", "(`total` >= 0)")]
    [InlineData("(customer_name || ' (vip)'::text)", "(CONCAT(`customer_name`, ' (vip)'))")]
    [InlineData("((customer_name || ' '::text) || status)", "(CONCAT(`customer_name`, ' ', `status`))")]
    [InlineData("concat(customer_name, updated, '!'::text)", "CONCAT(`customer_name`, COALESCE(`updated`, ''), '!')")]
    [InlineData("concat_ws(', '::text, customer_name, status)", "CONCAT_WS(', ', `customer_name`, `status`)")]
    [InlineData("COALESCE(updated, created)", "COALESCE(`updated`, `created`)")]
    [InlineData("length(customer_name)", "CHAR_LENGTH(`customer_name`)")]
    [InlineData("\"substring\"(customer_name, 1, 3)", "SUBSTRING(`customer_name`, 1, 3)")]
    [InlineData("strpos(customer_name, 'x'::text)", "LOCATE('x', `customer_name`)")]
    [InlineData("upper(TRIM(BOTH FROM customer_name))", "UPPER(TRIM(`customer_name`))")]
    [InlineData("gen_random_uuid()", "UUID()")]
    [InlineData("(created + '1 day'::interval)", "(`created` + INTERVAL 1 DAY)")]
    [InlineData("(created - '02:30:00'::interval)", "(`created` - INTERVAL 2 HOUR - INTERVAL 30 MINUTE)")]
    [InlineData("(created + ((id)::double precision * '1 mon'::interval))", "(`created` + INTERVAL (CAST(`id` AS DOUBLE)) MONTH)")]
    [InlineData("EXTRACT(year FROM created)", "EXTRACT(YEAR FROM `created`)")]
    [InlineData("EXTRACT(dow FROM created)", "(DAYOFWEEK(`created`) - 1)")]
    [InlineData("date_trunc('month'::text, created)", "CAST(DATE_FORMAT(`created`, '%Y-%m-01') AS DATETIME)")]
    [InlineData("to_char(created, 'YYYY-MM-DD HH24:MI'::text)", "DATE_FORMAT(`created`, '%Y-%m-%d %H:%i')")]
    [InlineData("(id)::text", "CAST(`id` AS CHAR)")]
    [InlineData("(total)::integer", "CAST(`total` AS SIGNED)")]
    [InlineData("(id)::numeric(10,2)", "CAST(`id` AS DECIMAL(10,2))")]
    [InlineData("(created)::date", "CAST(`created` AS DATE)")]
    [InlineData("(created)::timestamp without time zone", "CAST(`created` AS DATETIME(6))")]
    [InlineData("(customer_name ~~* 'a%'::text)", "(`customer_name` LIKE 'a%')")]
    [InlineData("(customer_name ~ '^A'::text)", "(REGEXP_LIKE(`customer_name`, '^A', 'c'))")]
    [InlineData("(customer_name !~* '^a'::text)", "(NOT REGEXP_LIKE(`customer_name`, '^a', 'i'))")]
    [InlineData("(doc ->> 'name'::text)", "(`doc` ->> '$.name')")]
    [InlineData("((doc -> 'customer'::text) ->> 'email'::text)", "(`doc` ->> '$.customer.email')")]
    [InlineData("(doc #>> '{lines,0,sku}'::text[])", "(`doc` ->> '$.lines[0].sku')")]
    [InlineData("(id / 2)", "(`id` DIV 2)")]
    [InlineData("(total / (2)::numeric)", "(`total` / 2)")]
    [InlineData("(updated IS DISTINCT FROM created)", "(NOT (`updated` <=> `created`))")]
    [InlineData("(updated IS NOT DISTINCT FROM created)", "(`updated` <=> `created`)")]
    [InlineData("((status)::text = ANY (ARRAY['new'::text, 'paid'::text]))", "(CAST(`status` AS CHAR) IN ('new', 'paid'))")]
    [InlineData("CASE WHEN paid THEN 'y'::text ELSE 'n'::text END", "CASE WHEN `paid` THEN 'y' ELSE 'n' END")]
    [InlineData("(2 ^ 3)", "(POW(2, 3))")]
    [InlineData("round(total, 1)", "ROUND(`total`, 1)")]
    public void Expressions(string pg, string expected) => Assert.Equal(expected, Expr(pg));

    [Theory]
    [InlineData("datetime(6)", "CURRENT_TIMESTAMP(6)")]
    [InlineData("timestamp(3)", "CURRENT_TIMESTAMP(3)")]
    [InlineData("datetime", "CURRENT_TIMESTAMP")]
    public void A_current_timestamp_default_matches_the_column_precision(string type, string expected) =>
        Assert.Equal(expected, Expr("now()", type));

    [Theory]
    [InlineData("(customer_name ~~ 'a%'::text)", "MySQL's LIKE ignores case")]
    [InlineData("string_agg(customer_name, ', '::text)", "group_concat_max_len")]
    [InlineData("greatest(total, 0::numeric)", "skips NULLs")]
    [InlineData("nextval('orders_id_seq'::regclass)", "MySQL has no sequences")]
    [InlineData("btrim(customer_name, 'xy'::text)", "trimming a set of characters")]
    [InlineData("(created AT TIME ZONE 'Europe/Paris'::text)", "CONVERT_TZ")]
    [InlineData("(tags @> ARRAY['a'::text])", "operator @> has no MySQL counterpart")]
    [InlineData("(created - updated)", "TIMESTAMPDIFF")]
    [InlineData("initcap(customer_name)", "initcap() was kept as written")]
    public void What_cannot_be_mapped_faithfully_is_flagged(string pg, string message)
    {
        var sql = Expr(pg);
        Assert.Contains("REVIEW trek convert", sql, StringComparison.Ordinal);
        Assert.Contains(message, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_becomes_create_or_replace_view_in_mysql()
    {
        var result = Translate(View("order_summary", """
             SELECT o.customer_name,
                count(*) FILTER (WHERE o.paid) AS paid_orders,
                string_agg((o.status)::text, ', '::text ORDER BY o.created DESC) AS statuses,
                max(o.updated) AS last_update
               FROM public.orders o
              WHERE (o.total > (100)::numeric)
              GROUP BY o.customer_name
              ORDER BY (max(o.updated)), o.customer_name
             LIMIT 10 OFFSET 20;
            """));

        Assert.Equal("views", result.Folder);
        Assert.Equal("order_summary.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from PostgreSQL view public.order_summary.", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW `order_summary` AS", result.Script, StringComparison.Ordinal);
        Assert.Contains("SELECT `o`.`customer_name`, COUNT(CASE WHEN `o`.`paid` THEN 1 END) AS `paid_orders`", result.Script, StringComparison.Ordinal);
        Assert.Contains("GROUP_CONCAT(CAST(`o`.`status` AS CHAR) ORDER BY `o`.`created` DESC SEPARATOR ', ') /* REVIEW", result.Script, StringComparison.Ordinal);
        Assert.Contains("FROM `orders` AS `o` WHERE (`o`.`total` > 100) GROUP BY `o`.`customer_name`", result.Script, StringComparison.Ordinal);

        // PostgreSQL sorts NULLs last ascending, MySQL first: the nullable sort key sorts on IS NULL first; the NOT NULL one is left alone.
        Assert.Contains("ORDER BY MAX(`o`.`updated`) IS NULL, MAX(`o`.`updated`), `o`.`customer_name` LIMIT 10 OFFSET 20;", result.Script, StringComparison.Ordinal);
        Assert.Contains("group_concat_max_len", result.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("FILTER", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Offset_without_limit_gets_mysqls_largest_limit()
    {
        var script = Translate(View("rest", "SELECT o.id FROM public.orders o ORDER BY o.id OFFSET 5;")).Script;
        Assert.Contains("ORDER BY `o`.`id` LIMIT 18446744073709551615 OFFSET 5;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_materialized_view_becomes_a_plain_view_and_is_flagged()
    {
        var code = new PortableCode(PortableCodeKind.View, new PortableName("public", "totals"), "CREATE MATERIALIZED VIEW public.totals AS\n SELECT sum(o.total) AS total FROM public.orders o;");
        var result = Translate(code);
        Assert.Contains("CREATE OR REPLACE VIEW `totals` AS\nSELECT SUM(`o`.`total`) AS `total` FROM `orders` AS `o`;", result.Script, StringComparison.Ordinal);
        Assert.Contains("materialized view became a plain view", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_in_another_schema_gets_a_prefixed_name()
    {
        var code = new PortableCode(PortableCodeKind.View, new PortableName("sales", "open_orders"), "CREATE VIEW sales.open_orders AS\n SELECT o.id FROM public.orders o WHERE (NOT o.paid);");
        var result = Translate(code);
        Assert.Equal("sales_open_orders.sql", result.FileName);
        Assert.Contains("CREATE OR REPLACE VIEW `sales_open_orders` AS", result.Script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PortableCodeKind.Function, "functions")]
    [InlineData(PortableCodeKind.Procedure, "procedures")]
    [InlineData(PortableCodeKind.Trigger, "triggers")]
    public void Routines_and_triggers_are_written_commented_out_for_review(PortableCodeKind kind, string folder)
    {
        var code = new PortableCode(kind, new PortableName("public", "touch"), "CREATE OR REPLACE FUNCTION public.touch()\n RETURNS integer\n LANGUAGE sql\nAS $function$ SELECT 1 $function$");
        var result = Translate(code);
        Assert.Equal(folder, result.Folder);
        Assert.Equal("touch.sql", result.FileName);
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for MySQL", result.Script, StringComparison.Ordinal);
        Assert.Contains("-- CREATE OR REPLACE FUNCTION public.touch()", result.Script, StringComparison.Ordinal);
        Assert.Single(result.Issues);
    }

    [Fact]
    public void SqlServer_reaches_mysql_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.SqlServer(), new ConversionOptions { TargetProvider = "mysql" });
        var translator = ConversionProject.TranslatorFor(ctx);
        Assert.IsType<ChainTranslator>(translator);

        var check = translator.Predicate("([Balance]>=(0))", new ExpressionSite("dbo.Customer"));
        output.WriteLine(check);
        Assert.Equal("`Balance` >= 0", check);

        var created = translator.Expression("sysdatetime()", new ExpressionSite("dbo.Customer", "datetime(6)"));
        output.WriteLine(created);
        Assert.Equal("CURRENT_TIMESTAMP(6)", created);

        var view = new PortableCode(PortableCodeKind.View, new PortableName("dbo", "ActiveCustomers"),
            "CREATE VIEW dbo.ActiveCustomers AS SELECT TOP (5) c.FirstName + ' ' + c.LastName AS FullName FROM dbo.Customer c WHERE c.IsActive = 1 ORDER BY c.LastName");
        var result = translator.Code(view, [view]);
        output.WriteLine(result.Script);
        Assert.Equal("ActiveCustomers.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from SQL Server view dbo.ActiveCustomers (by way of PostgreSQL).", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW `ActiveCustomers` AS", result.Script, StringComparison.Ordinal);
        Assert.Contains("CONCAT(`c`.`FirstName`, ' ', `c`.`LastName`) AS `FullName`", result.Script, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5", result.Script, StringComparison.Ordinal);
    }
}
