# Contributing to Badger SQL Migrate

Thanks for helping. Bug reports, documentation fixes, new providers and features are all welcome.

## Before you start

- **Bugs:** open an [issue](https://github.com/lonebadger-tech/badger-sql-migrate/issues/new/choose) with `bsm --version`, the command you ran, and the output of `bsm doctor`. Remove passwords, tokens and server names you don't want public.
- **Features and larger changes:** open an issue first to agree on the approach, so a pull request doesn't go to waste.
- **Security problems:** don't open an issue. Follow [SECURITY.md](SECURITY.md).

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Docker is optional: it runs the SQL Server tests.

```bash
dotnet build
dotnet test
dotnet run --project src/Badger.SqlMigrate.Cli -- plan check -C samples/demo-db
```

- The SQL Server conformance tests start SQL Server 2022 in Docker with Testcontainers, or use the server in `BSM_TEST_SQLSERVER=<connection string>`. Without either they're skipped, and CI runs them on Linux.
- Warnings are errors (`TreatWarningsAsErrors`), so the build must be clean.
- `tests/Badger.SqlMigrate.ArchitectureTests` fails the build if the engine, connectivity or CLI reference a database driver directly. Vendor code belongs in a provider.

## Pull requests

1. Fork the repository and branch from `dev`. Pull requests target `dev`.
2. Keep each pull request to one change, with tests for new behavior and bug fixes.
3. Update the documentation with the code:
   - `README.md` and `docs/` for repository docs.
   - `docs/website/` for the website pages (they're generated from there). Preview them with `python3 docs/website/build.py <empty folder>`.
   - `CHANGELOG.md`, under **Unreleased**.
4. Make sure `dotnet build` and `dotnet test` pass. CI runs both on Windows, macOS and Linux.

By submitting a pull request you agree that your contribution is licensed under the [MIT license](LICENSE), like the rest of the project.

## Compatibility rules

These are what make certification trustworthy, so changes to them need extra care and discussion in an issue first:

- **Script hashes and chain ids** ([docs/methodology.md](docs/methodology.md)): changing how they're computed changes every existing database's history. Never change them silently.
- **Journal tables:** schema changes need a migration path for databases deployed by earlier versions.
- **`bsm.yaml` and `bsm.plan`:** existing files must keep working. Deprecate before removing.
- **`--json` output and exit codes:** pipelines parse them.

## Adding a database provider

See [docs/writing-a-provider.md](docs/writing-a-provider.md). A provider is done when it passes the shared conformance suite in `tests/Badger.SqlMigrate.Providers.Testing`.

## Releases

Maintainers release by pushing a `v<version>` tag on `dev`: `.github/workflows/release.yml` builds every platform, publishes the .NET tool to nuget.org and creates the GitHub release.
