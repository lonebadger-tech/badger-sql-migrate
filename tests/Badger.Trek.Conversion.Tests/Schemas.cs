using Badger.Trek.Conversion;
using Badger.Trek.Core.Conversion;

namespace Badger.Trek.Conversion.Tests;

/// <summary>Small source schemas for translator tests.</summary>
internal static class Schemas
{
    public static PortableSchema SqlServer()
    {
        var s = new PortableSchema { SourceProvider = "sqlserver", DefaultSchema = "dbo", CaseInsensitive = true, Collation = "SQL_Latin1_General_CP1_CI_AS" };
        s.Tables.Add(new PortableTable(new PortableName("dbo", "Customer"),
            [
                new PortableColumn("CustomerId", "int", false) { Identity = new PortableIdentity(1, 1, false) },
                new PortableColumn("FirstName", "nvarchar(50)", false),
                new PortableColumn("LastName", "nvarchar(50)", false),
                new PortableColumn("IsActive", "bit", false) { Default = "((1))" },
                new PortableColumn("Created", "datetime2(7)", false) { Default = "(sysdatetime())" },
                new PortableColumn("Balance", "money", true),
            ],
            new PortableKey("PK_Customer", ["CustomerId"]), [], [new PortableCheck("CK_Balance", "([Balance]>=(0))")], [], []));
        return s;
    }

    public static PortableSchema Postgres()
    {
        var s = new PortableSchema { SourceProvider = "postgresql", DefaultSchema = "public" };
        s.Tables.Add(new PortableTable(new PortableName("public", "orders"),
            [
                new PortableColumn("id", "bigint", false) { Identity = new PortableIdentity(1, 1, true) },
                new PortableColumn("customer_name", "text", false),
                new PortableColumn("status", "public.order_status", false) { Default = "'new'::public.order_status" },
                new PortableColumn("paid", "boolean", false) { Default = "false" },
                new PortableColumn("total", "numeric(12,2)", false),
                new PortableColumn("created", "timestamp with time zone", false) { Default = "now()" },
                new PortableColumn("updated", "timestamp with time zone", true),
                new PortableColumn("tags", "text[]", true),
            ],
            new PortableKey("orders_pkey", ["id"]), [], [new PortableCheck("orders_total_check", "(total >= (0)::numeric)")], [], []));
        s.Enums.Add(new PortableEnum(new PortableName("public", "order_status"), ["new", "paid", "shipped"]));
        return s;
    }

    public static ConversionContext ToTSql() => new(Postgres(), new ConversionOptions { TargetProvider = "sqlserver" });

    public static ConversionContext ToPg(NameStyle? names = null) =>
        new(SqlServer(), new ConversionOptions { TargetProvider = "postgresql", Names = names });
}
