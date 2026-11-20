# Handoff: still pending

Updated 2026-11-19 on bw-4k-pc (Windows), after the 0.4.0 release: the rename to Badger Trek is finished
(NuGet `Badger.Trek` 0.4.0 published, `Badger.SqlMigrate` deprecated with `Badger.Trek` as its alternate, the
website at `lonebadger.tech/badger-trek/` with the old path redirecting, downloads in the `badger-trek` container).
0.4.0 is a hard break by the user's decision: nothing from `bsm` 0.3.0 and earlier is read.
Delete this file when the items below are done (it is not documentation).

## Still pending (ask the user)

- Dogfooding suggestions not done: `baseline --check-schema`, excluding objects from the schema fingerprint (e.g. an
  old EF/Flyway history table), DNS check before an Entra token in `doctor`, a `trek clone`.
- The required-checks ruleset has no bypass, so even the maintainer cannot push straight to `dev`; everything
  goes through a PR. Ask whether to add an admin bypass for emergencies.

## CI and GitHub settings (done; keep in mind)

- `ci.yml` runs on pull requests and by hand only. Jobs have no `if:` conditions on purpose: a skipped job counts as a
  passed required check.
- Repository setting: workflow runs from all external contributors need approval (only brockweaver has write access).
- Ruleset "CI must pass before merging" (id 27665614) on `dev` (and `main`, which does not exist: releases are
  tagged on `dev`) requires 20 checks from GitHub Actions: `build` ×3, `sqlserver` 2016 SP1 LocalDB/2017/2019/2022/2025, `postgresql` 12–18, and `sqlite` on AlmaLinux 8
  (3.26.0), AlmaLinux 9 (3.34.1), Ubuntu 22.04 (3.37.2), Debian 12 (3.40.1), Ubuntu 24.04 (3.45.1). A renamed or new CI
  job must be added to the ruleset, or a removed one taken out, or PRs can never merge.

## Testing on bw-4k-pc

- LocalDB instances `trek2016` (SQL Server 2016 SP3, 13.0) and `treknew` (SQL Server 2025, 17.0) exist there:

      TREK_TEST_SQLSERVER="Server=(localdb)\trek2016;Integrated Security=true;TrustServerCertificate=true"
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
