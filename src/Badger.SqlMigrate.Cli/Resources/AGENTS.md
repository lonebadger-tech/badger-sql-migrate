# Database changes (Badger SQL Migrate)

This folder is a Badger SQL Migrate project for **{{provider}}**. AI assistants: run `bsm ai-guide` for the
full guide before writing changes. The short version:

- Add a change with `bsm add <name> [--requires <change>] [--note "..."]`; never write `bsm.plan` lines by
  hand. Names are lower-case and hyphenated, e.g. `add-customer-email`.
- Fill in `changes/<name>/deploy.sql`, a `revert.sql` that undoes exactly what deploy did (in reverse order),
  and an optional `verify.sql` that raises an error when the change is missing.
- Each change runs in one transaction: no `BEGIN`/`COMMIT`/`ROLLBACK`. Statements that can't run in a
  transaction need `bsm add <name> --notx` and re-runnable scripts.
- Never edit a change that has been deployed anywhere: add a new one (`bsm rework <name>` for definitions).
- Views, procedures, functions and triggers go in `repeatable/`, written with {{repeatable}}.
- {{dialect}}
- Run `bsm plan check` and fix everything it reports. Don't run `bsm deploy` or `bsm revert` unless asked.
- `bsm mcp -C <this folder>` is an MCP server with these tools, if your assistant supports MCP.
