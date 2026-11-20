BEGIN TRANSACTION;
ALTER TABLE "Posts" ADD "Title" TEXT NOT NULL DEFAULT '';

CREATE TABLE "ef_temp_Blogs" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Blogs" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL,
    "Url" TEXT NULL
);

INSERT INTO "ef_temp_Blogs" ("Id", "Name", "Url")
SELECT "Id", "Name", "Url"
FROM "Blogs";

COMMIT;

PRAGMA foreign_keys = 0;

BEGIN TRANSACTION;
DROP TABLE "Blogs";

ALTER TABLE "ef_temp_Blogs" RENAME TO "Blogs";

COMMIT;

PRAGMA foreign_keys = 1;

BEGIN TRANSACTION;
CREATE INDEX "IX_Blogs_Name" ON "Blogs" ("Name");

COMMIT;

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261120131411_RatingDropTitle';

BEGIN TRANSACTION;
DROP INDEX "IX_Blogs_Name";

CREATE TABLE "ef_temp_Blogs" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_Blogs" PRIMARY KEY AUTOINCREMENT,
    "Name" TEXT NOT NULL
);

INSERT INTO "ef_temp_Blogs" ("Id", "Name")
SELECT "Id", "Name"
FROM "Blogs";

COMMIT;

PRAGMA foreign_keys = 0;

BEGIN TRANSACTION;
DROP TABLE "Blogs";

ALTER TABLE "ef_temp_Blogs" RENAME TO "Blogs";

COMMIT;

PRAGMA foreign_keys = 1;

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261120131407_AddBlogUrl';

BEGIN TRANSACTION;
DROP TABLE "Posts";

DROP TABLE "Blogs";

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261120131402_Initial';

COMMIT;

