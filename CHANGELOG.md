# Changelog

All notable changes to Badger Trek. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). The website's [version history](https://lonebadger.tech/badger-trek/releases/) has the full release notes.

## Unreleased

### Added

- **MySQL**: `trek init --provider mysql`, for MySQL 8.4 and 9.7, the LTS releases that are still supported (each tested on every build). Deploy, revert, verify, repeatables, bundles, the schema fingerprint and `diff-schema` work as on the other databases. MySQL commits DDL as it runs, so changes run without a transaction: a change that fails part-way keeps what already ran, and is recorded only when all of it has run. Scripts are split as the `mysql` client does, with `DELIMITER` around stored program bodies. The journal is `trek_*` tables in the environment's database, and the deploy lock is a named lock. `trek doctor` flags older MySQL releases, Innovation releases and MariaDB. Lint: `USE` (TREK009), mysql client commands and a stored program body without a delimiter (TREK005). Imports from other tools come later.
- MySQL: `trek add --from-diff` and `diff-schema --script` write MySQL changes. Tables change with `ALTER TABLE` (columns added in position, dropped, modified, moved and renamed; indexes, checks, options; a new character set with `CONVERT TO`), foreign keys are added after every table exists, and views, routines, triggers and events are created again without `DEFINER` and with a `DELIMITER` where needed. The shadow database is created on the same server.
- MySQL: `trek convert schema` and `trek convert data` move a MySQL database to SQL Server, PostgreSQL or SQLite, and any of them to MySQL. Types are mapped (`TINYINT(1)` and booleans, `ENUM` and `SET`, unsigned integers, `JSON`, `AUTO_INCREMENT` and identities), tables are InnoDB with `utf8mb4` and a case-sensitive or case-insensitive collation to match the source, and views are translated (MySQL and SQL Server or SQLite go by way of PostgreSQL's syntax). Functions, procedures and triggers are written commented out for now, with a REVIEW marker. Rows load into MySQL with multi-row inserts in one transaction, foreign keys checked before the commit.

### Changed

- `trek convert schema` always needs `--to`. It no longer picks a target when converting between SQL Server and PostgreSQL: every provider converts to every other, and none is the default.

### Fixed

- `trek convert schema` no longer creates the target's default schema (`dbo`, `public`) when a source schema maps to it, and no longer drops it on revert.
- A converted view that calls a routine which could not be translated is commented out with it, so the project deploys; before, creating the view failed.

## 0.5.0 (November 2026)

### Changed

- **Supported database versions follow their vendors' support.** trek supports, and CI tests, only versions that are still supported:
  - **SQL Server 2019 or later** (was 2016 SP1). SQL Server 2016 and 2017 are past or near the end of Microsoft's support, and lack what trek and `trek convert` now rely on (`STRING_AGG`, temporal history retention, secure enclaves). Azure SQL is unaffected. CI tests 2019, 2022 and 2025.
  - **PostgreSQL 15 or later** (was 12). The PostgreSQL project supports each major version for five years; 12, 13 and 14 have reached their end of life. CI tests 15 to 18.
  - **SQLite 3.26 or later**, unchanged: it is what RHEL 8 ships, which is supported until 2029.
  - `trek doctor` flags a SQL Server or PostgreSQL server older than that.

### Added

- Entity Framework Core: `trek import ef` turns EF Core migrations into a trek project, one change per migration, with each migration's `Up()` as `deploy.sql` and `Down()` as `revert.sql`. Point it at the project holding the migrations and trek runs `dotnet ef migrations script` for both directions, or give it the scripts (`up.sql --down down.sql`). The provider comes from the SQL (SQL Server, PostgreSQL or SQLite). EF's transactions and `__EFMigrationsHistory` rows are left out, and SQLite table rebuilds are `!notx`. `trek baseline --from ef` adopts databases EF already migrated by reading their `__EFMigrationsHistory`. Tested with EF Core 10.

### Fixed

- `trek convert schema --to sqlite` writes SQL that SQLite 3.26 runs, the default `sqlite.minVersion`, so the converted project no longer fails its own lint (TREK008). PostgreSQL's `->`/`->>`/`#>>` become `json_extract` (a chain becomes one path), `NULLS FIRST`/`LAST` an `IS NULL` sort key, an aggregate's `FILTER (WHERE ...)` a `CASE` inside it, `RIGHT JOIN` a `LEFT JOIN` with the sides swapped, and `UPDATE ... FROM` in a trigger correlated subqueries. `RETURNING` in a trigger is left out, since SQLite does not allow it there, and an `UPDATE` or `DELETE` in a trigger no longer gives its target table an alias, which SQLite rejects in triggers (the table name is used instead). Generated columns, `FULL JOIN` and `ORDER BY` inside an aggregate, which have no older form, get a REVIEW marker naming the SQLite version they need.
- `trek convert data` into SQLite with a relative `database:` finds the file the converted project was deployed to, whether `trek deploy` ran in the target folder or with `-C` from the source folder. It stops when both candidate files exist, or neither, instead of loading into or creating the wrong file.

## 0.4.1 (November 2026)

### Fixed

- A database deployed by Badger SQL Migrate (0.3.0 and earlier) no longer looks empty: 0.4.0 found no journal in state schema `trek`, showed every change as pending, and `trek deploy` would have run them all again. Every command that reads or creates the journal now stops when only a Badger SQL Migrate journal (state schema `bsm`, `bsm_*` tables on SQLite) is there, and prints the provider's SQL to move it to `trek` once, keeping the history, or suggests `stateSchema: bsm`. trek still reads nothing from the old journal itself.
- With `bsm.yaml` and no `trek.yaml`, trek explains the rename instead of suggesting `trek init`.
- The nuget.org package page and `trek ai-guide` explain the rename. The new [Upgrading from Badger SQL Migrate](https://lonebadger.tech/badger-trek/docs/upgrading/) page replaces the 0.4.0 advice to adopt each database with `trek baseline`.

## 0.4.0 (November 2026)

**Breaking: renamed from Badger SQL Migrate to Badger Trek, with no backwards compatibility.** The command is `trek`, the NuGet package `Badger.Trek` (the `Badger.SqlMigrate` package is deprecated), and the repository `lonebadger-tech/badger-trek`. Everything the tool names follows: project files `trek.yaml` and `trek.plan`, the `trek` journal schema (`trek_*` tables on SQLite), `-- trek:` script directives, `{trek.*}` variables, `TREK_*` environment variables, `~/.trek`, and lint codes `TREK001` and up. trek does not read anything from 0.3.0 and earlier (`bsm.yaml`, the `bsm` journal, `BSM_*` variables): pointed at a database deployed by an earlier version, it finds no journal. To move over, uninstall `Badger.SqlMigrate`, install `Badger.Trek`, rename the project files and the names inside the scripts, and adopt each existing database with `trek baseline`.

### Added

- SQL projects (SSDT): `trek import sqlproj` converts classic and SDK-style `.sqlproj` projects into changes in dependency order (with generated reverts, repeatables for views and routines, the post-deployment script and SqlCmdVariables), and `trek baseline --from sqlproj` adopts databases published from the dacpac. Every `trek baseline` on SQL Server records the repeatables whose live definition already matches.
- AI assistants: `trek mcp` (a Model Context Protocol server that edits project files and never connects to a database), `trek ai-guide`, and an `AGENTS.md` written by `trek init` and `trek import` (`--no-agents-file` to skip). `--json` output for `add`, `rework`, `tag`, `init`, `render` and `plan check`.
- SQLite provider (`trek init --provider sqlite`): database files with the engine built in, nothing to install. Statement splitting as the `sqlite3` shell does it (trigger bodies included), lint for dot-commands and statements that cannot run in a transaction, journal tables prefixed `trek_`, a file lock held for the whole deploy, a schema fingerprint, and `add --from-diff` / `diff-schema --script` that use `ALTER TABLE` where SQLite can and the documented table rebuild where it cannot (with foreign keys switched off when other tables refer to the rebuilt one). Imports take `--provider sqlite`; Sqitch SQLite projects and Flyway `jdbc:sqlite:` URLs map to it.
- `trek convert schema` and `trek convert data`: move a live database between SQL Server, PostgreSQL and SQLite, in any direction (`--to` picks the target). The schema becomes a new trek project for the other provider (types mapped, names lowercased or snake_cased for PostgreSQL, views, functions, procedures and triggers translated, every uncertain spot marked `REVIEW trek convert` and listed in `CONVERSION.md`); the rows are then copied (bulk copy, `COPY`, or prepared inserts for SQLite) and row counts compared. To SQLite, schemas become table-name prefixes, routines are kept commented out (single-expression functions are inlined into views), and trigger logic is flattened into SQLite's statement-only triggers.
- `trek add --author`; the default author is `TREK_AUTHOR`, then git's `user.name`, then the OS user name.
- `env show` and `vars` show unset `${env:...}` references as written instead of failing.
- A missing SQL Server database now gives "Could not open database 'x': it does not exist ... create it first" instead of the login error, and connection descriptions show a port only when one is configured.
- `vars` and `render` no longer need the environment's password or other sign-in secrets.
- Documented checking whether a database is at the build's plan head, and fast test databases from a template.

### Changed

- **Behaviour change for SQLite projects.** SQLite 3.26 and later: `sqlite.minVersion` in `trek.yaml` (default `"3.26"`) is the oldest SQLite that opens the project's databases, often the operating system's (3.26 on RHEL 8, 3.34 on RHEL 9). `add --from-diff` and `diff-schema --script` use only SQL it understands, so below 3.35 a dropped column is a table rebuild instead of `ALTER TABLE ... DROP COLUMN` (raise `minVersion` to get it back). Lint warns (TREK008) about SQL newer than `minVersion`, such as `RETURNING`, `STRICT` tables, generated columns and `->>`. CI tests the SQLite provider against the SQLite of AlmaLinux 8 and 9, Ubuntu 22.04 and 24.04 and Debian 12.

### Fixed

- Fixed: SQLite projects failed on Linux systems older than glibc 2.33 (RHEL 8, Ubuntu 20.04, Debian 11) because the built-in SQLite could not load. trek now uses the system's libsqlite3 there (3.26 or later), `TREK_SQLITE_LIBRARY` picks a library explicitly, and `trek doctor` shows which engine is in use.
- Fixed: reading the schema of SQLite databases (for `diff-schema`, `add --from-diff`, `convert`) used `sqlite_schema` and `pragma_table_list`, which need SQLite 3.33 and 3.37.
- Fixed: deploy, verify and `add --from-diff` failed on SQL Server 2016 ("Invalid column name 'history_retention_period'"); temporal tables now fingerprint the same on 2016 and later.
- Fixed more SQL Server 2016 failures, found running the tests and an end-to-end project on SQL Server 2016 and 2025 LocalDB: scripts from `add --from-diff` that drop an unnamed unique key or foreign key used `STRING_AGG` (SQL Server 2017+); deploying with Always Encrypted keys failed with "Invalid column name 'allow_enclave_computations'"; and `trek convert data` failed with "Invalid column name 'graph_type'". A column master key with `enclaveComputations` on a server without secure enclaves (before SQL Server 2019) is now a clear resource conflict instead of a syntax error.
- Fixed: if recording the schema fingerprint fails after a deploy, the deploy now reports a warning (the changes are applied) instead of an unhandled exception, and the next `trek deploy` records the fingerprint even when nothing is pending. Unexpected database errors print one line instead of a stack trace.
- Fixed: `trek verify` said the schema "matches last deploy" when no fingerprint had been recorded; it now says none is recorded and drift was not checked.
- Fixed: `revert -y` printed "would revert" for each change before reverting.
- Fixed: on Windows, the MCP server's `add_change` and `rework_change` hung: looking up git's `user.name` for the plan's author left git waiting on the server's stdin. The lookup now gives git no input and gives up after 3 seconds.
- Fixed: on PostgreSQL 14.25, 15.20, 16.16, 17.12, 18.7 and later (the November 2026 releases for CVE-2026-6460), every table showed as drifted and `add --from-diff` scripted `~ column ...: options` changes. These releases store an internal `pg_attver` entry in each column's options, different in every database; trek now leaves out internal `pg_` options. Fingerprints recorded on earlier releases are unchanged.
- Fixed: `--connection-string` now connects exactly as the string says. The environment's `tls` settings no longer override its `Encrypt` and `TrustServerCertificate`, joining host, database and sign-in (an environment with `authentication: integrated` no longer turns a SQL login string into Windows sign-in).
- Fixed: `trek doctor` failed with "VIEW SERVER STATE permission was denied" for logins without that permission; it now reports the connection's encryption as unknown and the rest as usual.

## 0.3.0 (November 2026)

- A PostgreSQL provider, tested on PostgreSQL 12 through 18 (`init --provider postgresql`), with everything SQL Server has: certified plans, revert from stored scripts, environments, variables, bundles, `diff-schema`, `add --from-diff`, rework, repair and imports. psql scripts (`\i`, `\copy`, `\set`, `\if`, `COPY ... FROM STDIN`, `\gexec`) run without psql; `baseline --from sqitch` adopts databases Sqitch deployed.

## 0.2.0 (November 2026)

- `rework`, `repair` and `revert --repeatable` for fixing deployed changes.
- `bundle create` and `--bundle`: build once, deploy everywhere, with every hash checked.
- `diff-schema`, and `add --from-diff` to turn schema edits into a change with deploy and revert scripts.
- Imports from Flyway, DbUp, grate/RoundhousE and Sqitch, and `baseline --from` to adopt their databases.
- System-versioned tables and partitions in the schema fingerprint; lint rule 004.

## 0.1.0 (November 2026)

The first release.

- Named changes in `trek.plan`, merged with git's union driver.
- Deploy, revert and verify scripts, stored in the database at deploy time.
- SHA-256 hash certification with a chain id over the whole plan.
- SQL Server and Azure SQL provider: every Entra ID flow, SSH tunnels, Always Encrypted keys per environment.
- Environments in `trek.yaml`, variables, repeatables, baselines and schema drift detection.
- Open source under the MIT license, with contribution, security and conduct guides.
- The .NET tool is published to nuget.org from GitHub Actions with Trusted Publishing.
- Fixed: on Windows, Always Encrypted connections failed with "'MSSQL_' prefix is reserved" because the system key stores were registered on the connection.
