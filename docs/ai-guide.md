# Writing Badger SQL Migrate changes: a guide for AI tools

This guide is for AI coding assistants (and people) writing database changes in a Badger SQL Migrate (`bsm`)
project. Follow it exactly: bsm certifies every change by hash, so a mistake in a deployed change can't be fixed
by editing it later.

## The project

```
bsm.yaml                 provider (sqlserver or postgresql), environments, variables
bsm.plan                 every change, one per line, in deploy order
changes/<name>/
  deploy.sql             makes the change
  revert.sql             undoes exactly what deploy.sql did
  verify.sql             optional: raises an error if the change is not in effect
repeatable/              views, procedures, functions, triggers: re-applied whenever they change
environments/<env>/      repeatables that run only in that environment (seed data, test users)
```

Read `bsm.yaml` first to learn the provider: `provider: sqlserver` means T-SQL, `provider: postgresql` means
PostgreSQL. Never put connection strings, passwords or secrets in scripts.

## Adding a change

1. Create the change with the CLI (or the `add_change` MCP tool). Don't write plan lines by hand:

   ```
   bsm add add-customer-email --requires create-customer --note "email for receipts"
   ```

   This creates `changes/add-customer-email/` with the three scripts and appends a line to `bsm.plan`.
2. Write `deploy.sql`, `revert.sql` and, when useful, `verify.sql`.
3. Run `bsm plan check` (or the `check` MCP tool) and fix every error and warning it reports.
4. Stop there. A person deploys with `bsm deploy`. Don't run `deploy`, `revert`, `rebase`, `repair` or
   `baseline` unless the user asked you to, and never against an environment other than their local one.

### Names and dependencies

- Names are short, lower-case, hyphenated and describe the change: `create-customer`, `add-order-status-index`,
  `backfill-customer-region`. Letters, digits, `-`, `_` and `.` only.
- `--requires` lists changes that must be deployed first, usually the change that created the table you alter.
  Required changes must already be in the plan.
- One logical change per change. Several small changes are better than one large one: each can be reverted on
  its own.

## Rules for the scripts

- **revert.sql undoes exactly what deploy.sql did, in reverse order.** Deploy creates a table and an index;
  revert drops the index, then the table. Deploy adds a column; revert drops it. Deploy changes data; revert
  restores it, or the change is marked irreversible.
- **verify.sql raises an error when the change is missing.** A query that merely returns no rows passes. Delete
  `verify.sql` if there is nothing worth checking.
- **A change runs in one transaction.** Don't write `BEGIN TRANSACTION`, `COMMIT` or `ROLLBACK`. A failure
  rolls back the whole change, so scripts don't need cleanup code.
- **Statements that can't run in a transaction need `--notx`.** Examples: `CREATE INDEX CONCURRENTLY`,
  `ALTER DATABASE`, full-text indexes. Create the change with `bsm add <name> --notx` and write the scripts so
  they can be run again (`IF NOT EXISTS` in deploy, `IF EXISTS` in revert), because a failure part way leaves
  partial work. The linter (BSM001) tells you when this is needed; with the MCP server, set it with
  `update_change` (`notx: true`).
- **Irreversible changes:** when undoing really isn't possible (dropping data that can't be restored), use
  `bsm add <name> --irreversible`. There is then no `revert.sql`. Prefer reversible designs: rename or copy
  before you drop.
- **Don't use `USE` (SQL Server) or `\connect` (PostgreSQL).** bsm deploys to the environment's database.
- **Schema-qualify every object:** `dbo.Customer`, `public.customer`.
- **Data changes** belong in changes too, written to be correct on every environment's data. Use variables for
  values that differ between environments.

## Never edit a deployed change

Once a change is deployed anywhere, its scripts are frozen: if a single character changes, its hash no longer
matches and every deploy stops. To change what a deployed change did:

- **New behaviour** (another column, a new procedure body): add a new change. For a definition that can be run
  again, `bsm rework <name>` adds `<name>-v2`, whose deploy script starts as a copy to edit and whose revert
  restores the old version.
- **A harmless fix** to a deployed change's `revert.sql`, `verify.sql` or comments: edit it, then a person
  runs `bsm repair <name>` in each environment. Don't use repair to change what deploy does.

You can't see which changes are deployed from the files alone. When unsure, ask the user, or have them run
`bsm status`. Changes that are new in the current branch and not yet in the main branch are usually safe to
edit.

## Repeatables

Views, stored procedures, functions and triggers go in `repeatable/`, not in changes, written so they can run
again:

- SQL Server: `CREATE OR ALTER VIEW|PROCEDURE|FUNCTION|TRIGGER ...`
- PostgreSQL: `CREATE OR REPLACE VIEW|FUNCTION|PROCEDURE ...`, and `DROP ... IF EXISTS` then `CREATE` for
  objects that have no `OR REPLACE`.

Name them by path, for example `repeatable/views/active_customer.sql`. bsm applies them after the changes,
whenever their text differs from what was last applied. When one repeatable uses another, declare it on the
first line:

```
-- bsm:requires views/active_customer.sql functions/full_name.sql
```

List columns explicitly in views (no `SELECT *`): a view that selects every column depends on all of them,
so a later change can't drop or retype a column the view never needed.

Tables, columns, indexes and data are never repeatables. Anything a change needs while it deploys (a function
used in a computed column or a default) must be created by a change, because repeatables run after changes.

