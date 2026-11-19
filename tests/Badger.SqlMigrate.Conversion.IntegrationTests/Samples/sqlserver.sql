CREATE SCHEMA sales;
GO
CREATE TYPE dbo.Email FROM nvarchar(320) NULL;
GO
CREATE SEQUENCE sales.InvoiceNumbers AS bigint START WITH 5000 INCREMENT BY 1;
GO
CREATE TABLE sales.Customer (
    CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    FirstName nvarchar(50) NOT NULL,
    LastName nvarchar(50) NOT NULL,
    Email dbo.Email NULL CONSTRAINT UQ_Customer_Email UNIQUE,
    IsActive bit NOT NULL CONSTRAINT DF_Customer_IsActive DEFAULT (1),
    Created datetime2(3) NOT NULL CONSTRAINT DF_Customer_Created DEFAULT (sysdatetime()),
    Balance money NULL CONSTRAINT CK_Customer_Balance CHECK (Balance >= 0),
    Notes nvarchar(max) NULL
);
CREATE TABLE sales.[Order] (
    OrderId bigint IDENTITY(100,1) NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
    CustomerId int NOT NULL CONSTRAINT FK_Order_Customer REFERENCES sales.Customer (CustomerId) ON DELETE CASCADE,
    InvoiceNumber bigint NOT NULL CONSTRAINT DF_Order_Invoice DEFAULT (NEXT VALUE FOR sales.InvoiceNumbers),
    Status tinyint NOT NULL CONSTRAINT DF_Order_Status DEFAULT (0),
    OrderedAt datetimeoffset(3) NOT NULL CONSTRAINT DF_Order_OrderedAt DEFAULT (sysdatetimeoffset()),
    Total decimal(12,2) NOT NULL CONSTRAINT DF_Order_Total DEFAULT (0),
    TotalWithTax AS (Total * 1.2) PERSISTED,
    Ref uniqueidentifier NOT NULL CONSTRAINT DF_Order_Ref DEFAULT (newid()),
    Paid bit NOT NULL CONSTRAINT DF_Order_Paid DEFAULT (0)
);
CREATE TABLE sales.OrderLine (
    OrderId bigint NOT NULL CONSTRAINT FK_OrderLine_Order REFERENCES sales.[Order] (OrderId) ON DELETE CASCADE,
    [LineNo] int NOT NULL,
    Product nvarchar(100) NOT NULL,
    Qty int NOT NULL CONSTRAINT CK_OrderLine_Qty CHECK (Qty > 0),
    Price decimal(10,2) NOT NULL,
    CONSTRAINT PK_OrderLine PRIMARY KEY (OrderId, [LineNo])
);
CREATE TABLE sales.OrderAudit (
    AuditId int IDENTITY NOT NULL PRIMARY KEY,
    OrderId bigint NOT NULL,
    OldTotal decimal(12,2) NULL,
    NewTotal decimal(12,2) NULL,
    ChangedAt datetime2 NOT NULL DEFAULT (sysutcdatetime())
);
CREATE INDEX IX_Order_Customer ON sales.[Order] (CustomerId) INCLUDE (Total) WHERE Paid = 0;
CREATE INDEX IX_Customer_Name ON sales.Customer (LastName, FirstName DESC);
EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = N'People who buy things', @level0type = N'SCHEMA', @level0name = N'sales', @level1type = N'TABLE', @level1name = N'Customer';
GO
CREATE FUNCTION sales.FullName (@First nvarchar(50), @Last nvarchar(50)) RETURNS nvarchar(101)
AS BEGIN
    RETURN LTRIM(RTRIM(ISNULL(@First, N'') + N' ' + ISNULL(@Last, N'')));
END
GO
CREATE VIEW sales.ActiveCustomer AS
SELECT c.CustomerId, sales.FullName(c.FirstName, c.LastName) AS Name, c.Email, DATEDIFF(day, c.Created, GETDATE()) AS DaysSince
FROM sales.Customer AS c WHERE c.IsActive = 1;
GO
CREATE VIEW sales.CustomerTotals AS
SELECT c.CustomerId, COUNT(o.OrderId) AS Orders, ISNULL(SUM(o.Total), 0) AS Spent
FROM sales.Customer c LEFT JOIN sales.[Order] o ON o.CustomerId = c.CustomerId
GROUP BY c.CustomerId;
GO
CREATE FUNCTION sales.OrdersFor (@CustomerId int) RETURNS TABLE
AS RETURN (SELECT o.OrderId, o.Total, o.OrderedAt FROM sales.[Order] o WHERE o.CustomerId = @CustomerId);
GO
CREATE FUNCTION sales.BigOrders (@Min decimal(12,2)) RETURNS @r TABLE (OrderId bigint, Total decimal(12,2))
AS BEGIN
    INSERT INTO @r (OrderId, Total) SELECT OrderId, Total FROM sales.[Order] WHERE Total >= @Min;
    RETURN;
END
GO
CREATE PROCEDURE sales.PlaceOrder @CustomerId int, @Product nvarchar(100), @Qty int, @Price decimal(10,2), @OrderId bigint OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS (SELECT 1 FROM sales.Customer WHERE CustomerId = @CustomerId AND IsActive = 1)
        THROW 50001, N'Unknown or inactive customer', 1;
    BEGIN TRY
        INSERT INTO sales.[Order] (CustomerId, Total) VALUES (@CustomerId, @Qty * @Price);
        SET @OrderId = SCOPE_IDENTITY();
        INSERT INTO sales.OrderLine (OrderId, [LineNo], Product, Qty, Price) VALUES (@OrderId, 1, @Product, @Qty, @Price);
    END TRY
    BEGIN CATCH
        THROW;
    END CATCH
END
GO
CREATE TRIGGER sales.trOrderTotal ON sales.[Order] AFTER UPDATE AS
BEGIN
    IF @@ROWCOUNT = 0 RETURN;
    INSERT INTO sales.OrderAudit (OrderId, OldTotal, NewTotal)
    SELECT i.OrderId, d.Total, i.Total FROM inserted i JOIN deleted d ON d.OrderId = i.OrderId WHERE i.Total <> d.Total;
END
GO
SET IDENTITY_INSERT sales.Customer ON;
INSERT INTO sales.Customer (CustomerId, FirstName, LastName, Email, IsActive, Balance, Notes) VALUES
 (1, N'Ada', N'Lovelace', N'ada@example.com', 1, 10.50, N'tab	and
newline \ backslash'),
 (2, N'Bob', N'Smith', NULL, 0, NULL, NULL),
 (3, N'Chloé', N'Dupont', N'chloe@example.com', 1, 0, N'unicode ✓');
SET IDENTITY_INSERT sales.Customer OFF;
DECLARE @id bigint;
EXEC sales.PlaceOrder 1, N'Widget', 3, 9.99, @id OUTPUT;
EXEC sales.PlaceOrder 3, N'Gadget', 1, 120.00, @id OUTPUT;
UPDATE sales.[Order] SET Total = Total + 1 WHERE OrderId = @id;
GO
