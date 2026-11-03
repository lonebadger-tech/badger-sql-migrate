# Changelog

All notable changes to Badger SQL Migrate. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/). The website's [version history](https://lonebadger.tech/badger-sql-migrate/releases/) has the full release notes.

## Unreleased

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
