# Badger SQL Migrate

Free, cross-platform (Windows, macOS, Linux) database migrations with **real up and down**, **named changes instead of version numbers**, and **SHA-256 hash certification** of everything that reaches a database.

Day one ships the **SQL Server / Azure SQL** provider. The engine is vendor-neutral, so PostgreSQL, MySQL and SQLite can be added as providers without touching the engine or CLI.

MIT licensed. © Lone Badger Tech, LLC. Documentation: [lonebadger.tech/badger-sql-migrate](https://lonebadger.tech/badger-sql-migrate/).

## Why another migration tool?

| Problem in other tools | Badger SQL Migrate |
|---|---|
| Flyway charges for undo | `revert.sql` is first-class and free |
| Two developers both create `V12__...sql` | Changes are **named**; `badger.plan` is merged with git's union driver, so concurrent additions never conflict |
| Rollback depends on the scripts in your current checkout | Deploy, revert and verify scripts are **stored in the database** at deploy time; revert works from any branch or machine |
| Weak checksums (CRC32) or none (DbUp) | SHA-256 per change plus a **Merkle chain** over the plan: equal head hash = identical history |
| "Did someone hotfix prod?" | Schema fingerprint after every run; `badger verify` reports drift |
| Two pipelines deploying at once | Exclusive deploy lock (`sp_getapplock`) for the whole run |
| sqlcmd/Perl/JVM installs | One .NET tool, no shelling out |

## Install

```bash
dotnet tool install -g Badger.SqlMigrate
```

## Quick start

```bash
badger init -C db
badger add create-customer -C db
# edit db/changes/create-customer/deploy.sql, revert.sql, verify.sql
badger plan check -C db
badger status -C db
badger deploy -C db
```

A project looks like this:

```
db/
  badger.yaml                 # provider, connection profiles, variables
  badger.plan                 # ordered list of changes: the single source of ordering
  changes/
    create-customer/
      deploy.sql              # up
      revert.sql              # down (required unless the change is !irreversible)
      verify.sql              # optional: raise an error if deploy did not take effect
  repeatable/                 # views, procs, functions: re-applied when their hash changes
    views/vCustomer.sql
  environments/
    dev/seed.sql              # runs only in the dev environment
```

### The plan

```
create-customer                          @2026-11-03T14:10Z brock
add-customer-email [create-customer]     @2026-11-04T09:02Z alice
rebuild-fulltext !notx                   @2026-11-04T09:15Z bob   # runs outside a transaction
@release-1.4
```

- `[a b]` dependencies, `!notx` (no transaction), `!irreversible` (no revert), `# note`.
- `@tag` lines tag the change above; use them as `--to @release-1.4`.
- `badger init` adds `badger.plan merge=union` to `.gitattributes`. Run `badger plan check` in CI to catch duplicate names or broken dependencies after a merge.

### Commands

| Command | What it does |
|---|---|
| `init` | Create `badger.yaml`, `badger.plan`, folders, `.gitattributes` |
| `add <name> [-r dep] [--notx] [--irreversible]` | Scaffold a change and append it to the plan |
| `tag <name>` | Tag the last change |
| `plan check` / `plan show` | Validate plan, folders and scripts (lint) / list hashes and chain ids. No database needed |
| `status [--exit-code]` | Plan vs database: pending, applied, edited, orphaned, certified |
| `deploy [--to x] [--dry-run] [--script file] [--ordering strict]` | Deploy pending changes and repeatables |
| `revert --to x \| --last n \| --all [-y]` | Revert using the scripts stored in the database |
| `rebase --to x` | Revert to x, then redeploy to head |
| `verify` | Hash check, run verify scripts, detect schema drift |
| `log` | Deploy/revert/failure history from the database |
| `baseline --to x` | Adopt an existing database without running scripts |
| `doctor` | Check config, Entra sign-in and connectivity; show resources |
| `env list` / `env show` | Environments (with templates) / one environment fully merged, secrets masked |
| `env status` / `env provision [--allow-destructive]` | Compare / create or rotate per-environment resources (Always Encrypted keys) |
| `vars` | Every variable for an environment with value and source |
| `render <change> [--kind revert]` | The exact SQL a script becomes for an environment |
| `auth clear` | Forget saved Entra sign-ins |

Global options: `-C <dir>`, `-e <environment>`, `--var name=value`, `--connection-string`, `--claim-environment`, `--json`, `-v`, `--no-color`. Exit codes: `0` ok, `1` failure, `3` not up to date (`status --exit-code`).

## Configuration

`badger.yaml` holds the provider, shared variables and named **environments** (dev, test, prod...). Environments inherit from each other with `extends`; names starting with `_` are templates.

```yaml
provider: sqlserver
defaultEnvironment: dev
variables:
  AppRole: app_reader
  schemas:
    calc: calc_{env}                 # scripts can use {schemas.calc}
environments:
  _azure:
    database: App
    tls: { mode: strict }
    authentication: entraInteractive # browser sign-in with MFA; token cached between runs
    entra: { tenantId: 00000000-0000-0000-0000-000000000000 }
  dev:
    extends: _azure
    host: app-dev.database.windows.net
  prod:
    extends: _azure
    host: app-prod.database.windows.net
    protected: true                  # type the environment name (or --yes) to deploy
    allowRevert: false               # fix forward
    ordering: strict                 # exact plan order: certified history
    authentication: entraWorkloadIdentity
    connectionProperties:            # any SqlClient keyword
      Connect Retry Count: 3
    alwaysEncrypted:                 # per-environment CMK/CEK; scripts use the same CEK names everywhere
      columnMasterKeys:
        CMK_Prod: { keyStore: AZURE_KEY_VAULT, keyPath: "https://app-prod-kv.vault.azure.net/keys/cmk/0123" }
      columnEncryptionKeys:
        CEK_Customer: { columnMasterKey: CMK_Prod }
```

- **Authentication:** SQL logins, Windows/Kerberos, and every Entra ID flow: interactive and device code (MFA), default chain, managed identity, service principal (secret or certificate), workload identity, Azure CLI/PowerShell/azd, Azure Pipelines, environment, pre-acquired token, Entra Integrated and Entra Password. Sovereign clouds via `entra.authorityHost`.
- **Connection settings:** any SqlClient keyword under `connectionProperties`.
- **Always Encrypted:** column master keys and column encryption keys are provisioned per environment before changes run, with rotation and Key Vault, Windows stores or custom key store providers.
- **Variables:** `{env}`, `{db}`, `{change}`, `{deploy.utc}` and other built-ins, plus your own (nested, environment-specific, `--var`). `select * from [calc_{env}].[table1]` becomes `select * from [calc_test].[table1]` in test.
- **Safety:** the database remembers which environment owns it; prod settings pointed at a test database are refused.
- **Secrets:** `${env:NAME}` and `${file:path}`, resolved only for the environment in use.

Full reference: [docs/configuration.md](docs/configuration.md).

## How certification works

See [docs/methodology.md](docs/methodology.md). In short: every change's script hash is SHA-256 over its normalized deploy/revert/verify scripts (BOM stripped, CRLF→LF, so Windows and macOS checkouts agree). Each change's chain id is SHA-256 of `(provider, name, scriptHash, parentChainId)`. A database that received exactly the plan, in order, reports the same head chain id as `badger plan show`.

## Building

```bash
dotnet build
dotnet test
build/publish.sh        # release binaries for every platform, the .NET tool and SHA256SUMS into ./artifacts
```

The website pages are generated from `docs/website/` into the site repository:

```bash
python3 docs/website/build.py ../badger-sql/website
```

The SQL Server conformance tests start SQL Server 2022 with Testcontainers (Docker required) or use `BADGER_TEST_SQLSERVER=<connection string>`. They skip when neither is available.

Adding a database vendor: see [docs/writing-a-provider.md](docs/writing-a-provider.md).
