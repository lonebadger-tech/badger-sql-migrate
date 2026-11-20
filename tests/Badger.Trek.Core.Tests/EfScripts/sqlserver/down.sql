BEGIN TRANSACTION;
DECLARE @var nvarchar(max);
SELECT @var = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Blogs]') AND [c].[name] = N'Rating');
IF @var IS NOT NULL EXEC(N'ALTER TABLE [Blogs] DROP CONSTRAINT ' + @var + ';');
ALTER TABLE [Blogs] DROP COLUMN [Rating];

ALTER TABLE [Posts] ADD [Title] nvarchar(max) NOT NULL DEFAULT N'';

DELETE FROM [__EFMigrationsHistory]
WHERE [MigrationId] = N'20261120131344_RatingDropTitle';

COMMIT;
GO

BEGIN TRANSACTION;
DROP INDEX [IX_Blogs_Name] ON [Blogs];

DECLARE @var1 nvarchar(max);
SELECT @var1 = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Blogs]') AND [c].[name] = N'Url');
IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [Blogs] DROP CONSTRAINT ' + @var1 + ';');
ALTER TABLE [Blogs] DROP COLUMN [Url];

DECLARE @var2 nvarchar(max);
SELECT @var2 = QUOTENAME([d].[name])
FROM [sys].[default_constraints] [d]
INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Blogs]') AND [c].[name] = N'Name');
IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [Blogs] DROP CONSTRAINT ' + @var2 + ';');
ALTER TABLE [Blogs] ALTER COLUMN [Name] nvarchar(max) NOT NULL;

DELETE FROM [__EFMigrationsHistory]
WHERE [MigrationId] = N'20261120131340_AddBlogUrl';

COMMIT;
GO

BEGIN TRANSACTION;
DROP TABLE [Posts];

DROP TABLE [Blogs];

DELETE FROM [__EFMigrationsHistory]
WHERE [MigrationId] = N'20261120131330_Initial';

COMMIT;
GO

