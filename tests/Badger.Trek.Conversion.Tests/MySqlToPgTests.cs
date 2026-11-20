using Badger.Trek.Conversion.MySql;
using Badger.Trek.Conversion.PgToTSql;
using Badger.Trek.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.Trek.Conversion.Tests;

/// <summary>
/// MySQL expressions (as information_schema reports them) and views (as SHOW CREATE VIEW writes them) in PostgreSQL,
/// and MySQL to SQL Server and SQLite by way of PostgreSQL.
/// </summary>
public class MySqlToPgTests(ITestOutputHelper output)
{
    private string Expr(string mysql, string? targetType = null)
    {
        var result = new MySqlToPgTranslator(Schemas.MySqlToPg()).Expression(mysql, new ExpressionSite("shop.orders", targetType));
        output.WriteLine(result);
        return result;
    }

    private TranslatedCode Translate(PortableCode code)
    {
        var result = new MySqlToPgTranslator(Schemas.MySqlToPg()).Code(code, [code]);
        output.WriteLine(result.Script);
        return result;
    }

    private static PortableCode View(string name, string body) =>
        new(PortableCodeKind.View, new PortableName("shop", name), $"CREATE ALGORITHM=UNDEFINED SQL SECURITY DEFINER VIEW `{name}` AS {body}");

