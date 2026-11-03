CREATE TABLE dbo.[Order] (
    Id int IDENTITY NOT NULL CONSTRAINT PK_Order PRIMARY KEY,
    CustomerId int NOT NULL CONSTRAINT FK_Order_Customer REFERENCES dbo.Customer (Id),
    Total decimal(19, 4) NOT NULL
);
GO
CREATE INDEX IX_Order_CustomerId ON dbo.[Order] (CustomerId);
