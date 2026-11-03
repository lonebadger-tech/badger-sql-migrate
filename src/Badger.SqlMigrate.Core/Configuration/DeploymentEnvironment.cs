using System.Text.Json.Nodes;

namespace Badger.SqlMigrate.Core.Configuration;

/// <summary>
/// How the tool authenticates. Entra ID kinds are vendor-neutral (Azure SQL, Azure Database for PostgreSQL
/// and MySQL all accept Entra access tokens); providers reject kinds they cannot use.
/// </summary>
public enum AuthenticationKind
{
    /// <summary>User name and password (SQL Server login, PostgreSQL role, ...).</summary>
    Password,

    /// <summary>Windows/Integrated auth: SSPI on Windows, Kerberos (GSSAPI) on macOS/Linux.</summary>
    Integrated,

    /// <summary>DefaultAzureCredential chain: environment, workload identity, managed identity, VS/VS Code, Azure CLI, PowerShell, azd.</summary>
    EntraDefault,

    /// <summary>Browser sign-in with full MFA / Conditional Access support. Tokens are cached so MFA is not repeated every run.</summary>
    EntraInteractive,

    /// <summary>Device code flow (MFA-capable) for terminals without a browser, e.g. SSH sessions.</summary>
    EntraDeviceCode,

    /// <summary>System-assigned or user-assigned (client id, object id or resource id) managed identity.</summary>
    EntraManagedIdentity,

    /// <summary>App registration with a client secret.</summary>
    EntraServicePrincipal,

    /// <summary>App registration with a certificate (PFX/PEM file, or Windows certificate store thumbprint).</summary>
    EntraServicePrincipalCertificate,

    /// <summary>Kubernetes / federated workload identity (AZURE_FEDERATED_TOKEN_FILE).</summary>
    EntraWorkloadIdentity,

    /// <summary>The account signed in to the Azure CLI ('az login', including MFA).</summary>
    EntraAzureCli,

    /// <summary>The account signed in to Azure PowerShell ('Connect-AzAccount').</summary>
    EntraAzurePowerShell,

    /// <summary>The account signed in to the Azure Developer CLI ('azd auth login').</summary>
    EntraAzureDeveloperCli,

    /// <summary>Azure Pipelines service connection with workload identity federation.</summary>
    EntraAzurePipelines,

    /// <summary>EnvironmentCredential: AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET or AZURE_CLIENT_CERTIFICATE_PATH.</summary>
    EntraEnvironment,

    /// <summary>A pre-acquired access token, typically from ${env:...} in a pipeline.</summary>
    EntraAccessToken,

    /// <summary>Entra Integrated (federated domain, Windows/Kerberos). Handled natively by the provider's driver.</summary>
    EntraIntegrated,

    /// <summary>Entra user name and password. No MFA; deprecated by Microsoft; handled natively by the provider's driver.</summary>
    EntraPassword,
}

public enum TlsMode
{
    /// <summary>Provider default.</summary>
    Default,
    Disabled,
    Optional,
    Mandatory,

    /// <summary>TLS before any protocol traffic (TDS 8.0 on SQL Server).</summary>
    Strict,
}

/// <summary>Ordering policy for changes that land in the plan before changes already applied to the database.</summary>
public enum OrderingPolicy
{
    /// <summary>Apply out-of-order changes if their dependencies are satisfied (shared dev databases).</summary>
    Dependency,

    /// <summary>Refuse; the database must receive changes in exact plan order (production).</summary>
    Strict,
}

public enum TokenCacheMode
{
    /// <summary>Encrypted OS store (Keychain, DPAPI, libsecret) so interactive/MFA sign-in survives between runs.</summary>
    Persistent,

    /// <summary>Per-process only; every run signs in again.</summary>
    Memory,
}

public sealed record TlsOptions
{
    public TlsMode Mode { get; init; } = TlsMode.Default;

    public bool TrustServerCertificate { get; init; }

    public string? HostNameInCertificate { get; init; }

    /// <summary>Path to the expected server certificate (pinning).</summary>
    public string? ServerCertificate { get; init; }
}

/// <summary>Entra ID (Azure AD) settings; which fields apply depends on <see cref="AuthenticationKind"/>.</summary>
public sealed record EntraOptions
{
    public string? TenantId { get; init; }

    /// <summary>App (client) id: service principal, user-assigned managed identity, or a custom public client for interactive sign-in.</summary>
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    /// <summary>PFX or PEM file for certificate-based service principals.</summary>
    public string? CertificatePath { get; init; }

