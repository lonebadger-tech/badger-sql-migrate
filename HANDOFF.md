# Handoff: still pending

Updated 2026-11-19 on bw-4k-pc (Windows) after the SQL Server 2016 / LocalDB testing (branch `localdb-2016-testing`).
Delete this file when the items below are done (it is not documentation).

## Testing on bw-4k-pc

LocalDB instances `bsm2016` (SQL Server 2016 SP3, 13.0) and `bsmnew` (SQL Server 2025, 17.0) exist there. Run the SQL
Server tests against either:

    BSM_TEST_SQLSERVER="Server=(localdb)\bsm2016;Integrated Security=true;TrustServerCertificate=true"
    dotnet test tests/Badger.SqlMigrate.Providers.SqlServer.Tests

Docker is installed but was not running, so the PostgreSQL and conversion integration tests skipped there.

## Still pending

- **Rename the product to "Badger DB Migrator"** (agreed: after SQLite and conversion landed, which they now have).
  Everything: product name in docs, website, help text and package descriptions; C# namespaces and assemblies
  (`Badger.SqlMigrate.*` → `Badger.DbMigrator.*`); NuGet package id. Ask the user about the `bsm` command name and
  the GitHub repository name before changing those.
- Dogfooding suggestions not done (ask the user): `baseline --check-schema`, excluding objects from the schema
  fingerprint (e.g. an old EF/Flyway history table), DNS check before an Entra token in `doctor`, a `bsm clone`.
- Release questions (ask): `ai-guide`/`mcp` are documented but not in NuGet 0.3.0; CHANGELOG lists 0.1.0 while
  NuGet has 0.3.0.
- `bsm convert data` resolves a SQLite target's relative `database:` from the working directory (as documented, like
  the `sqlite3` shell), not from the `--target` project. Running it from the source project after `bsm deploy` in the
  target folder points at a different, empty file. Ask the user whether to resolve it from the target project.

## Standing rules from the user (normally in the Mac's Claude config)

- The company for all registrations, publishing, legal pages, billing/organization fields and documentation is
  **Lone Badger Tech, LLC**. Never use CircaWare (CircaWare, LLC) for anything.
- Never commit, ask about or mention `.claude/launch.json`.
- Commit and push only when the user asks; branch from `dev`; end commit messages with the co-author line the
  session's instructions give.
