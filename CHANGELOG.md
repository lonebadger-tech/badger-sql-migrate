# Changelog

All notable changes to Badger SQL Migrate. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). The website's [version history](https://lonebadger.tech/badger-sql-migrate/releases/) has the full release notes.

## Unreleased

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
