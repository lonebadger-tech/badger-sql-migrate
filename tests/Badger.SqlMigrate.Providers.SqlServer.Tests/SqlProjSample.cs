namespace Badger.SqlMigrate.Providers.SqlServer.Tests;

/// <summary>
/// A small SSDT project as Visual Studio writes it: backslash paths (one in the wrong case), CRLF line endings,
/// a BOM, foreign keys in a cycle, a view on a view, a function, a procedure with its GRANT, a trigger in a table's
/// file, standalone permissions, a pre-deployment script and a post-deployment seed with an :r include.
/// Also linked into the CLI tests.
/// </summary>
public static class SqlProjSample
{
    public static readonly Dictionary<string, string> Files = new()
    {
        ["Security/Sales.sql"] = "CREATE SCHEMA [Sales]\r\n    AUTHORIZATION [dbo];\r\n",
        ["Security/AppReader.sql"] = "CREATE ROLE [AppReader];\r\n",
        ["Types/Phone.sql"] = "CREATE TYPE [dbo].[Phone] FROM varchar(20) NULL;\r\n",
        ["Sequences/OrderNumber.sql"] = "CREATE SEQUENCE [Sales].[OrderNumber] AS int START WITH 1000 INCREMENT BY 1;\r\n",
        ["Tables/Country.sql"] = """
            CREATE TABLE [dbo].[Country] (
                [Code] char(2) NOT NULL CONSTRAINT [PK_Country] PRIMARY KEY,
                [Name] nvarchar(100) NOT NULL
            );
            """,
        // Customer and Order reference each other: the import moves the foreign keys into later changes.
        ["Tables/Customer.sql"] = """
            -- Customers. LastOrderId points at the newest order.
            CREATE TABLE [dbo].[Customer] (
                [Id] int IDENTITY (1, 1) NOT NULL,
                [Name] nvarchar(100) NOT NULL,
                [Phone] [dbo].[Phone] NULL,
                [CountryCode] char(2) NOT NULL CONSTRAINT [FK_Customer_Country] REFERENCES [dbo].[Country] ([Code]),
                [LastOrderId] int NULL,
                CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Id] ASC),
                CONSTRAINT [FK_Customer_LastOrder] FOREIGN KEY ([LastOrderId]) REFERENCES [Sales].[Order] ([Id])
            );
            GO
            CREATE NONCLUSTERED INDEX [IX_Customer_Name] ON [dbo].[Customer] ([Name]);
            """,
        ["Tables/Order.sql"] = """
            CREATE TABLE [Sales].[Order] (
                [Id] int NOT NULL CONSTRAINT [PK_Order] PRIMARY KEY,
                [Number] int NOT NULL CONSTRAINT [DF_Order_Number] DEFAULT (NEXT VALUE FOR [Sales].[OrderNumber]),
                [CustomerId] int NOT NULL CONSTRAINT [FK_Order_Customer] FOREIGN KEY REFERENCES [dbo].[Customer] ([Id]),
                [Placed] datetime2 NOT NULL CONSTRAINT [DF_Order_Placed] DEFAULT (sysutcdatetime())
            );
            GO
            CREATE TRIGGER [Sales].[trOrder_Insert] ON [Sales].[Order] AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                UPDATE c SET LastOrderId = i.Id FROM dbo.Customer c JOIN inserted i ON i.CustomerId = c.Id;
            END
            """,
        ["Tables/OrderLine.sql"] = """
            CREATE TABLE [Sales].[OrderLine] (
                [OrderId] int NOT NULL,
                [Line] int NOT NULL,
                [Amount] decimal(19, 4) NOT NULL,
                CONSTRAINT [PK_OrderLine] PRIMARY KEY ([OrderId], [Line]),
                CONSTRAINT [FK_OrderLine_Order] FOREIGN KEY ([OrderId]) REFERENCES [Sales].[Order] ([Id]) ON DELETE CASCADE,
                CONSTRAINT [CK_OrderLine_Amount] CHECK ([Amount] >= 0)
            );
            """,
        ["Functions/fnOrderTotal.sql"] = """
            CREATE FUNCTION [Sales].[fnOrderTotal] (@orderId int)
            RETURNS decimal(19, 4)
            AS
            BEGIN
                RETURN (SELECT SUM(Amount) FROM Sales.OrderLine WHERE OrderId = @orderId);
            END
            """,
        ["Views/vOrderTotals.sql"] = """
            CREATE VIEW [Sales].[vOrderTotals]
            AS
            SELECT o.Id, o.CustomerId, Sales.fnOrderTotal(o.Id) AS Total
            FROM Sales.[Order] o;
            """,
        // A view on a view.
        ["Views/vBigCustomers.sql"] = """
            CREATE VIEW [dbo].[vBigCustomers]
            AS
            SELECT c.Id, c.Name, SUM(t.Total) AS Total
            FROM dbo.Customer c
            JOIN Sales.vOrderTotals t ON t.CustomerId = c.Id
            GROUP BY c.Id, c.Name
            HAVING SUM(t.Total) > 1000;
            """,
        ["StoredProcedures/usp_GetBigCustomers.sql"] = """
            CREATE PROCEDURE [dbo].[usp_GetBigCustomers]
            AS
            SET NOCOUNT ON;
            SELECT Id, Name, Total FROM dbo.vBigCustomers ORDER BY Total DESC;
            GO
            GRANT EXECUTE ON [dbo].[usp_GetBigCustomers] TO [AppReader];
            """,
        ["Security/Permissions.sql"] = "GRANT SELECT ON [Sales].[vOrderTotals] TO [AppReader];\r\nGRANT SELECT ON [dbo].[Country] TO [AppReader];\r\n",
        ["Scripts/Script.PreDeployment.sql"] = "PRINT 'before';\r\n",
        ["Scripts/Script.PostDeployment.sql"] = """
            /*
             Post-Deployment Script
            */
            :r .\Seed\Countries.sql
            PRINT 'Seeded $(Environment)';
            """,
        ["Scripts/Seed/Countries.sql"] = """
            MERGE dbo.Country AS t
            USING (VALUES ('US', N'United States'), ('CA', N'Canada')) AS s (Code, Name) ON t.Code = s.Code
            WHEN MATCHED THEN UPDATE SET Name = s.Name
            WHEN NOT MATCHED THEN INSERT (Code, Name) VALUES (s.Code, s.Name);
            """,
    };

