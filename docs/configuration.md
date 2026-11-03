# Configuration reference (`badger.yaml`)

```yaml
provider: sqlserver
defaultEnvironment: dev           # or BADGER_ENV, or --env
preprocessor:
  unknown: error                  # error (default) | keep: leave unknown {name} tokens untouched
  sqlcmdSyntax: true              # also expand $(name)
variables:                        # shared by all environments; see "Variables"
  AppRole: app_reader
  schemas:
    calc: calc_{env}
environments:
  _azure:                         # '_' prefix = template, only used through 'extends'
    database: App
    tls: { mode: strict }
    authentication: entraInteractive
    entra: { tenantId: 00000000-0000-0000-0000-000000000000 }
  dev:
    extends: _azure
    host: app-dev.database.windows.net
  prod:
    extends: _azure
    host: app-prod.database.windows.net
    protected: true
    allowRevert: false
    ordering: strict
```

Secrets never belong in the file: any string may use `${env:NAME}` or `${file:path}`. They are resolved only for the environment being used, so a missing production secret never breaks a dev run.

## Environments

`extends` deep-merges the parent first (maps merge key by key; scalars and lists are replaced). Chains are allowed; cycles are errors. `badger env show -e prod` prints the merged result with secrets masked.

| Key | Meaning |
|---|---|
| `description` | Shown in `env list`; available as `{env.description}` |
| `connectionString` | Raw SqlClient connection string. The fields below override it |
| `host`, `port`, `database` | Server and database |
| `authentication` | See *Authentication* |
| `user`, `password` | SQL login, or Entra user for `entraPassword` / `entraIntegrated` |
| `entra` | Entra ID options (see below) |
| `tls` | `mode` (default/optional/mandatory/strict), `trustServerCertificate`, `hostNameInCertificate`, `serverCertificate` |
| `connectionProperties` | **Any** SqlClient connection-string keyword, applied last (validated by SqlClient) |
| `ssh` | Bastion tunnel: `host`, `port`, `user`, `privateKeyFile`, `privateKeyPassphrase`, `password`, `knownHostsFile`, `hostKeyFingerprint`, `insecureSkipHostKeyCheck` |
| `protected` | Deploy, revert, baseline and destructive provisioning require typing the environment name (or `--yes`) |
| `allowRevert` | `false` refuses revert and rebase (fix forward) |
| `ordering` | `dependency` (shared dev databases) or `strict` (exact plan order; certified history) |
| `stateSchema` | Schema for badger's journal tables (default `badger`) |
| `commandTimeoutSeconds`, `lockTimeoutSeconds` | Per-command timeout; how long to wait for another deployer |
| `variables` | Environment-specific variables (override shared ones) |
| `alwaysEncrypted` | SQL Server Always Encrypted keys (see below) |

**Database ownership.** The first deploy records the environment name in the database. Running as another environment (for example `prod` settings pointed at the test server by mistake) is refused. If a database was copied on purpose (prod restored into test), pass `--claim-environment` once.

**Environment-specific scripts.** `environments/<name>/**/*.sql` are repeatable scripts that run only in that environment, after the shared `repeatable/` scripts (seed data, grants, test users). They are hash-tracked like repeatables but are not part of the plan certificate, so every environment still certifies against the same plan.

## Authentication

| `authentication` | How | Notes |
|---|---|---|
| `password` | SQL login | |
| `integrated` | Windows SSPI; Kerberos on macOS/Linux | run `kinit` first on macOS/Linux |
| `entraInteractive` | Browser sign-in | **MFA / Conditional Access**. Token cached in the OS keychain, so you are not prompted every run |
| `entraDeviceCode` | Code shown in the terminal, sign in on any device | **MFA**; works over SSH |
| `entraDefault` | DefaultAzureCredential chain | environment, workload identity, managed identity, VS/VS Code, Azure CLI, PowerShell, azd |
| `entraAzureCli` / `entraAzurePowerShell` / `entraAzureDeveloperCli` | The account signed in to that tool | MFA happens in that tool |
| `entraManagedIdentity` | System-assigned, or user-assigned by `clientId`, `managedIdentityObjectId` or `managedIdentityResourceId` | Azure VMs, App Service, Functions, AKS, ACI |
| `entraServicePrincipal` | `tenantId`, `clientId`, `clientSecret` | |
| `entraServicePrincipalCertificate` | `certificatePath` (PFX/PEM, optional `certificatePassword`) or `certificateThumbprint` (Windows store); `sendCertificateChain` for SN+I | |
| `entraWorkloadIdentity` | Federated token (AKS, GitHub Actions via `AZURE_FEDERATED_TOKEN_FILE`) | |
| `entraAzurePipelines` | Azure DevOps service connection: `serviceConnectionId`, `systemAccessToken` | |
| `entraEnvironment` | `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` or `AZURE_CLIENT_CERTIFICATE_PATH` | |
| `entraAccessToken` | A token you already have: `accessToken: ${env:SQL_TOKEN}` | |
| `entraIntegrated` | Federated-domain Entra Integrated (native SqlClient) | |
| `entraPassword` | Entra user + password (native SqlClient) | no MFA; deprecated by Microsoft; prints a warning |

Other `entra` options: `authorityHost` (`azurePublicCloud`, `azureGovernment`, `azureChina`, or a URL), `additionallyAllowedTenants`, `loginHint`, `tokenCache` (`persistent` / `memory`), `allowUnencryptedTokenCache` (Linux without libsecret), `scope` (override the token audience), `timeoutSeconds`. `badger auth clear` forgets saved interactive sign-ins. `badger doctor` acquires a token before connecting, so identity problems and network problems are reported separately.

