# Methodology

## Changes are named, ordered by the plan

Flyway and DbUp order scripts by a number or name in the file name. Two developers who pick the same next number on two branches collide, and the tool can only either refuse or run "out of order" globally.

Badger SQL Migrate identifies a change by its **name** (its folder under `changes/`). Order comes from `bsm.plan`, one change per line. Because the plan is line-based, git's `union` merge driver merges two developers' appended lines without a conflict. `bsm plan check` then validates the result (unique names, dependencies exist and come earlier, every folder is listed).

## Hash certification

| Hash | Over | Used for |
|---|---|---|
| Script hash | name, requires, flags, deploy, revert, verify (length-prefixed, normalized) | Detecting edits to deployed changes |
| Chain id | SHA-256(provider, name, script hash, parent chain id) | Certifying order and content |
| Plan head | chain id of the last change | "What exactly will a fresh database get?" |
| Schema fingerprint | canonical dump of the catalog (system-generated constraint names masked) | Drift detection |

Normalization is deliberately minimal: strip a UTF-8 BOM and convert CRLF/CR to LF. Any other edit, including whitespace, changes the hash. Plan notes, authors and timestamps are not hashed.

When a change is deployed, the database records its script hash and a chain id computed from **the database's actual deploy order**. If the database received exactly a prefix of the plan in plan order, its head chain id equals the plan's chain id at that position, and `status` reports it as **certified**.

## Out-of-order changes

Shared development databases see merges in arbitrary order. Each profile picks a policy:

- `dependency` (default): a pending change that sits before already-deployed changes is applied if its dependencies are satisfied; it is flagged `applied_out_of_order` and the database is reported as *up to date but not certified*.
- `strict` (use for production): refuse. Production databases only ever receive exact plan order, so their head hash certifies them.

`bsm rebase --to <change>` returns a dev database to plan order.

## Down

`revert.sql` is required unless the change is marked `!irreversible`. At deploy time the deploy, revert and verify scripts are stored in `bsm.changes`. Revert executes the **stored** revert script, so it works from any branch or machine, even after the change was removed from the working copy (`status` lists such changes as orphans).

Repeatables keep their previous definition in `bsm.repeatables`; `bsm revert --repeatable <name>` re-applies it.

## Fixing deployed changes

A deployed change is immutable: any edit changes its hash, and deploy refuses the mismatch. Two ways forward:

- `bsm rework <change>` adds `<change>-v2`, which requires the original. Its deploy script starts as a copy of the original's, and its revert script is the original's deploy script, so reverting the rework restores the previous version. For definitions that can be re-run (procedures, views, permissions).
- `bsm repair <change>` accepts an edit without running SQL. It records the working copy's scripts, hash and flags, recomputes the chain ids of every later change from the database's deploy order, and logs a `repair` event with the old hash and a reason. Meant for revert or verify fixes and comment-only edits; an edited `deploy.sql` triggers a warning and a forced confirmation, because databases that ran the old script keep its result.

## Bundles

`bsm bundle create` writes a zip of the plan, the planned change folders, repeatables, environment scripts and (optionally) `bsm.yaml`, plus `bsm-bundle.json` with each file's SHA-256, the provider, the plan head and a content hash. Text is normalized and entries are sorted with fixed timestamps, so the same commit gives a byte-identical bundle on every OS. `--bundle <file>` verifies every hash, rejects missing or extra files, extracts to a temporary folder and recomputes the plan head before any command runs.

## Transactions

Each change, its verify script and its journal row commit in one transaction. If anything fails the transaction rolls back, a `fail` event is recorded and the database is unchanged. Statements SQL Server refuses inside a transaction (ALTER DATABASE, full-text DDL, RECONFIGURE, resumable index operations) are caught by the linter; mark such changes `!notx` and write them so they can be re-run.

Providers without transactional DDL (e.g. MySQL) declare it in their capabilities; the engine then records a change only after it succeeds.

## Schema diff

Providers split their schema dump into entries keyed by object (`column dbo.Customer.Email`, `module dbo.vCustomer`). `bsm diff-schema` compares the live schema with the snapshot recorded after the last run, or two environments, and reports added, removed and changed entries, with a line diff for multi-line definitions. Entries whose key repeats in a dump (several system-named constraints on one table) are compared by full text.

The SQL Server dump also records system versioning (period columns, versioning state, history table, retention) and partitioning (function type, direction and boundaries; scheme filegroups; the scheme and column of each partitioned index). A history table SQL Server named itself (`MSSQL_TemporalHistoryFor_<object id>`) is recorded under its table's name, because the id differs per server. An environment can set `schemaFingerprint.partitionBoundaries: ignore` when a job splits and merges partitions. The dump format has a version (`schema_format` in `bsm.meta`); a snapshot taken with an older format that no longer matches produces a warning instead of a drift error until the next deploy re-records it.

