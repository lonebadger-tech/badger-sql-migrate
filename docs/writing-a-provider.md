# Writing a provider

All vendor-specific behaviour lives behind `IDatabaseProvider` (`src/Badger.SqlMigrate.Core/Providers`). The engine, CLI, hashing and SSH tunnel never reference a driver, and an architecture test (`tests/Badger.SqlMigrate.ArchitectureTests`) fails the build if they do.

`src/Badger.SqlMigrate.Providers.PostgreSql` is a complete example: it was added exactly this way. To add, for example, MySQL:

1. Create `src/Badger.SqlMigrate.Providers.MySql` referencing `Badger.SqlMigrate.Core` and the driver (MySqlConnector).
2. Implement:
   - `IDatabaseProvider`: id (`mysql`), capabilities, `DefaultSchema`, profile validation, endpoint resolution, `OpenSessionAsync`, `DescribeAsync`.
   - `IDatabaseSession`: transactions, `ExecuteAsync`, `QueryAsync`, server notices via `Message`.
   - `IScriptParser`: statement or batch splitting (dollar-quoted bodies for PostgreSQL, `DELIMITER` for MySQL).
   - `IScriptLinter`: statements that cannot run in a transaction (e.g. `CREATE INDEX CONCURRENTLY`).
   - `IStateStore`: the journal tables in the vendor's DDL, including `UpdateChangeAsync` for `bsm repair`.
   - `IDeployLock`: PostgreSQL uses `pg_try_advisory_lock`; MySQL `GET_LOCK`, SQLite `BEGIN EXCLUSIVE`.
   - `ISchemaInspector`: a canonical, sorted dump of the catalog, and (optionally) `Parse`, which splits a dump into entries keyed by object so `bsm diff-schema` can report an object as changed rather than removed and added.
   - `ResolveDatabaseName`: the database name for the `{db}` built-in.
   - `IEnvironmentResources` (optional): per-environment objects that must exist before changes run (SQL Server uses it for Always Encrypted keys).

   Variable preprocessing (`{name}`, `$(name)`), Entra ID tokens (`SessionContext.Tokens`) and SSH tunnels are vendor-neutral and come for free. Azure Database for PostgreSQL/MySQL can use the same Entra token source.
3. Register it in `Commands.Providers` in the CLI (one line) and add a project reference.
4. Add a test project that subclasses `ProviderConformanceTests` (`tests/Badger.SqlMigrate.Providers.Testing`) with a Testcontainers fixture and an `IConformanceDialect`. The provider is done when the conformance suite passes.

Map `ConnectionProfile.Authentication` to what the vendor supports and report unsupported kinds from `ValidateProfile`. Vendor-only settings go in `providerOptions`.
