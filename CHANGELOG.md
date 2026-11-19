# Changelog

All notable changes to Badger SQL Migrate. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). The website's [version history](https://lonebadger.tech/badger-sql-migrate/releases/) has the full release notes.

## Unreleased

- SQLite 3.26 and later: `sqlite.minVersion` in `bsm.yaml` (default `"3.26"`) is the oldest SQLite that opens the project's databases, often the operating system's (3.26 on RHEL 8, 3.34 on RHEL 9). `add --from-diff` and `diff-schema --script` use only SQL it understands, so below 3.35 a dropped column is a table rebuild instead of `ALTER TABLE ... DROP COLUMN` (raise `minVersion` to get it back). Lint warns (BSM008) about SQL newer than `minVersion`, such as `RETURNING`, `STRICT` tables, generated columns and `->>`. CI tests the SQLite provider against the SQLite of AlmaLinux 8 and 9, Ubuntu 22.04 and 24.04 and Debian 12.
- Fixed: SQLite projects failed on Linux systems older than glibc 2.33 (RHEL 8, Ubuntu 20.04, Debian 11) because the built-in SQLite could not load. bsm now uses the system's libsqlite3 there (3.26 or later), `BSM_SQLITE_LIBRARY` picks a library explicitly, and `bsm doctor` shows which engine is in use.
- Fixed: reading the schema of SQLite databases (for `diff-schema`, `add --from-diff`, `convert`) used `sqlite_schema` and `pragma_table_list`, which need SQLite 3.33 and 3.37.
- Fixed: deploy, verify and `add --from-diff` failed on SQL Server 2016 ("Invalid column name 'history_retention_period'"); temporal tables now fingerprint the same on 2016 and later.
- Fixed more SQL Server 2016 failures, found running the tests and an end-to-end project on SQL Server 2016 and 2025 LocalDB: scripts from `add --from-diff` that drop an unnamed unique key or foreign key used `STRING_AGG` (SQL Server 2017+); deploying with Always Encrypted keys failed with "Invalid column name 'allow_enclave_computations'"; and `bsm convert data` failed with "Invalid column name 'graph_type'". A column master key with `enclaveComputations` on a server without secure enclaves (before SQL Server 2019) is now a clear resource conflict instead of a syntax error.
- Fixed: if recording the schema fingerprint fails after a deploy, the deploy now reports a warning (the changes are applied) instead of an unhandled exception, and the next `bsm deploy` records the fingerprint even when nothing is pending. Unexpected database errors print one line instead of a stack trace.
- Fixed: `bsm verify` said the schema "matches last deploy" when no fingerprint had been recorded; it now says none is recorded and drift was not checked.
- Fixed: `revert -y` printed "would revert" for each change before reverting.
- Fixed: on Windows, the MCP server's `add_change` and `rework_change` hung: looking up git's `user.name` for the plan's author left git waiting on the server's stdin. The lookup now gives git no input and gives up after 3 seconds.
- Fixed: on PostgreSQL 14.25, 15.20, 16.16, 17.12, 18.7 and later (the November 2026 releases for CVE-2026-6460), every table showed as drifted and `add --from-diff` scripted `~ column ...: options` changes. These releases store an internal `pg_attver` entry in each column's options, different in every database; bsm now leaves out internal `pg_` options. Fingerprints recorded on earlier releases are unchanged.
- Fixed: `--connection-string` now connects exactly as the string says. The environment's `tls` settings no longer override its `Encrypt` and `TrustServerCertificate`, joining host, database and sign-in (an environment with `authentication: integrated` no longer turns a SQL login string into Windows sign-in).
- Fixed: `bsm doctor` failed with "VIEW SERVER STATE permission was denied" for logins without that permission; it now reports the connection's encryption as unknown and the rest as usual.
- `bsm add --author`; the default author is `BSM_AUTHOR`, then git's `user.name`, then the OS user name.
- `env show` and `vars` show unset `${env:...}` references as written instead of failing.
- A missing SQL Server database now gives "Could not open database 'x': it does not exist ... create it first" instead of the login error, and connection descriptions show a port only when one is configured.
- Documented checking whether a database is at the build's plan head, and fast test databases from a template.
- SQLite provider (`bsm init --provider sqlite`): database files with the engine built in, nothing to install. Statement splitting as the `sqlite3` shell does it (trigger bodies included), lint for dot-commands and statements that cannot run in a transaction, journal tables prefixed `bsm_`, a file lock held for the whole deploy, a schema fingerprint, and `add --from-diff` / `diff-schema --script` that use `ALTER TABLE` where SQLite can and the documented table rebuild where it cannot (with foreign keys switched off when other tables refer to the rebuilt one). Imports take `--provider sqlite`; Sqitch SQLite projects and Flyway `jdbc:sqlite:` URLs map to it.
- `bsm convert schema` and `bsm convert data`: move a live database between SQL Server, PostgreSQL and SQLite, in any direction (`--to` picks the target). The schema becomes a new bsm project for the other provider (types mapped, names lowercased or snake_cased for PostgreSQL, views, functions, procedures and triggers translated, every uncertain spot marked `REVIEW bsm convert` and listed in `CONVERSION.md`); the rows are then copied (bulk copy, `COPY`, or prepared inserts for SQLite) and row counts compared. To SQLite, schemas become table-name prefixes, routines are kept commented out (single-expression functions are inlined into views), and trigger logic is flattened into SQLite's statement-only triggers.
- Everything the tool names is now `bsm`: the command (`bsm`, was `badger`), project files `bsm.yaml` and `bsm.plan`, the `bsm` journal schema, `-- bsm:` script directives, `{bsm.*}` variables, `BSM_*` environment variables and `~/.bsm`.
- Open source under the MIT license, with contribution, security and conduct guides.
- The .NET tool is published to nuget.org from GitHub Actions with Trusted Publishing.
- Fixed: on Windows, Always Encrypted connections failed with "'MSSQL_' prefix is reserved" because the system key stores were registered on the connection.

## 0.1.0 (November 2026)

The first release.

- Named changes in `bsm.plan`, merged with git's union driver.
- Deploy, revert and verify scripts, stored in the database at deploy time.
- SHA-256 hash certification with a chain id over the whole plan.
- SQL Server and Azure SQL provider: every Entra ID flow, SSH tunnels, Always Encrypted keys per environment.
- Environments in `bsm.yaml`, variables, repeatables, baselines and schema drift detection.
