using Badger.SqlMigrate.Conversion.PgToTSql;
using Badger.SqlMigrate.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.SqlMigrate.Conversion.Tests;

public class PgToTSqlTests(ITestOutputHelper output)
{
    private static string Expr(string pg) => new PgToTSqlTranslator(Schemas.ToTSql()).Expression(pg, new ExpressionSite("public.orders"));

    private static string Pred(string pg) => new PgToTSqlTranslator(Schemas.ToTSql()).Predicate(pg, new ExpressionSite("public.orders"));

    [Theory]
    [InlineData("now()", "SYSDATETIMEOFFSET()")]
    [InlineData("false", "0")]
    [InlineData("'new'::public.order_status", "N'new'")]
    [InlineData("nextval('public.order_numbers'::regclass)", "NEXT VALUE FOR dbo.order_numbers")]
    [InlineData("(customer_name || ' #'::text) || id", "(customer_name + N' #') + CAST(id AS nvarchar(max))")]
    [InlineData("created + '30 days'::interval", "DATEADD(day, 30, created)")]
    [InlineData("created - (paid_days * '1 day'::interval)", "DATEADD(day, -paid_days, created)")]
    [InlineData("COALESCE(updated, created)", "COALESCE(updated, created)")]
    [InlineData("date_trunc('month'::text, created)", "DATETRUNC(month, created)")]
    [InlineData("EXTRACT(year FROM created)", "DATEPART(year, created)")]
    [InlineData("to_char(created, 'YYYY-MM-DD'::text)", "CONVERT(varchar(10), created, 23)")]
    [InlineData("substr(customer_name, 1, 3)", "SUBSTRING(customer_name, 1, 3)")]
    [InlineData("(total > (100)::numeric)", "CASE WHEN (total > 100) THEN 1 ELSE 0 END")]
    [InlineData("gen_random_uuid()", "NEWID()")]
    public void Expressions(string pg, string expected) => Assert.Equal(expected, Expr(pg).Split(" /*")[0]);

    [Theory]
    [InlineData("(total >= (0)::numeric)", "(total >= 0)")]
    [InlineData("paid", "paid = 1")]
    [InlineData("NOT paid", "paid = 0")]
    [InlineData("(paid IS TRUE) AND (status = ANY (ARRAY['new'::text, 'paid'::text]))", "(paid = 1) AND (status IN (N'new', N'paid'))")]
    [InlineData("customer_name ~~ 'A[1]%'::text", "customer_name LIKE N'A[[]1]%'")]
    [InlineData("customer_name ILIKE 'smi%'", "LOWER(customer_name) LIKE LOWER(N'smi%')")]
    [InlineData("updated IS NOT DISTINCT FROM created", "updated IS NOT DISTINCT FROM created")]
    public void Predicates(string pg, string expected) => Assert.Equal(expected, Pred(pg));

    private TranslatedCode Translate(PortableCode code, params PortableCode[] others)
    {
        var result = new PgToTSqlTranslator(Schemas.ToTSql()).Code(code, [code, .. others]);
        output.WriteLine(result.Script);
        return result;
    }

    [Fact]
    public void View()
    {
        var code = new PortableCode(PortableCodeKind.View, new PortableName("public", "open_orders"), """
            CREATE VIEW public.open_orders AS
             SELECT o.id,
                o.customer_name,
                o.total,
                count(*) OVER (PARTITION BY o.customer_name) AS customer_orders
               FROM public.orders o
              WHERE (NOT o.paid) AND (o.created > (now() - '7 days'::interval))
              ORDER BY o.created DESC
             LIMIT 100;
            """);
        var script = Translate(code).Script;
        Assert.Contains("CREATE OR ALTER VIEW dbo.open_orders\nAS\nSELECT TOP (100) o.id, o.customer_name, o.total, COUNT(*) OVER (PARTITION BY o.customer_name) AS customer_orders FROM dbo.orders AS o WHERE (o.paid = 0) AND (o.created > (DATEADD(day, -7, SYSDATETIMEOFFSET()))) ORDER BY o.created DESC;", script);
    }

