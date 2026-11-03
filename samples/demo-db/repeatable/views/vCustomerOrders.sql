CREATE OR ALTER VIEW dbo.vCustomerOrders
AS
SELECT c.Id, c.Name, c.Email, COUNT(o.Id) AS Orders, SUM(o.Total) AS Total
FROM dbo.Customer c
LEFT JOIN dbo.[Order] o ON o.CustomerId = c.Id
GROUP BY c.Id, c.Name, c.Email;
GO
GRANT SELECT ON dbo.vCustomerOrders TO [{AppRole}];
