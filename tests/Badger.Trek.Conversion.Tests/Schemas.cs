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

    /// <summary>A MySQL database (schema "shop"), column types as information_schema's COLUMN_TYPE prints them.</summary>
    public static PortableSchema MySql()
    {
        var s = new PortableSchema { SourceProvider = "mysql", DefaultSchema = "shop", CaseInsensitive = true, Collation = "utf8mb4_0900_ai_ci" };
        s.Tables.Add(new PortableTable(new PortableName("shop", "orders"),
            [
                new PortableColumn("id", "int", false) { Identity = new PortableIdentity(1, 1, false) },
                new PortableColumn("customer_name", "varchar(200)", false),
                new PortableColumn("status", "enum('new','paid','shipped')", false) { Default = "'new'" },
                new PortableColumn("paid", "tinyint(1)", false) { Default = "0" },
                new PortableColumn("qty", "int", false) { Default = "1" },
                new PortableColumn("price", "decimal(12,2)", false),
                new PortableColumn("total", "decimal(12,2)", false) { Computed = "(`price` * `qty`)", ComputedStored = true },
                new PortableColumn("created", "datetime(6)", false) { Default = "CURRENT_TIMESTAMP(6)" },
                new PortableColumn("updated", "datetime", true),
                new PortableColumn("ref", "char(36)", false) { Default = "(uuid())" },
                new PortableColumn("doc", "json", true),
            ],
            new PortableKey("PRIMARY", ["id"]), [], [new PortableCheck("orders_chk_1", "(`total` >= 0)")], [], []));
        s.Tables.Add(new PortableTable(new PortableName("shop", "customers"),
            [
                new PortableColumn("name", "varchar(100)", false),
                new PortableColumn("email", "varchar(200)", true),
            ],
            new PortableKey("PRIMARY", ["name"]), [], [], [], []));
        return s;
    }

    public static ConversionContext ToTSql() => new(Postgres(), new ConversionOptions { TargetProvider = "sqlserver" });

    public static ConversionContext ToSqlite() => new(Postgres(), new ConversionOptions { TargetProvider = "sqlite" });

    public static ConversionContext ToMySql() => new(Postgres(), new ConversionOptions { TargetProvider = "mysql" });

    public static ConversionContext MySqlToPg() => new(MySql(), new ConversionOptions { TargetProvider = "postgresql" });

    public static ConversionContext ToPg(NameStyle? names = null) =>
        new(SqlServer(), new ConversionOptions { TargetProvider = "postgresql", Names = names });
}