    [Theory]
    [InlineData("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP")]
    [InlineData("CURRENT_TIMESTAMP(6)", "CURRENT_TIMESTAMP")]
    [InlineData("(now())", "now()")]
    [InlineData("(uuid())", "gen_random_uuid()")]
    [InlineData("'abc'", "'abc'")]
    [InlineData("'it\\'s a \\\\ test'", "'it''s a \\ test'")]
    [InlineData("'a\\_b'", "'a\\_b'")]
    [InlineData("_utf8mb4'new'", "'new'")]
    [InlineData("0", "0")]
    [InlineData("(`total` >= 0)", "(total >= 0)")]
    [InlineData("(`price` * `qty`)", "(price * qty)")]
    [InlineData("ifnull(`updated`,`created`)", "coalesce(updated, created)")]
    [InlineData("if((`qty` > 10),'bulk','single')", "CASE WHEN (qty > 10) THEN 'bulk' ELSE 'single' END")]
    [InlineData("if(`qty`,1,2)", "CASE WHEN (qty) <> 0 THEN 1 ELSE 2 END")]
    [InlineData("concat(`customer_name`,' x')", "(customer_name || ' x')")]
    [InlineData("concat(`qty`,'x')", "(CAST(qty AS text) || 'x')")]
    [InlineData("concat_ws(',',`customer_name`,`status`)", "concat_ws(',', customer_name, status)")]
    [InlineData("(`updated` <=> `created`)", "(updated IS NOT DISTINCT FROM created)")]
    [InlineData("((`qty` > 1) || (`price` < 2))", "((qty > 1) OR (price < 2))")]
    [InlineData("((`qty` > 1) && !(`paid`))", "((qty > 1) AND NOT paid)")]
    [InlineData("(`qty` div 2)", "(qty / 2)")]
    [InlineData("(`price` div 2)", "(div(price, 2))")]
    [InlineData("(`qty` / 2)", "(CAST(qty AS numeric) / 2)")]
    [InlineData("(`price` / 2)", "(price / 2)")]
    [InlineData("(`qty` mod 3)", "(qty % 3)")]
    [InlineData("(`qty` ^ 3)", "(qty # 3)")]
    [InlineData("(`customer_name` regexp '^a')", "(customer_name ~* '^a')")]
    [InlineData("(not((`customer_name` rlike '^a')))", "(NOT (customer_name ~* '^a'))")]
    [InlineData("(`customer_name` not regexp '^a')", "(customer_name !~* '^a')")]
    [InlineData("(`customer_name` like 'a%')", "(customer_name ILIKE 'a%')")]
    [InlineData("(`created` + interval 1 day)", "(created + interval '1 day')")]
    [InlineData("(`created` - interval '1:30' hour_minute)", "(created - interval '1 hour 30 minute')")]
    [InlineData("(`created` + interval 1 quarter)", "(created + interval '3 month')")]
    [InlineData("date_add(`created`,interval `qty` day)", "(created + qty * interval '1 day')")]
    [InlineData("date_sub(`created`,interval (`qty` + 1) week)", "(created - (qty + 1) * interval '1 week')")]
    [InlineData("x'4142'", "bytea '\\x4142'")]
    [InlineData("0x4142", "bytea '\\x4142'")]
    [InlineData("b'101'", "5")]
    [InlineData("cast(`price` as signed)", "CAST(price AS bigint)")]
    [InlineData("cast(`qty` as char charset utf8mb4)", "CAST(qty AS text)")]
    [InlineData("cast(`price` as decimal(10,2))", "CAST(price AS numeric(10,2))")]
    [InlineData("convert(`created`, date)", "CAST(created AS date)")]
    [InlineData("cast(`created` as datetime)", "CAST(created AS timestamp(0))")]
    [InlineData("year(`created`)", "EXTRACT(year FROM created)")]
    [InlineData("extract(month from `created`)", "EXTRACT(month FROM created)")]
    [InlineData("dayofweek(`created`)", "(EXTRACT(dow FROM created) + 1)")]
    [InlineData("date_format(`created`,'%Y-%m-%d %H:%i')", "to_char(created, 'YYYY-MM-DD HH24:MI')")]
    [InlineData("datediff(`updated`,`created`)", "(CAST(updated AS date) - CAST(created AS date))")]
    [InlineData("timestampdiff(HOUR,`created`,`updated`)", "CAST(trunc(extract(epoch FROM (CAST(updated AS timestamp) - CAST(created AS timestamp))) / 3600) AS bigint)")]
    [InlineData("json_unquote(json_extract(`doc`,'$.name'))", "(doc ->> 'name')")]
    [InlineData("json_extract(`doc`,'$.lines[0]')", "(doc #> '{lines,0}')")]
    [InlineData("(`doc` ->> '$.customer.email')", "((doc #>> '{customer,email}'))")]
    [InlineData("(`doc` -> '$.\"odd key\"')", "((doc -> 'odd key'))")]
    [InlineData("json_extract(`doc`,'$.\"a,b\".c')", "(doc #> '{\"a,b\",c}')")]
    [InlineData("char_length(`customer_name`)", "char_length(customer_name)")]
    [InlineData("length(`customer_name`)", "octet_length(customer_name)")]
    [InlineData("locate('a',`customer_name`)", "strpos(customer_name, 'a')")]
    [InlineData("substr(`customer_name`,2,3)", "substr(customer_name, 2, 3)")]
    [InlineData("trim(both 'x' from `customer_name`)", "btrim(customer_name, 'x')")]
    [InlineData("ucase(`customer_name`)", "upper(customer_name)")]
    [InlineData("(`paid` = 1)", "(paid = true)")]
    [InlineData("(`paid` is true)", "(paid IS TRUE)")]
    [InlineData("truncate(`price`,1)", "trunc(price, 1)")]
    [InlineData("log(`price`)", "ln(price)")]
    public void Expressions(string mysql, string expected) => Assert.Equal(expected, Expr(mysql));

    [Theory]
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    [InlineData("b'1'", "true")]
    public void A_tinyint_default_for_a_boolean_column_becomes_true_or_false(string mysql, string expected) =>
        Assert.Equal(expected, Expr(mysql, "boolean"));