    [Fact]
    public void Plpgsql_function_that_writes_becomes_a_procedure()
    {
        var code = new PortableCode(PortableCodeKind.Function, new PortableName("public", "mark_paid"), """
            CREATE OR REPLACE FUNCTION public.mark_paid(p_id bigint, p_amount numeric DEFAULT 0)
             RETURNS integer
             LANGUAGE plpgsql
            AS $function$
            DECLARE
                v_total numeric(12,2);
                v_count integer := 0;
            BEGIN
                SELECT total INTO STRICT v_total FROM public.orders WHERE id = p_id;
                IF v_total IS NULL THEN
                    RAISE EXCEPTION 'order % not found', p_id;
                ELSIF v_total > p_amount THEN
                    RAISE NOTICE 'partial payment for %', p_id;
                END IF;
                FOR i IN 1..3 LOOP
                    v_count := v_count + i;
                    EXIT WHEN v_count > 3;
                END LOOP;
                UPDATE public.orders SET paid = true, updated = now() WHERE id = p_id RETURNING id INTO v_count;
                GET DIAGNOSTICS v_count = ROW_COUNT;
                RETURN v_count;
            EXCEPTION WHEN unique_violation THEN
                RETURN -1;
            END;
            $function$
            """) { Language = "plpgsql" };
        var result = Translate(code);
        var script = result.Script;
        Assert.Equal("procedures", result.Folder);
        Assert.Contains("CREATE OR ALTER PROCEDURE dbo.mark_paid\n    @p_id bigint,\n    @p_amount decimal(38,10) = 0,\n    @result int OUTPUT", script);
        Assert.Contains("SELECT @v_total = total FROM dbo.orders WHERE id = @p_id;", script);
        Assert.Contains("SET @bsm_message = FORMATMESSAGE(N'order %s not found', CAST(@p_id AS nvarchar(4000)));", script);
        Assert.Contains("THROW 50000, @bsm_message, 1;", script);
        Assert.Contains("WHILE @i <= 3", script);
        Assert.Contains("UPDATE dbo.orders SET paid = 1, updated = SYSDATETIMEOFFSET() OUTPUT inserted.id INTO @bsm_out_", script);
        Assert.Contains("SET @v_count = @bsm_rowcount;", script);
        Assert.Contains("IF ERROR_NUMBER() IN (2601, 2627)", script);
    }

    [Fact]
    public void Sql_function_returning_table_becomes_inline_tvf()
    {
        var code = new PortableCode(PortableCodeKind.Function, new PortableName("public", "orders_since"), """
            CREATE OR REPLACE FUNCTION public.orders_since(since timestamp with time zone)
             RETURNS TABLE(id bigint, total numeric)
             LANGUAGE sql
             STABLE
            AS $function$
              SELECT o.id, o.total FROM public.orders o WHERE o.created >= since ORDER BY o.created
            $function$
            """) { Language = "sql" };
        var script = Translate(code).Script;
        Assert.Contains("CREATE OR ALTER FUNCTION dbo.orders_since(@since datetimeoffset(6))\nRETURNS TABLE\nAS\nRETURN\n(\nSELECT o.id, o.total FROM dbo.orders AS o WHERE o.created >= @since", script);
    }

    [Fact]
    public void Before_row_trigger_writes_new_values_back()
    {
        var function = new PortableCode(PortableCodeKind.Function, new PortableName("public", "touch_updated"), """
            CREATE OR REPLACE FUNCTION public.touch_updated()
             RETURNS trigger
             LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF TG_OP = 'UPDATE' AND NEW.total < 0 THEN
                    RAISE EXCEPTION 'negative total';
                END IF;
                NEW.updated := now();
                RETURN NEW;
            END;
            $function$
            """) { Language = "plpgsql" };
        var trigger = new PortableCode(PortableCodeKind.Trigger, new PortableName("public", "orders_touch"),
            "CREATE TRIGGER orders_touch BEFORE INSERT OR UPDATE ON public.orders FOR EACH ROW EXECUTE FUNCTION public.touch_updated();")
        {
            Table = new PortableName("public", "orders"),
            TriggerFunction = new PortableName("public", "touch_updated"),
        };
        var script = Translate(trigger, function).Script;
        Assert.Contains("CREATE OR ALTER TRIGGER dbo.orders_touch ON dbo.orders\nAFTER INSERT, UPDATE", script);
        Assert.Contains("FROM inserted AS i FULL JOIN deleted AS d ON i.id = d.id", script);
        Assert.Contains("IF @tg_op = N'UPDATE' AND @new_total < 0", script);
        Assert.Contains("SET @new_updated = SYSDATETIMEOFFSET();", script);
        Assert.Contains("UPDATE dbo.orders SET updated = @new_updated WHERE id = @bsm_key_id;", script);
        Assert.Equal(string.Empty, new PgToTSqlTranslator(Schemas.ToTSql()).Code(function, [function, trigger]).Script);
    }
}
