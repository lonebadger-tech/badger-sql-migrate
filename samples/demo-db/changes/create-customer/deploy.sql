CREATE TABLE dbo.Customer (
    Id int IDENTITY NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    CreatedAt datetime2 NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT (sysutcdatetime())
);
GO
CREATE ROLE [{AppRole}];
