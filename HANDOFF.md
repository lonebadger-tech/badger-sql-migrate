# Handoff: still pending

Updated 2026-11-19 on bw-4k-pc (Windows), after PR #2 (SQL Server 2016 / LocalDB fixes, CI per version) and PR #4
(SQLite 3.26+, `sqlite.minVersion`, system SQLite fallback) were merged into `dev`.
Delete this file when the items below are done (it is not documentation).

## Still pending (ask the user)

- **Rename the product to "Badger Trek"** (chosen 2026-11-19, replacing the earlier plan of "Badger DB Migrator").
  Everything: product name in docs, website, help text and package descriptions; C# namespaces and assemblies
  (`Badger.SqlMigrate.*` → `Badger.Trek.*`); NuGet package id (`Badger.Trek`). Before starting, check that the NuGet
  id, a GitHub repository name and a domain are free. The command is `trek` (decided; `bsm` → `trek`, `dotnet trek`
  for the local tool). Confirm with the user:
  - the GitHub repository name (e.g. `badger-trek`);
  - what the other `bsm` names become (`bsm.yaml`, `bsm.plan`, the `bsm` journal schema and `bsm_*` SQLite tables,
    `-- bsm:` directives, `{bsm.*}` variables, `BSM_*` environment variables, `~/.bsm`, lint codes `BSM0xx`), and
    how existing projects and databases carry over: at minimum, keep reading the old file names and find an existing
    `bsm` journal so deployed databases are not seen as empty.
- Dogfooding suggestions not done: `baseline --check-schema`, excluding objects from the schema fingerprint (e.g. an
  old EF/Flyway history table), DNS check before an Entra token in `doctor`, a `bsm clone`.
- Release questions: `ai-guide`/`mcp` are documented but not in NuGet 0.3.0; CHANGELOG lists 0.1.0 while NuGet has
  0.3.0. The next release notes should call out one behavior change: with the new default `sqlite.minVersion: "3.26"`,
  `add --from-diff` rebuilds a SQLite table to drop a column instead of `ALTER TABLE ... DROP COLUMN` (set
  `minVersion: "3.35"` to keep the old output).
- `bsm convert data` resolves a SQLite target's relative `database:` from the working directory (as documented, like
  the `sqlite3` shell), not from the `--target` project. Running it from the source project after `bsm deploy` in the
  target folder points at a different, empty file. Ask whether to resolve it from the target project.
- `bsm convert schema --to sqlite` does not follow `sqlite.minVersion`: translated views and triggers can use
  `RETURNING`, `->>`, `unixepoch()` and generated columns (all newer than 3.26). Lint flags them (BSM008) in the
  converted project. Ask whether conversion should write SQL for the oldest version instead.
- The required-checks ruleset has no bypass, so even the maintainer cannot push straight to `dev`/`main`; everything
  goes through a PR. Ask whether to add an admin bypass for emergencies.
- `docs/website/docs_pages.py` does not compile with Python 3.12 (`unterminated string literal` around line 1662, in
  the conversion page's SQLite table). It was already broken before PR #4. Check how the site is built and fix it.

## CI and GitHub settings (done; keep in mind)

- `ci.yml` runs on pull requests and by hand only. Jobs have no `if:` conditions on purpose: a skipped job counts as a
  passed required check.
- Repository setting: workflow runs from all external contributors need approval (only brockweaver has write access).
- Ruleset "CI must pass before merging" (id 27665614) on `dev` and `main` requires 20 checks from GitHub Actions:
  `build` ×3, `sqlserver` 2016 SP1 LocalDB/2017/2019/2022/2025, `postgresql` 12–18, and `sqlite` on AlmaLinux 8
  (3.26.0), AlmaLinux 9 (3.34.1), Ubuntu 22.04 (3.37.2), Debian 12 (3.40.1), Ubuntu 24.04 (3.45.1). A renamed or new CI
  job must be added to the ruleset, or a removed one taken out, or PRs can never merge.

## Testing on bw-4k-pc

- LocalDB instances `bsm2016` (SQL Server 2016 SP3, 13.0) and `bsmnew` (SQL Server 2025, 17.0) exist there:

      BSM_TEST_SQLSERVER="Server=(localdb)\bsm2016;Integrated Security=true;TrustServerCertificate=true"
      dotnet test tests/Badger.SqlMigrate.Providers.SqlServer.Tests

- SQLite against another engine: `BSM_SQLITE_LIBRARY=<path to sqlite3.dll or libsqlite3.so.0>`, plus
  `BSM_TEST_SQLITE_VERSION=3.26.0` to assert the version. The official 3.26.0 Windows DLL is
  `https://www.sqlite.org/2018/sqlite-dll-win64-x64-3260000.zip`.
- Docker Desktop is installed but does not start (WSL is not installed), so the PostgreSQL and conversion
  integration tests skip there; CI covers them.

## Standing rules from the user (normally in the Mac's Claude config)

- The company for all registrations, publishing, legal pages, billing/organization fields and documentation is
  **Lone Badger Tech, LLC**. Never use CircaWare (CircaWare, LLC) for anything.
- Never commit, ask about or mention `.claude/launch.json`.
- Commit and push only when the user asks; branch from `dev`; end commit messages with the co-author line the
  session's instructions give. Ask before merging PRs.