Scripts under `environments/<env>/` run only in that environment, after the shared repeatables. Write them so
they can run again (`MERGE`, `INSERT ... ON CONFLICT DO NOTHING`, `IF NOT EXISTS`).

## Variables

Scripts are preprocessed before they run. `{name}` and `$(name)` are replaced with variables from `bsm.yaml` or
built-ins such as `{env}` (environment name) and `{db}` (database name). Use them for names and values that
differ between environments. An unknown variable is an error; write `{{name}}` for literal braces. JSON
(`'{"a":1}'`), format strings (`'{0}'`) and ODBC escapes are left alone. `bsm render <change> -e <env>` shows
the exact SQL an environment will receive.

## SQL Server

- Split batches with a line containing only `GO`, as SSMS does. `CREATE VIEW`, `CREATE PROCEDURE`,
  `CREATE FUNCTION` and `CREATE TRIGGER` must be the first statement in their batch.
- Guard objects that may exist with `IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL` or `DROP ... IF EXISTS`.
- verify.sql: `IF COL_LENGTH('dbo.Customer', 'Email') IS NULL THROW 50000, 'Customer.Email is missing', 1;`
- Temporal tables: when you set `SYSTEM_VERSIONING = OFF` to alter a table, turn it back on in the same script
  (BSM004).

Example change `add-customer-email`:

```sql
-- deploy.sql
ALTER TABLE dbo.Customer ADD Email nvarchar(320) NULL;
GO
CREATE UNIQUE INDEX UX_Customer_Email ON dbo.Customer (Email) WHERE Email IS NOT NULL;
```

```sql
-- revert.sql
DROP INDEX UX_Customer_Email ON dbo.Customer;
GO
ALTER TABLE dbo.Customer DROP COLUMN Email;
```

```sql
-- verify.sql
IF COL_LENGTH('dbo.Customer', 'Email') IS NULL
    THROW 50000, 'dbo.Customer.Email is missing', 1;
```

## PostgreSQL

- Statements end with `;`. Function bodies and `DO` blocks use dollar quoting (`$$ ... $$`); semicolons
  inside them don't split statements.
- Use `IF EXISTS` / `IF NOT EXISTS` freely; DDL is transactional, so a failed change leaves nothing behind.
- verify.sql: a `DO` block that raises:
  `DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'customer' AND column_name = 'email') THEN RAISE EXCEPTION 'customer.email is missing'; END IF; END $$;`
- `ALTER TYPE ... ADD VALUE`: the new enum value can't be used until the change commits, so use it in a later
  change (BSM007).
- psql meta-commands work: `\i`, `\ir`, `\set`, `:'var'`, `\if`/`\endif`, `\copy ... from 'file.csv'`,
  `\gexec`. These are refused (BSM005): `\connect`, `\gset`, `\copy ... to`, `\o`, `\!`, `\prompt`, and
  backquoted shell commands.

Example change `add-customer-email`:

```sql
-- deploy.sql
ALTER TABLE public.customer ADD COLUMN email text;
CREATE UNIQUE INDEX customer_email_key ON public.customer (email) WHERE email IS NOT NULL;
```

```sql
-- revert.sql
DROP INDEX public.customer_email_key;
ALTER TABLE public.customer DROP COLUMN email;
```

## Linter codes

`bsm plan check` reports these. Errors must be fixed; fix warnings unless the user says otherwise.

| Code | Provider | Meaning and fix |
|---|---|---|
| BSM000 | SQL Server | The script didn't parse, so lint was skipped. Check the syntax. |
| BSM001 | both | A statement can't run in a transaction. Mark the change `!notx` (`update_change` with `notx: true`, or `--notx` on `bsm add`) and make it re-runnable. |
| BSM002 | SQL Server | `USE` switches databases. Remove it. |
| BSM003 | both | `BEGIN`/`COMMIT`/`ROLLBACK` in a transactional change. Remove it. |
| BSM004 | SQL Server | `SYSTEM_VERSIONING = OFF` without turning it back on. Turn it back on in the same script. |
| BSM005 | PostgreSQL | A psql command bsm can't run, or unbalanced `\if`/`\endif`. Remove it. |
| BSM007 | PostgreSQL | An enum value added with `ADD VALUE` is used in the same change. Move that use to a later change. |

## Generating a change from a database (person-run)

When a person has already made schema edits in their local database, `bsm add <name> --from-diff` writes
`deploy.sql` and `revert.sql` by comparing that database with the plan. Review the output: it is a starting
point, and data migrations still need writing by hand.

## Commands for checking your work

| Command | Needs a database | Use |
|---|---|---|
| `bsm plan check --json` | no | Errors and lint findings for the whole project |
| `bsm plan show --json` | no | Every change with its dependencies, flags and tags |
| `bsm render <change> [--kind revert] -e <env>` | no | The SQL an environment will receive |
| `bsm ai-guide` | no | This guide, matching the installed version |
| `bsm status -e <env> --json` | yes | What is deployed (ask the user before connecting) |

`--json` prints machine-readable output on stdout; warnings and errors go to stderr. Exit code 0 means
success.

## MCP server

`bsm mcp -C <project folder>` runs a Model Context Protocol server over stdio with the tools `project_info`,
`plan_show`, `read_change`, `add_change`, `write_change_script`, `update_change`, `rework_change`,
`write_repeatable`, `check`, `lint_sql`, `render` and `guide`. It only reads and writes project files; it never connects to a database.