## Concurrency

Deploy, revert and baseline hold an exclusive deploy lock for the whole run (`sp_getapplock` on SQL Server, owned by the session). A second deployer waits up to `lockTimeoutSeconds` and then fails with a clear message.

## State tables (schema `bsm`, configurable)

- `changes`: applied changes with scripts, hashes, chain ids, who/when/where, duration, out-of-order and baseline flags
- `repeatables`: current and previous definition of each repeatable
- `events`: append-only deploy/revert/fail/repeatable/baseline log
- `schema_snapshots`: fingerprint and canonical dump after each run
- `meta`: state table version

## Environments

Everything that legitimately differs between dev, test and prod lives in the environment, not in change scripts: connection and identity, policies (`protected`, `allowRevert`, `ordering`), variables, Always Encrypted keys, and environment-only scripts. Change scripts stay byte-identical across environments, so every environment certifies against the same plan head.

The first deploy records the environment name in the database's `meta` table. Later runs under a different environment name are refused (`--claim-environment` to re-record a copied database).

## Variables and rendered hashes

Scripts are preprocessed (`{name}`, `$(name)`) before the provider sees them. The **raw** script is hashed for certification; the **rendered** script is hashed per environment and stored with each change. That separates "same change" (chain id) from "same SQL executed" (rendered hash): `verify` reports a change whose variables changed since it was deployed, and `revert` refuses to run a revert script that no longer matches what deploy expected. Per-run built-ins (`{deploy.*}`) opt a script out of rendered-hash tracking.

## Environment resources

Some objects must exist before changes run but cannot be in hashed scripts because their definitions differ per environment. Providers expose them through `IEnvironmentResources`. On SQL Server these are Always Encrypted keys: scripts name a column encryption key; each environment maps it to its own column master key, and bsm provisions the key metadata and material. Safe actions run automatically before a deploy; destructive ones (dropping an old key value after rotation) need `env provision --allow-destructive`; contradictions are conflicts that stop the deploy.

## Importing from other tools

`bsm import flyway|dbup|grate|sqitch` writes a new project; the importers live in Core (`Import/`) and know nothing about the database. Each imported change keeps the source identity in its plan note (`flyway V1.2 checksum <crc32>`, `dbup <script name>`), which is not hashed. `bsm baseline --from` reads the old tool's journal through the provider (`QuoteIdentifier` builds the table name), maps it to those notes, refuses gaps, unknown entries and (for Flyway) checksum mismatches, and then runs an ordinary baseline. Flyway's checksum is CRC-32 over each line's UTF-8 bytes without line breaks, with the BOM removed, as a signed integer (repeatables after placeholder replacement). grate's text_hash is base64 MD5 of the token-replaced script with LF line endings and quotes doubled, so baseline renders each imported script for the environment before comparing. Repeatables the old tool already applied with identical rendered text are recorded at baseline. Imports run the provider's linter and mark changes with statements that cannot run in a transaction `!notx`. Sqitch has no SQL Server engine, so a Sqitch import has no journal to baseline from.

## Generating changes

`bsm add <name> --from-diff` builds a shadow database from the plan (as far as the developer's database has deployed it, with that environment's variables), reads a detailed catalog model of both (`Providers.SqlServer/Scripting`: columns with identity, computed and default details, keys, indexes, foreign keys, checks, modules with dependencies, types, sequences, synonyms, temporal and partition settings) and scripts the differences in phases: drops of dependents first, then creates in dependency order. Columns whose type changes have their indexes and constraints dropped and re-created around the `ALTER COLUMN`; system-named constraints are dropped by a lookup at deploy time. Anything that needs a table rebuild or a judgement call is a warning, not SQL. The same generator writes the revert by comparing in the other direction, and `diff-schema --script` exposes it between two live databases. Tests check round trips against SQL Server: the deploy script makes the first database's fingerprint equal to the second's, and the revert restores it.

## PostgreSQL

The PostgreSQL provider (`Providers.PostgreSql`, Npgsql) implements the same contracts: a psql-compatible statement splitter (strings, quoted identifiers, nested comments, dollar quoting), a regex linter over split statements (transaction-block-only commands, transaction control, psql meta-commands, COPY FROM STDIN), the journal in PostgreSQL DDL, a session advisory lock, and a catalog dump built from PostgreSQL's deparsers with an empty search_path. Entra tokens for Azure Database for PostgreSQL are requested for `https://ossrdbms-aad.database.windows.net/.default` and sent as the password. It passes the shared provider conformance suite against PostgreSQL 17 in Docker. Sqitch's PostgreSQL registry (`sqitch.changes`) is readable for `bsm baseline --from sqitch`; its `script_hash` is the SHA-1 of the deploy file.

