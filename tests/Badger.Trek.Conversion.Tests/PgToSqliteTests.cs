using Badger.Trek.Conversion.Sqlite;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Plan;
using Badger.Trek.Core.Providers;
using Badger.Trek.Providers.Sqlite;
using Xunit.Abstractions;

namespace Badger.Trek.Conversion.Tests;

/// <summary>
/// Converted SQLite projects run on SQLite 3.26 and later (sqlite.minVersion's default), so PostgreSQL syntax that
/// SQLite only learned later is written the older way, or marked for review when there is no older way.
/// </summary>
public class PgToSqliteTests(ITestOutputHelper output)
{
    private string Expr(string pg)
    {
        var result = new PgToSqliteTranslator(Schemas.ToSqlite()).Expression(pg, new ExpressionSite("public.orders"));
        output.WriteLine(result);
        return result;
    }

    private string Translate(PortableCode code, params PortableCode[] others)
    {
        var result = new PgToSqliteTranslator(Schemas.ToSqlite()).Code(code, [code, .. others]);
        output.WriteLine(result.Script);
        return result.Script;
    }

    private static PortableCode View(string name, string body) =>
        new(PortableCodeKind.View, new PortableName("public", name), $"CREATE VIEW public.{name} AS\n{body}");

    /// <summary>trek's own check (TREK008) for SQL newer than the default sqlite.minVersion, 3.26.</summary>
    private static List<string> NewerThan326(string script) =>
        [.. new SqliteLinter().Lint(script, new LintContext("converted", "deploy", ChangeFlags.None)).Where(d => d.Code == "TREK008").Select(d => d.Message)];

    [Theory]
    [InlineData("(doc ->> 'name'::text)", "json_extract(doc, '$.name')")]
    [InlineData("((doc -> 'customer'::text) ->> 'email'::text)", "json_extract(doc, '$.customer.email')")]
    [InlineData("((doc -> 'lines'::text) -> 0) ->> 'sku'::text", "json_extract(doc, '$.lines[0].sku')")]
    [InlineData("(doc #>> '{customer,addresses,1,city}'::text[])", "json_extract(doc, '$.customer.addresses[1].city')")]
    [InlineData("(doc ->> 'odd key'::text)", "json_extract(doc, '$.\"odd key\"')")]
    public void Json_operators_become_json_extract(string pg, string expected)
    {
        var sql = Expr(pg);
        Assert.Contains(expected, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("->", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_json_valued_arrow_keeps_json_and_is_flagged()
    {
        var sql = Expr("(doc -> 'customer'::text)");
        Assert.StartsWith("(json_quote(json_extract(doc, '$.customer'))", sql, StringComparison.Ordinal);
        Assert.Contains("REVIEW trek convert", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Nulls_last_sorts_on_is_null_and_the_default_order_is_left_alone()
    {
        var script = Translate(View("by_update", """
             SELECT o.id FROM public.orders o
              ORDER BY o.updated NULLS FIRST, o.created DESC NULLS LAST, o.total DESC NULLS FIRST;
            """));
        Assert.Contains("ORDER BY o.updated, o.created DESC, o.total IS NOT NULL, o.total DESC", script, StringComparison.Ordinal);
        Assert.DoesNotContain("NULLS", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_filter_becomes_case_and_aggregate_order_by_is_flagged()
    {
        var script = Translate(View("order_stats", """
             SELECT o.customer_name,
                count(*) FILTER (WHERE o.paid) AS paid_orders,
                sum(o.total) FILTER (WHERE (NOT o.paid)) AS open_total,
                string_agg(o.status::text, ', '::text ORDER BY o.created) AS statuses
               FROM public.orders o
              GROUP BY o.customer_name;
            """));
        Assert.Contains("count(CASE WHEN o.paid THEN 1 END) AS paid_orders", script, StringComparison.Ordinal);
        Assert.Contains("sum(CASE WHEN (NOT o.paid) THEN o.total END) AS open_total", script, StringComparison.Ordinal);
        Assert.Contains("group_concat(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("FILTER", script, StringComparison.Ordinal);
        Assert.Contains("ORDER BY inside group_concat() needs SQLite 3.44", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Right_join_becomes_a_left_join_and_full_join_is_flagged()
    {
        var right = Translate(View("all_customers", """
             SELECT c.name, o.id FROM public.orders o
               RIGHT JOIN public.customers c ON c.name = o.customer_name;
            """));
        Assert.Contains("FROM customers AS c LEFT JOIN orders AS o ON c.name = o.customer_name", right, StringComparison.Ordinal);
        Assert.DoesNotContain("RIGHT JOIN", right, StringComparison.Ordinal);

        var full = Translate(View("everything", """
             SELECT c.name, o.id FROM public.orders o
               FULL JOIN public.customers c ON c.name = o.customer_name;
            """));
        Assert.Contains("FULL JOIN needs SQLite 3.39", full, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_from_in_a_trigger_becomes_correlated_subqueries_and_returning_is_dropped()
    {
        var function = new PortableCode(PortableCodeKind.Function, new PortableName("public", "sync_totals"), """
            CREATE OR REPLACE FUNCTION public.sync_totals()
             RETURNS trigger
             LANGUAGE plpgsql
            AS $function$
            BEGIN
                UPDATE public.customers c SET order_total = s.total
                  FROM public.order_sums s
                 WHERE s.customer_name = c.name AND c.name = NEW.customer_name;
                INSERT INTO public.audit (order_id) VALUES (NEW.id) RETURNING id;
                RETURN NEW;
            END;
            $function$
            """) { Language = "plpgsql" };
        var trigger = new PortableCode(PortableCodeKind.Trigger, new PortableName("public", "orders_sync"),
            "CREATE TRIGGER orders_sync AFTER INSERT ON public.orders FOR EACH ROW EXECUTE FUNCTION public.sync_totals();")
        {
            Table = new PortableName("public", "orders"),
            TriggerFunction = new PortableName("public", "sync_totals"),
        };

        var script = Translate(trigger, function);

        // SQLite rejects an alias on the target table of a statement in a trigger: the table name stands in for it.
        Assert.Contains("UPDATE customers SET order_total = (SELECT s.total FROM order_sums AS s WHERE s.customer_name = customers.name AND customers.name = NEW.customer_name) WHERE EXISTS (SELECT 1 FROM order_sums AS s WHERE s.customer_name = customers.name AND customers.name = NEW.customer_name);", script, StringComparison.Ordinal);
        Assert.Contains("RETURNING was left out", script, StringComparison.Ordinal);
        Assert.Empty(NewerThan326(script));
    }

    [Fact]
    public void Rewritten_views_pass_trek_lint_for_sqlite_3_26()
    {
        var script = Translate(View("rewritten", """
             SELECT c.name,
                (o.doc -> 'customer'::text) ->> 'email'::text AS email,
                count(*) FILTER (WHERE o.paid) AS paid_orders
               FROM public.orders o
               RIGHT JOIN public.customers c ON c.name = o.customer_name
              WHERE o.updated IS DISTINCT FROM o.created
              GROUP BY c.name, email
              ORDER BY c.name NULLS LAST;
            """));

        Assert.Empty(NewerThan326(script));
    }
}
