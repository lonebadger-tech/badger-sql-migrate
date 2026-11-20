# Handoff: still pending

Updated 2026-11-19 on bw-4k-pc (Windows), after PR #2 (SQL Server 2016 / LocalDB fixes, CI per version), PR #4
(SQLite 3.26+, `sqlite.minVersion`, system SQLite fallback) and the rename to Badger Trek (command `trek`).
Delete this file when the items below are done (it is not documentation).

## Still pending (ask the user)

- **After the rename to Badger Trek** (done in the `rename-badger-trek` PR, repository renamed to `badger-trek`), the
  user has to do these on nuget.org, which only they can: point the Trusted Publishing policy at the new repository
  name (`lonebadger-tech/badger-trek`, workflow `release.yml`), make sure it allows the new package id `Badger.Trek`
  (the first push creates it), and deprecate the old `Badger.SqlMigrate` package with a pointer to `Badger.Trek`. The
  website moves from `lonebadger.tech/badger-sql-migrate/` to `lonebadger.tech/badger-trek/`: deploy it there and
  redirect the old path. The local checkout folder is still `C:\githubcode\badger-sql-migrate`; renaming it is optional.
- Dogfooding suggestions not done: `baseline --check-schema`, excluding objects from the schema fingerprint (e.g. an
  old EF/Flyway history table), DNS check before an Entra token in `doctor`, a `trek clone`.
- Release questions: `ai-guide`/`mcp` are documented but not in NuGet 0.3.0; CHANGELOG lists 0.1.0 while NuGet has
  0.3.0. The next release notes should call out one behavior change: with the new default `sqlite.minVersion: "3.26"`,
  `trek add --from-diff` rebuilds a SQLite table to drop a column instead of `ALTER TABLE ... DROP COLUMN` (set
  `minVersion: "3.35"` to keep the old output).
- `trek convert data` resolves a SQLite target's relative `database:` from the working directory (as documented, like
  the `sqlite3` shell), not from the `--target` project. Running it from the source project after `trek deploy` in the
  target folder points at a different, empty file. Ask whether to resolve it from the target project.
- `trek convert schema --to sqlite` does not follow `sqlite.minVersion`: translated views and triggers can use
  `RETURNING`, `->>`, `unixepoch()` and generated columns (all newer than 3.26). Lint flags them (TREK008) in the
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

- LocalDB instances `bsm2016` (SQL Server 2016 SP3, 13.0) and `bsmnew` (SQL Server 2025, 17.0) exist there (named
  before the rename):

      TREK_TEST_SQLSERVER="Server=(localdb)\bsm2016;Integrated Security=true;TrustServerCertificate=true"
      dotnet test tests/Badger.Trek.Providers.SqlServer.Tests

- SQLite against another engine: `TREK_SQLITE_LIBRARY=<path to sqlite3.dll or libsqlite3.so.0>`, plus
  `TREK_TEST_SQLITE_VERSION=3.26.0` to assert the version. The official 3.26.0 Windows DLL is
  `https://www.sqlite.org/2018/sqlite-dll-win64-x64-3260000.zip`.
- Docker Desktop is installed but does not start (WSL is not installed), so the PostgreSQL and conversion
  integration tests skip there; CI covers them.

## Standing rules from the user (normally in the Mac's Claude config)

- The company for all registrations, publishing, legal pages, billing/organization fields and documentation is
  **Lone Badger Tech, LLC**. Never use CircaWare (CircaWare, LLC) for anything.
- Never commit, ask about or mention `.claude/launch.json`.
- Commit and push only when the user asks; branch from `dev`; end commit messages with the co-author line the
  session's instructions give. Ask before merging PRs.
