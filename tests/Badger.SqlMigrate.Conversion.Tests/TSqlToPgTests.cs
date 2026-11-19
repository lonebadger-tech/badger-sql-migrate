using Badger.SqlMigrate.Conversion.TSqlToPg;
using Badger.SqlMigrate.Core.Conversion;
using Xunit.Abstractions;

namespace Badger.SqlMigrate.Conversion.Tests;

public class TSqlToPgTests(ITestOutputHelper output)
{
    private static string Expr(string tsql, string? targetType = null) =>
        new TSqlToPgTranslator(Schemas.ToPg()).Expression(tsql, new ExpressionSite("dbo.Customer", targetType));

    [Theory]
    [InlineData("((0))", null, "0")]
    [InlineData("((1))", "boolean", "true")]
    [InlineData("(getdate())", null, "LOCALTIMESTAMP")]
    [InlineData("(newid())", null, "gen_random_uuid()")]
    [InlineData("isnull([FirstName],N'')", null, "COALESCE(firstname, '')")]
    [InlineData("[FirstName]+N' '+[LastName]", null, "firstname || ' ' || lastname")]
    [InlineData("[Balance]+(1)", null, "balance + (1)")]
    [InlineData("len([FirstName])", null, "char_length(rtrim(firstname))")]
    [InlineData("dateadd(day,(30),[Created])", null, "(created + ((30)) * interval '1 day')")]
    [InlineData("datediff(day,[Created],getdate())", null, "(CAST(LOCALTIMESTAMP AS date) - CAST(created AS date))")]
    [InlineData("convert(varchar(10),[Created],(23))", null, "to_char(created, 'YYYY-MM-DD')")]
    [InlineData("iif([Balance]>(0),N'credit',N'debit')", null, "CASE WHEN balance > (0) THEN 'credit' ELSE 'debit' END")]
    [InlineData("NEXT VALUE FOR [dbo].[OrderNumbers]", null, "nextval('public.ordernumbers')")]
    public void Expressions(string tsql, string? type, string expected) => Assert.Equal(expected, Expr(tsql, type));

    [Fact]
    public void Bit_comparisons_use_booleans()
    {
        var t = new TSqlToPgTranslator(Schemas.ToPg());
        Assert.Equal("isactive = true AND balance > (0)", t.Predicate("[IsActive]=(1) AND [Balance]>(0)", new ExpressionSite("x")));
    }

    [Fact]
    public void Snake_case_names()
    {
        var t = new TSqlToPgTranslator(Schemas.ToPg(NameStyle.Snake));
        Assert.Equal("first_name || ' ' || last_name", t.Expression("[FirstName]+N' '+[LastName]", new ExpressionSite("x")));
    }

    [Fact]
    public void Like_uses_case_insensitive_matching()
    {
        var t = new TSqlToPgTranslator(Schemas.ToPg());
        Assert.Equal("lastname COLLATE \"default\" ILIKE 'smi%'", t.Predicate("[LastName] LIKE N'smi%'", new ExpressionSite("x")));
    }

    [Fact]
    public void Procedure()
    {
        var code = new PortableCode(PortableCodeKind.Procedure, new PortableName("dbo", "DeactivateCustomer"), """
            CREATE PROCEDURE dbo.DeactivateCustomer @CustomerId int, @Reason nvarchar(200) = NULL, @Count int OUTPUT
            AS
            BEGIN
                SET NOCOUNT ON;
                DECLARE @Name nvarchar(101);
                SELECT @Name = FirstName + N' ' + LastName FROM dbo.Customer WHERE CustomerId = @CustomerId;
                IF @Name IS NULL
                    THROW 50001, N'No such customer', 1;
                BEGIN TRY
                    UPDATE c SET IsActive = 0 FROM dbo.Customer c JOIN dbo.Customer o ON o.CustomerId = c.CustomerId WHERE c.CustomerId = @CustomerId;
                    SET @Count = @@ROWCOUNT;
                END TRY
                BEGIN CATCH
                    PRINT ERROR_MESSAGE();
                    THROW;
                END CATCH
                SELECT TOP 5 CustomerId, FirstName FROM dbo.Customer WHERE IsActive = 1 ORDER BY Created DESC;
            END
            """);
        var result = new TSqlToPgTranslator(Schemas.ToPg()).Code(code, [code]);
        output.WriteLine(result.Script);
        Assert.Contains("CREATE OR REPLACE PROCEDURE public.deactivatecustomer(p_customerid integer, p_reason varchar(200) DEFAULT NULL, INOUT p_count integer DEFAULT NULL)", result.Script);
        Assert.Contains("SELECT firstname || ' ' || lastname INTO v_name FROM public.customer WHERE customerid = p_customerid;", result.Script);
        Assert.Contains("SET isactive = false FROM public.customer AS c JOIN public.customer AS o ON o.customerid = c.customerid WHERE bsm_target.ctid = c.ctid", result.Script);
        Assert.Contains("GET DIAGNOSTICS v_rowcount = ROW_COUNT;", result.Script);
        Assert.Contains("p_count := v_rowcount;", result.Script);
        Assert.Contains("EXCEPTION WHEN OTHERS THEN", result.Script);
        Assert.Contains("ORDER BY created DESC LIMIT 5", result.Script);
    }

    [Fact]
    public void Trigger_uses_transition_tables()
    {
        var code = new PortableCode(PortableCodeKind.Trigger, new PortableName("dbo", "trCustomerAudit"), """
            CREATE TRIGGER dbo.trCustomerAudit ON dbo.Customer AFTER INSERT, UPDATE AS
            BEGIN
                IF @@ROWCOUNT = 0 RETURN;
                INSERT INTO dbo.Audit (CustomerId, WasActive, IsActive)
                SELECT i.CustomerId, d.IsActive, i.IsActive FROM inserted i LEFT JOIN deleted d ON d.CustomerId = i.CustomerId;
            END
            """) { Table = new PortableName("dbo", "Customer") };
        var result = new TSqlToPgTranslator(Schemas.ToPg()).Code(code, [code]);
        output.WriteLine(result.Script);
        Assert.Contains("CREATE TRIGGER trcustomeraudit_ins AFTER INSERT ON public.customer", result.Script);
        Assert.Contains("REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted", result.Script);
        Assert.Contains("LEFT JOIN (SELECT * FROM public.customer WHERE false) AS d", result.Script);
        Assert.Contains("v_rowcount := (SELECT count(*) FROM inserted);", result.Script);
    }
}
