IF COL_LENGTH(N'dbo.Customer', N'Email') IS NULL THROW 50000, N'Customer.Email is missing', 1;
