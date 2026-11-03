-- Local-only sample data. Runs after shared repeatables, only in the 'local' environment.
MERGE dbo.Customer AS t
USING (VALUES (N'Ada Lovelace', N'ada@example.com'), (N'Grace Hopper', N'grace@example.com')) AS s (Name, Email)
    ON t.Email = s.Email
WHEN NOT MATCHED THEN INSERT (Name, Email) VALUES (s.Name, s.Email);
