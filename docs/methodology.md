# Methodology

## Changes are named, ordered by the plan

Flyway and DbUp order scripts by a number or name in the file name. Two developers who pick the same next number on two branches collide, and the tool can only either refuse or run "out of order" globally.

Badger identifies a change by its **name** (its folder under `changes/`). Order comes from `badger.plan`, one change per line. Because the plan is line-based, git's `union` merge driver merges two developers' appended lines without a conflict. `bsm plan check` then validates the result (unique names, dependencies exist and come earlier, every folder is listed).

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

`revert.sql` is required unless the change is marked `!irreversible`. At deploy time the deploy, revert and verify scripts are stored in `badger.changes`. Revert executes the **stored** revert script, so it works from any branch or machine, even after the change was removed from the working copy (`status` lists such changes as orphans).

## Transactions

Each change, its verify script and its journal row commit in one transaction. If anything fails the transaction rolls back, a `fail` event is recorded and the database is unchanged. Statements SQL Server refuses inside a transaction (ALTER DATABASE, full-text DDL, RECONFIGURE, resumable index operations) are caught by the linter; mark such changes `!notx` and write them so they can be re-run.

Providers without transactional DDL (e.g. MySQL) declare it in their capabilities; the engine then records a change only after it succeeds.

## Concurrency

Deploy, revert and baseline hold an exclusive deploy lock for the whole run (`sp_getapplock` on SQL Server, owned by the session). A second deployer waits up to `lockTimeoutSeconds` and then fails with a clear message.

## State tables (schema `badger`, configurable)

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
