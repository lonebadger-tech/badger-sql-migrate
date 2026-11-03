# Badger SQL Migrate

Free, cross-platform (Windows, macOS, Linux) database migrations for SQL Server and Azure SQL with **real up and down**, **named changes instead of version numbers**, and **SHA-256 hash certification** of everything that reaches a database.

Documentation: [lonebadger.tech/badger-sql-migrate](https://lonebadger.tech/badger-sql-migrate/)

## Install

```bash
dotnet tool install --global Badger.SqlMigrate
```

To pin a version for a repository (recommended for CI):

```bash
dotnet new tool-manifest
dotnet tool install Badger.SqlMigrate
dotnet tool restore        # in CI
dotnet badger status
```

## Quick start

```bash
badger init -C db
badger add create-customer -C db
# edit db/changes/create-customer/deploy.sql, revert.sql, verify.sql
badger plan check -C db
badger deploy -C db
```

- [Quick start](https://lonebadger.tech/badger-sql-migrate/docs/quick-start/)
- [Commands](https://lonebadger.tech/badger-sql-migrate/docs/commands/)
- [CI/CD](https://lonebadger.tech/badger-sql-migrate/docs/ci-cd/)
- [Examples](https://lonebadger.tech/badger-sql-migrate/examples/)

## License

[MIT](https://github.com/lonebadger-tech/badger-sql-migrate/blob/dev/LICENSE). © Lone Badger Tech, LLC.

Source, issues and contributions: [github.com/lonebadger-tech/badger-sql-migrate](https://github.com/lonebadger-tech/badger-sql-migrate). Support: [support@lonebadger.tech](mailto:support@lonebadger.tech)