    /// <summary>Build items as Visual Studio writes them (backslashes; one with the wrong case).</summary>
    private static readonly string[] BuildItems =
    [
        @"Security\Sales.sql", @"Security\AppReader.sql", @"Types\Phone.sql", @"Sequences\OrderNumber.sql", @"tables\country.sql",
        @"Tables\Customer.sql", @"Tables\Order.sql", @"Tables\OrderLine.sql", @"Functions\fnOrderTotal.sql", @"Views\vOrderTotals.sql",
        @"Views\vBigCustomers.sql", @"StoredProcedures\usp_GetBigCustomers.sql", @"Security\Permissions.sql",
    ];

    /// <summary>Writes the classic (MSBuild) project; returns the .sqlproj path.</summary>
    public static string WriteClassic(string dir)
    {
        WriteScripts(dir);
        var items = string.Join("\r\n", BuildItems.Select(i => $"    <Build Include=\"{i}\" />"));
        var path = Path.Combine(dir, "Shop.sqlproj");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003" ToolsVersion="4.0">
              <PropertyGroup>
                <Name>Shop</Name>
                <DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP>
                <DefaultSchema>dbo</DefaultSchema>
              </PropertyGroup>
              <ItemGroup>
            {items}
              </ItemGroup>
              <ItemGroup>
                <PreDeploy Include="Scripts\Script.PreDeployment.sql" />
                <PostDeploy Include="Scripts\Script.PostDeployment.sql" />
                <None Include="Scripts\Seed\Countries.sql" />
              </ItemGroup>
              <ItemGroup>
                <SqlCmdVariable Include="Environment">
                  <DefaultValue>dev</DefaultValue>
                  <Value>$(SqlCmdVar__1)</Value>
                </SqlCmdVariable>
              </ItemGroup>
              <ItemGroup>
                <ArtifactReference Include="$(DacPacRootPath)\Extensions\Microsoft\SQLDB\Extensions\SqlServer\160\SqlSchemas\master.dacpac">
                  <HintPath>$(DacPacRootPath)\Extensions\Microsoft\SQLDB\Extensions\SqlServer\160\SqlSchemas\master.dacpac</HintPath>
                  <SuppressMissingDependenciesErrors>False</SuppressMissingDependenciesErrors>
                  <DatabaseVariableLiteralValue>master</DatabaseVariableLiteralValue>
                </ArtifactReference>
              </ItemGroup>
            </Project>
            """.Replace("\n", "\r\n", StringComparison.Ordinal));
        return path;
    }

    /// <summary>Writes the SDK-style (Microsoft.Build.Sql) project: every .sql builds except None/Remove and deploy scripts.</summary>
    public static string WriteSdkStyle(string dir)
    {
        WriteScripts(dir);
        File.WriteAllText(Path.Combine(dir, "Scratch.sql"), "SELECT 1;");
        Directory.CreateDirectory(Path.Combine(dir, "bin", "Debug"));
        File.WriteAllText(Path.Combine(dir, "bin", "Debug", "Generated.sql"), "CREATE TABLE dbo.Junk (Id int);");
        var path = Path.Combine(dir, "Shop.sqlproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.Build.Sql/1.0.0">
              <PropertyGroup>
                <Name>Shop</Name>
                <DSP>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</DSP>
              </PropertyGroup>
              <ItemGroup>
                <Build Remove="Scratch.sql" />
                <PreDeploy Include="Scripts\Script.PreDeployment.sql" />
                <PostDeploy Include="Scripts\Script.PostDeployment.sql" />
                <SqlCmdVariable Include="Environment">
                  <DefaultValue>dev</DefaultValue>
                </SqlCmdVariable>
                <PackageReference Include="Microsoft.SqlServer.Dacpacs.Master" Version="160.2.2" DacpacName="master" DatabaseVariableLiteralValue="master" />
              </ItemGroup>
            </Project>
            """);
        return path;
    }

    private static void WriteScripts(string dir)
    {
        foreach (var (name, text) in Files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Visual Studio writes CRLF and a UTF-8 BOM.
            File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), new System.Text.UTF8Encoding(true));
        }
    }

    /// <summary>
    /// What publishing the dacpac does to an empty database, as SQL: SSDT creates the tables, then adds the foreign
    /// keys that form a cycle, then the modules, then runs the post-deployment script.
    /// </summary>
    public static string PublishScript()
    {
        string F(string name) => Files[name].Replace("\r\n", "\n", StringComparison.Ordinal);
        var customer = F("Tables/Customer.sql").Replace(",\n    CONSTRAINT [FK_Customer_LastOrder] FOREIGN KEY ([LastOrderId]) REFERENCES [Sales].[Order] ([Id])", string.Empty, StringComparison.Ordinal);
        return string.Join("\nGO\n",
            F("Security/Sales.sql"), F("Security/AppReader.sql"), F("Types/Phone.sql"), F("Sequences/OrderNumber.sql"), F("Tables/Country.sql"),
            customer, F("Tables/Order.sql"), F("Tables/OrderLine.sql"),
            "ALTER TABLE [dbo].[Customer] ADD CONSTRAINT [FK_Customer_LastOrder] FOREIGN KEY ([LastOrderId]) REFERENCES [Sales].[Order] ([Id]);",
            F("Functions/fnOrderTotal.sql"), F("Views/vOrderTotals.sql"), F("Views/vBigCustomers.sql"), F("StoredProcedures/usp_GetBigCustomers.sql"),
            F("Security/Permissions.sql"), F("Scripts/Seed/Countries.sql"));
    }
}