    [Theory]
    [InlineData("greatest(`price`,`qty`)", "skips NULLs")]
    [InlineData("substr(`customer_name`,-3)", "negative start")]
    [InlineData("trim(both 'xy' from `customer_name`)", "TRIM of a whole string")]
    [InlineData("json_extract(`doc`,'$.lines[*].sku')", "JSON path not translated")]
    [InlineData("extract(year_month from `created`)", "EXTRACT(YEAR_MONTH) not translated")]
    [InlineData("field(`status`,'new','paid')", "field() was kept as written")]
    [InlineData("current_user()", "'user@host'")]
    public void What_cannot_be_mapped_faithfully_is_flagged(string mysql, string message)
    {
        var sql = Expr(mysql);
        Assert.Contains("REVIEW trek convert", sql, StringComparison.Ordinal);
        Assert.Contains(message, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparsable_expression_is_kept_for_review()
    {
        var sql = Expr("match (`customer_name`) against ('x')");
        Assert.StartsWith("NULL /* REVIEW trek convert: could not translate", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_create_view_output_becomes_a_postgresql_view()
    {
        var result = Translate(View("big_orders", "select `t`.`id` AS `id`,`t`.`total` AS `total` from `orders` `t` where (`t`.`total` > 100)"));
        Assert.Equal("views", result.Folder);
        Assert.Equal("public.big_orders.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from MySQL view shop.big_orders. Check it before deploying.", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW public.big_orders AS\nSELECT t.id AS id, t.total AS total FROM public.orders AS t WHERE (t.total > 100);", result.Script, StringComparison.Ordinal);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Aggregates_joins_and_limits_from_mysql()
    {
        var result = Translate(View("customer_orders", """
            select `c`.`name` AS `name`,count(0) AS `orders`,group_concat(distinct `o`.`status` order by `o`.`status` ASC separator '; ') AS `statuses`,sum(`o`.`paid`) AS `paid_count`,max(`o`.`updated`) AS `last_update` from (`customers` `c` left join `orders` `o` on((`o`.`customer_name` = `c`.`name`))) where (`c`.`email` is not null) group by `c`.`name` order by `last_update` desc,`c`.`name` limit 10,20
            """));
        Assert.Contains("SELECT c.name AS name, count(*) AS orders, string_agg(DISTINCT CAST(o.status AS text), '; ' ORDER BY o.status) AS statuses", result.Script, StringComparison.Ordinal);
        Assert.Contains("sum(CAST(o.paid AS integer)) AS paid_count", result.Script, StringComparison.Ordinal);
        Assert.Contains("FROM public.customers AS c LEFT JOIN public.orders AS o ON (o.customer_name = c.name) WHERE (c.email IS NOT NULL) GROUP BY c.name", result.Script, StringComparison.Ordinal);

        // MySQL sorts NULLs first ascending and last descending: written out for PostgreSQL, which does the opposite.
        Assert.Contains("ORDER BY last_update DESC NULLS LAST, c.name LIMIT 20 OFFSET 10;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_with_a_column_list_and_check_option()
    {
        var code = new PortableCode(PortableCodeKind.View, new PortableName("shop", "Paid_Orders"),
            "CREATE ALGORITHM=MERGE DEFINER=`root`@`%` SQL SECURITY INVOKER VIEW `Paid_Orders` (`Id`,`Total`) AS select `orders`.`id` AS `id`,`orders`.`total` AS `total` from `orders` where (`orders`.`paid` = true) WITH CASCADED CHECK OPTION");
        var result = Translate(code);
        Assert.Contains("CREATE OR REPLACE VIEW public.paid_orders (id, total) AS\nSELECT orders.id AS id, orders.total AS total FROM public.orders WHERE (orders.paid = true)\nWITH CASCADED CHECK OPTION;", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparsable_view_is_commented_out()
    {
        var result = Translate(View("odd", "select match (`name`) against ('x') AS `m` from `customers`"));
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for PostgreSQL", result.Script, StringComparison.Ordinal);
        Assert.Contains("-- CREATE ALGORITHM=UNDEFINED", result.Script, StringComparison.Ordinal);
        Assert.Contains(result.Issues, i => i.StartsWith("could not parse", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PortableCodeKind.Function, "functions")]
    [InlineData(PortableCodeKind.Procedure, "procedures")]
    [InlineData(PortableCodeKind.Trigger, "triggers")]
    public void Routines_and_triggers_are_written_commented_out_for_review(PortableCodeKind kind, string folder)
    {
        var code = new PortableCode(kind, new PortableName("shop", "touch"), "CREATE DEFINER=`root`@`%` PROCEDURE `touch`()\nBEGIN\n  UPDATE orders SET updated = now();\nEND");
        var translator = new MySqlToPgTranslator(Schemas.MySqlToPg());
        var result = translator.Code(code, [code]);
        output.WriteLine(result.Script);
        Assert.Equal(folder, result.Folder);
        Assert.Equal("public.touch.sql", result.FileName);
        Assert.Contains("REVIEW trek convert: not translated; rewrite it for PostgreSQL", result.Script, StringComparison.Ordinal);
        Assert.Contains("--   UPDATE orders SET updated = now();", result.Script, StringComparison.Ordinal);
        Assert.Empty(translator.Intermediate(code, [code]));
    }

    [Fact]
    public void The_intermediate_view_has_postgresql_names_for_the_next_step()
    {
        var code = View("big_orders", "select `t`.`id` AS `id` from `orders` `t` where (`t`.`total` > 100)");
        var parts = new MySqlToPgTranslator(Schemas.MySqlToPg()).Intermediate(code, [code]);
        var view = Assert.Single(parts);
        Assert.Equal(new PortableName("public", "big_orders"), view.Name);
        Assert.Equal("CREATE VIEW public.big_orders AS\nSELECT t.id AS id FROM public.orders AS t WHERE (t.total > 100);", view.Definition);
        Assert.Equal("public.orders", new PgParser(view.Definition["CREATE VIEW public.big_orders AS\n".Length..]).QueryOnly() is { Body: PgSelectCore { From: [PgTableRef t] } } ? string.Join('.', t.Name.Parts) : null);
    }

    [Fact]
    public void MySql_reaches_sqlite_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.MySql(), new ConversionOptions { TargetProvider = "sqlite" });
        var translator = ConversionProject.TranslatorFor(ctx);
        Assert.IsType<ChainTranslator>(translator);

        var check = translator.Predicate("(`total` >= 0)", new ExpressionSite("shop.orders"));
        output.WriteLine(check);
        Assert.Equal("(total >= 0)", check);

        var name = translator.Expression("ifnull(`updated`,`created`)", new ExpressionSite("shop.orders"));
        output.WriteLine(name);
        Assert.Equal("coalesce(updated, created)", name);

        var view = View("recent", "select `o`.`id` AS `id`,(`o`.`created` + interval 7 day) AS `due` from `orders` `o` order by `o`.`id` desc limit 5");
        var result = translator.Code(view, [view]);
        output.WriteLine(result.Script);
        Assert.Equal("recent.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from MySQL view shop.recent (by way of PostgreSQL).", result.Script, StringComparison.Ordinal);
        Assert.Contains("CREATE VIEW recent AS", result.Script, StringComparison.Ordinal);
        Assert.Contains("SELECT o.id AS id, (datetime(o.created, '+7 days')) AS due FROM orders AS o ORDER BY o.id DESC LIMIT 5;", result.Script, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5", result.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void MySql_reaches_sql_server_by_way_of_postgresql()
    {
        var ctx = new ConversionContext(Schemas.MySql(), new ConversionOptions { TargetProvider = "sqlserver" });
        var translator = ConversionProject.TranslatorFor(ctx);
        Assert.IsType<ChainTranslator>(translator);

        var total = translator.Expression("(`price` * `qty`)", new ExpressionSite("shop.orders"));
        output.WriteLine(total);
        Assert.Equal("(price * qty)", total);

        var view = View("big_orders", "select `t`.`id` AS `id`,concat(`t`.`customer_name`,' (',`t`.`status`,')') AS `label` from `orders` `t` where (`t`.`total` > 100)");
        var result = translator.Code(view, [view]);
        output.WriteLine(result.Script);
        Assert.Equal("dbo.big_orders.sql", result.FileName);
        Assert.StartsWith("-- Converted by trek convert from MySQL view shop.big_orders (by way of PostgreSQL).", result.Script, StringComparison.Ordinal);
        Assert.Contains("VIEW dbo.big_orders", result.Script, StringComparison.Ordinal);
        Assert.Contains("FROM dbo.orders AS t WHERE (t.total > 100)", result.Script, StringComparison.Ordinal);

        // CONCAT() is NULL when any argument is: so is SQL Server's +.
        Assert.Contains("(t.customer_name + N' (' + t.status + N')') AS label", result.Script, StringComparison.Ordinal);
    }
}
