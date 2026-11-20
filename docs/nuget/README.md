# Badger Trek

Free, cross-platform (Windows, macOS, Linux) database migrations for SQL Server, Azure SQL, PostgreSQL and SQLite with **real up and down**, **named changes instead of version numbers**, and **SHA-256 hash certification** of everything that reaches a database.

Documentation: [lonebadger.tech/badger-trek](https://lonebadger.tech/badger-trek/)

> **Coming from Badger SQL Migrate (`Badger.SqlMigrate`, command `bsm`)?** Badger Trek is the same tool under a new name, and it reads nothing the old one wrote. Uninstall `Badger.SqlMigrate`, install `Badger.Trek`, rename `bsm.yaml`/`bsm.plan` to `trek.yaml`/`trek.plan`, and move each database's journal once (trek prints the SQL, and the history is kept): [Upgrading from Badger SQL Migrate](https://lonebadger.tech/badger-trek/docs/upgrading/).

## Install

```bash
dotnet tool install --global Badger.Trek
```

To pin a version for a repository (recommended for CI):

```bash
dotnet new tool-manifest
dotnet tool install Badger.Trek
dotnet tool restore        # in CI
dotnet trek status
```

## Quick start

```bash
trek init -C db
trek add create-customer -C db
# edit db/changes/create-customer/deploy.sql, revert.sql, verify.sql
trek plan check -C db
trek deploy -C db
```

## AI assistants

New projects get an `AGENTS.md` with the rules for writing changes, `trek ai-guide` prints the full guide, and `trek mcp -C db` is an MCP server for Claude, VS Code, Cursor and other clients (project files only; it never connects to a database):

```bash
claude mcp add trek -- trek mcp -C db
```

- [Quick start](https://lonebadger.tech/badger-trek/docs/quick-start/)
- [AI assistants](https://lonebadger.tech/badger-trek/docs/ai/)
- [Commands](https://lonebadger.tech/badger-trek/docs/commands/)
- [CI/CD](https://lonebadger.tech/badger-trek/docs/ci-cd/)
- [Examples](https://lonebadger.tech/badger-trek/examples/)

## License

[MIT](https://github.com/lonebadger-tech/badger-trek/blob/dev/LICENSE). © Lone Badger Tech, LLC.

Source, issues and contributions: [github.com/lonebadger-tech/badger-trek](https://github.com/lonebadger-tech/badger-trek). Support: [support@lonebadger.tech](mailto:support@lonebadger.tech)