A raw `connectionString` with `Authentication=Active Directory ...` also works; SqlClient's own Entra provider is registered.

## Connection properties (Azure SQL and everything else)

Any SqlClient keyword works under `connectionProperties`, e.g. `Connect Retry Count`, `Connect Retry Interval`, `Connect Timeout`, `Command Timeout`, `ApplicationIntent`, `Multi Subnet Failover`, `Failover Partner`, `Server SPN`, `IP Address Preference`, `Transparent Network IP Resolution`, `Pooling`, `Max Pool Size`, `Packet Size`, `Application Name`, `Workstation ID`, `Current Language`, `Host Name In Certificate`, `Server Certificate`, `Attestation Protocol`, `Enclave Attestation Url`. Unknown or invalid keywords are reported by `badger doctor` and before connecting.

## Always Encrypted keys per environment

Change scripts reference **stable key names**; each environment supplies its own keys:

```sql
-- changes/add-ssn/deploy.sql (identical in every environment)
ALTER TABLE dbo.Customer ADD Ssn char(11) COLLATE Latin1_General_BIN2
    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_Customer, ENCRYPTION_TYPE = Deterministic,
                    ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NULL;
```

```yaml
environments:
  test:
    alwaysEncrypted:
      columnMasterKeys:
        CMK_Test: { keyStore: AZURE_KEY_VAULT, keyPath: "https://app-test-kv.vault.azure.net/keys/cmk/0123..." }
      columnEncryptionKeys:
        CEK_Customer: { columnMasterKey: CMK_Test }
  prod:
    alwaysEncrypted:
      attestation: { protocol: AAS, url: "https://app.weu.attest.azure.net/attest/SgxEnclave" }   # enclaves only
      keyStores:
        azureKeyVault:
          authentication: entraManagedIdentity   # optional: a different identity for Key Vault
          trustedEndpoints: [vault.azure.net, managedhsm.azure.net]
        custom:
          - { name: CONTOSO_HSM, assembly: tools/Contoso.KeyStore.dll, type: Contoso.KeyStore.HsmProvider }
      columnMasterKeys:
        CMK_Prod_2026: { keyStore: AZURE_KEY_VAULT, keyPath: "https://app-prod-kv.vault.azure.net/keys/cmk/abcd...", enclaveComputations: true }
      columnEncryptionKeys:
        CEK_Customer: { columnMasterKey: CMK_Prod_2026 }
```

- `keyStore`: `AZURE_KEY_VAULT` (Key Vault and Managed HSM), `MSSQL_CERTIFICATE_STORE`, `MSSQL_CNG_STORE`, `MSSQL_CSP_PROVIDER` (the last three are Windows only), or a custom provider name. Aliases: `azureKeyVault`, `certificateStore`, `cng`, `csp`.
- Before changes run, `deploy` creates missing column master keys (signed when `enclaveComputations: true`) and column encryption keys (a new random 256-bit key, encrypted by the CMK on the client; key material is never logged).
- **Rotation:** add a new CMK and point the CEK at it. The next deploy adds a CEK value protected by the new CMK (the key is decrypted with the old CMK and re-encrypted). Once every client can reach the new CMK, run `badger env provision --allow-destructive` to drop the old value.
- A CMK whose key path differs from the database is a **conflict**: SQL Server cannot alter CMK metadata, so define a new name and rotate.
- `badger env status` shows each key as in sync, to create, to rotate, destructive, or conflicting.
- Schema fingerprints include the CEK name of encrypted columns, never key paths, so environments with different keys still fingerprint identically.
- Library users can register a provider instance with `SqlServerProvider.RegisterKeyStoreProvider(name, provider)`.

## Variables

Scripts are preprocessed before they reach the database. Raw scripts are what get hashed and certified; the rendered SQL is hashed separately per environment (`rendered_hash` in the journal).

| Syntax | Meaning |
|---|---|
| `{name}`, `{group.name}`, `{var.name}` | variable |
| `$(name)` | same variables, sqlcmd syntax |
| `{{name}}` | literal `{name}` |
| `-- badger:preprocess off` (first line) | leave this script untouched |

Only an identifier directly inside braces is a token, so ODBC escapes (`{fn NOW()}`), JSON (`'{"a":1}'`) and format strings (`'{0}'`) are left alone.

**Built-ins** (reserved names):

| Variable | Value |
|---|---|
| `{env}`, `{env.name}`, `{env.description}` | environment |
| `{db}`, `{db.name}`, `{db.host}`, `{db.port}` | target database |
| `{change}`, `{change.name}`, `{change.kind}`, `{change.hash}` | current change/repeatable, `deploy`/`revert`/`verify`/`repeatable`, script hash |
| `{badger.provider}`, `{badger.version}`, `{badger.schema}` | tool facts |
| `{plan.head}` | plan certificate |
| `{deploy.user}`, `{deploy.host}`, `{deploy.utc}`, `{deploy.date}`, `{deploy.id}` | per-run values (scripts using them are not rendered-hash tracked) |

**User variables** come from `variables:` (shared), the environment's `variables:` and `--var name=value` (highest precedence). Nested maps become dotted names. Values may reference other variables and built-ins (`calc: calc_{env}`). In inline YAML maps (`{ a: b }`) quote values that contain braces, or use block style.

**Safety:**
- An unknown variable is an error with its line number (unless `preprocessor.unknown: keep`).
- `badger plan check` fails when a script uses a variable that some environment does not define.
- `badger verify` reports changes that would now render differently than what was deployed.
- `revert` refuses when the stored revert script now renders differently (`--allow-variable-drift` to override).
- `badger vars -e prod` lists every variable with its value and source. `badger render <change> -e prod [--kind revert]` prints the exact SQL.
