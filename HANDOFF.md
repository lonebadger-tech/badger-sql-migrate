# Handoff: continue on bw-4k-pc (Windows)

Written 2026-11-19 at the end of a Claude Code session on the Mac. Read this, do the work, and delete this file when the
work below is done (it is not documentation).

## Where things stand

`dev` contains, all with passing tests (386 on the Mac, database tests run in Docker there):

- **SQLite provider** (`src/Badger.SqlMigrate.Providers.Sqlite`), including `add --from-diff`.
- **`bsm convert schema` / `bsm convert data`** between SQL Server, PostgreSQL and SQLite in all six directions
  (`src/Badger.SqlMigrate.Conversion`, `src/Badger.SqlMigrate.Cli/ConvertCommands.cs`; docs page
  `docs/website/docs_pages.py` → `converting()`).
- **Fixes for the dogfooding report** from the badger-dungeon (Recall Notice) project, findings 1-16
  (CHANGELOG "Unreleased" lists them). Their log: `docs/bsm-dogfooding-findings.md` in lonebadger-tech/badger-dungeon.

Nothing is released: NuGet is still 0.3.0.

## Why this machine

Two fixes could not be tested for real on the Mac:

1. **SQL Server 2016** (finding 1): `deploy`, `verify`, `add --from-diff` failed with
   "Invalid column name 'history_retention_period'" because those `sys.tables` columns arrived in SQL Server 2017.
   The fix is `src/Badger.SqlMigrate.Providers.SqlServer/SqlServerCompatibility.cs` (queries adapt when
   `COL_LENGTH('sys.tables','history_retention_period')` is NULL; 2016 is reported as -1 / INFINITE retention so
   fingerprints match across versions). It is tested only by forcing that path on SQL Server 2022
   (`TemporalAndPartitionTests.Servers_without_history_retention_columns_...`).
   A database's *compatibility level* does not change the catalog: a real 2016 **engine** is needed. SQL Server
   2016 LocalDB (13.0) is what the dogfooding project used.
2. **LocalDB** in general: `host: (localdb)\MSSQLLocalDB` with `authentication: integrated` is documented
   (`connections()` in `docs/website/docs_pages.py`) but untested here. Also new: the missing-database message and a
   connection description without a made-up port (`SqlServerConnectionFactory.Describe`, `ConnectionMessageTests`).

## What to do

1. Install what is missing (ask the user before each download; Microsoft's own sites only):
   - .NET 10 SDK.
   - SQL Server 2016 Express **LocalDB** (`SqlLocalDB.msi`, version 13.x) side by side with any newer LocalDB.
   - `sqlcmd` if absent (handy, not required).
2. `sqllocaldb versions` should list 13.0. Create instances explicitly per version, e.g.
   `sqllocaldb create bsm2016 13.0 -s` and, if a newer LocalDB exists, `sqllocaldb create bsmnew -s`.
   Use only instances you create (names starting with `bsm`).
3. Build and run the provider-independent tests: `dotnet test Badger.SqlMigrate.slnx`. Without Docker the
   PostgreSQL/SQL Server container tests skip; to run the SQL Server ones against LocalDB set
   `BSM_TEST_SQLSERVER="Server=(localdb)\bsm2016;Integrated Security=true;TrustServerCertificate=true"` (they create
   and drop their own databases) and run `dotnet test tests/Badger.SqlMigrate.Providers.SqlServer.Tests`.
   Expect failures from features 2016 lacks (e.g. `CREATE OR ALTER` is 2016 SP1+, `STRING_AGG` is 2017+,
   `TRIM` 2017+, TDS 8 / strict TLS): sort out which tests are version-dependent versus real bugs, and make the
   tool degrade with a clear message where a feature truly needs a newer server.
4. End-to-end on LocalDB 2016 and on the newer LocalDB, with a project made by `bsm init` (environment
   `host: (localdb)\bsm2016`, `authentication: integrated`, `database: BsmProbe`):
   - Missing database: `bsm deploy` must say to create it (finding 14), then `CREATE DATABASE BsmProbe`.
   - A temporal table change (system-versioned, with and without `HISTORY_RETENTION_PERIOD` on the newer one):
     `deploy`, `status`, `verify` (expects "matches last deploy"), drift by hand → `verify` reports it,
     `diff-schema`, `add --from-diff`, `revert --all -y` (no "would revert" lines).
   - Same fingerprint for the same plan on 2016 and the newer instance (`bsm.schema_snapshots.fingerprint`), except
     where 2016 lacks retention.
   - `bsm doctor`, `env show` with an unset `${env:...}`, `bsm add --author`.
   - `bsm convert schema` from LocalDB to SQLite (no server needed on the other side) and `convert data`.
5. Fix what breaks, with tests; update CHANGELOG; commit on a branch, push, PR into `dev` (ask before merging).

## Still pending after that

- **Rename the product to "Badger DB Migrator"** (agreed: after SQLite and conversion landed, which they now have).
  Everything: product name in docs, website, help text and package descriptions; C# namespaces and assemblies
  (`Badger.SqlMigrate.*` → `Badger.DbMigrator.*`); NuGet package id. Ask the user about the `bsm` command name and
  the GitHub repository name before changing those.
- Dogfooding suggestions not done (ask the user): `baseline --check-schema`, excluding objects from the schema
  fingerprint (e.g. an old EF/Flyway history table), DNS check before an Entra token in `doctor`, a `bsm clone`.
- Release questions (ask): `ai-guide`/`mcp` are documented but not in NuGet 0.3.0; CHANGELOG lists 0.1.0 while
  NuGet has 0.3.0.

## Standing rules from the user (normally in the Mac's Claude config)

- The company for all registrations, publishing, legal pages, billing/organization fields and documentation is
  **Lone Badger Tech, LLC**. Never use CircaWare (CircaWare, LLC) for anything.
- Never commit, ask about or mention `.claude/launch.json`.
- Commit and push only when the user asks; branch from `dev`; end commit messages with the co-author line the
  session's instructions give.