    public string? CertificatePassword { get; init; }

    /// <summary>Windows only: certificate thumbprint in CurrentUser\My or LocalMachine\My.</summary>
    public string? CertificateThumbprint { get; init; }

    /// <summary>Send the certificate chain (x5c) for subject-name/issuer authentication.</summary>
    public bool SendCertificateChain { get; init; }

    /// <summary>User-assigned managed identity by object (principal) id.</summary>
    public string? ManagedIdentityObjectId { get; init; }

    /// <summary>User-assigned managed identity by ARM resource id.</summary>
    public string? ManagedIdentityResourceId { get; init; }

    /// <summary>Pre-selects the account in the browser/device-code prompt.</summary>
    public string? LoginHint { get; init; }

    /// <summary>"azurePublicCloud" (default), "azureGovernment", "azureChina", or an authority URL.</summary>
    public string? AuthorityHost { get; init; }

    public IReadOnlyList<string> AdditionallyAllowedTenants { get; init; } = [];

    public TokenCacheMode TokenCache { get; init; } = TokenCacheMode.Persistent;

    /// <summary>Linux without libsecret: allow an unencrypted token cache file. Off by default.</summary>
    public bool AllowUnencryptedTokenCache { get; init; }

    /// <summary>Pre-acquired token for <see cref="AuthenticationKind.EntraAccessToken"/>.</summary>
    public string? AccessToken { get; init; }

    /// <summary>Override the token scope (default: the resource the server asks for + "/.default").</summary>
    public string? Scope { get; init; }

    /// <summary>Azure Pipelines: service connection id (EntraAzurePipelines).</summary>
    public string? ServiceConnectionId { get; init; }

    /// <summary>Azure Pipelines: $(System.AccessToken) (EntraAzurePipelines).</summary>
    public string? SystemAccessToken { get; init; }

    /// <summary>Seconds to wait for interactive sign-in / CLI credential processes.</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>SSH tunnel to reach a database through a bastion host. Opened by Badger.SqlMigrate.Connectivity, vendor-neutral.</summary>
public sealed record SshOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string User { get; init; }

    public string? PrivateKeyFile { get; init; }

    public string? PrivateKeyPassphrase { get; init; }

    public string? Password { get; init; }

    /// <summary>known_hosts file used to verify the bastion's host key. Defaults to ~/.ssh/known_hosts.</summary>
    public string? KnownHostsFile { get; init; }

    /// <summary>Expected host key fingerprint ("SHA256:..."). Takes precedence over known_hosts.</summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>Skip host key verification. Only for throwaway environments; logs a warning.</summary>
    public bool InsecureSkipHostKeyCheck { get; init; }
}

/// <summary>
/// A named deployment environment from bsm.yaml (dev, test, staging, prod, ...): where to connect, how to
/// authenticate, and the policies that apply there. Vendor-specific blocks (e.g. SQL Server Always Encrypted keys)
/// arrive untyped in <see cref="ProviderSettings"/> and are interpreted only by the provider.
/// Secrets are already resolved by the time an environment reaches a provider.
/// </summary>
public sealed record DeploymentEnvironment
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Raw driver connection string. Host/Port/Database/auth fields and <see cref="ConnectionProperties"/> override it.</summary>
    public string? ConnectionString { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? Database { get; init; }

    public AuthenticationKind? Authentication { get; init; }

    public string? User { get; init; }

    public string? Password { get; init; }

    public EntraOptions Entra { get; init; } = new();

    public TlsOptions Tls { get; init; } = new();

    public SshOptions? Ssh { get; init; }

    /// <summary>Any driver connection-string keyword (e.g. "Max Pool Size", "Server SPN"), applied last.</summary>
    public IReadOnlyDictionary<string, string> ConnectionProperties { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public OrderingPolicy Ordering { get; init; } = OrderingPolicy.Dependency;

    /// <summary>Deploy/revert/baseline/provision ask for the environment name to be typed (or --yes).</summary>
    public bool Protected { get; init; }

    /// <summary>When false, revert and rebase are refused for this environment.</summary>
    public bool AllowRevert { get; init; } = true;

    /// <summary>Schema (or equivalent namespace) that holds the bsm state tables.</summary>
    public string StateSchema { get; init; } = "bsm";

    public int? CommandTimeoutSeconds { get; init; }

    public int LockTimeoutSeconds { get; init; } = 300;

    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Provider-specific settings (e.g. "alwaysEncrypted"), untyped. Strings have secrets resolved.</summary>
    public JsonObject ProviderSettings { get; init; } = [];
}
