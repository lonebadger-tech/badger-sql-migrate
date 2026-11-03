using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// The "alwaysEncrypted:" block of an environment. Change scripts reference stable key names
/// (ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_Customer ...)); each environment maps those names to its own
/// column master key (key store + key path) and badger provisions the key metadata and key material.
/// <code>
/// alwaysEncrypted:
///   enabled: true                      # sets Column Encryption Setting=Enabled
///   attestation: { protocol: AAS, url: https://... }
///   keyStores:
///     azureKeyVault:
///       authentication: entraAzureCli  # optional; defaults to the environment's Entra identity, else entraDefault
///       trustedEndpoints: [vault.azure.net, managedhsm.azure.net]
///     custom:
///       - { name: CONTOSO_HSM, assembly: tools/Contoso.KeyStore.dll, type: Contoso.KeyStore.HsmProvider }
///   columnMasterKeys:
///     CMK_2026:
///       keyStore: AZURE_KEY_VAULT
///       keyPath: https://prod-kv.vault.azure.net/keys/cmk-2026/0123456789abcdef
///       enclaveComputations: false
///   columnEncryptionKeys:
///     CEK_Customer: { columnMasterKey: CMK_2026 }
/// </code>
/// </summary>
public sealed class AlwaysEncryptedSettings
{
    public const string SectionName = "alwaysEncrypted";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public bool Enabled { get; set; } = true;

    public AttestationSettings? Attestation { get; set; }

    public KeyStoreSettings KeyStores { get; set; } = new();

    public Dictionary<string, ColumnMasterKeySettings> ColumnMasterKeys { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, ColumnEncryptionKeySettings> ColumnEncryptionKeys { get; set; } = new(StringComparer.Ordinal);

    public static AlwaysEncryptedSettings? From(DeploymentEnvironment environment)
    {
        if (!environment.ProviderSettings.TryGetPropertyValue(SectionName, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize<AlwaysEncryptedSettings>(JsonOptions) ?? new AlwaysEncryptedSettings();
        }
        catch (JsonException ex)
        {
            throw new MigrationException($"environment '{environment.Name}': invalid {SectionName} settings: {ex.Message}", ex);
        }
    }

    /// <summary>Unknown top-level provider settings are reported so typos do not silently disable features.</summary>
    public static IEnumerable<string> UnknownSections(JsonObject settings) =>
        settings.Select(kv => kv.Key).Where(k => !string.Equals(k, SectionName, StringComparison.OrdinalIgnoreCase));
}

public sealed class AttestationSettings
{
    /// <summary>AAS (Azure Attestation), HGS (Host Guardian Service) or None (VBS enclaves without attestation).</summary>
    public string Protocol { get; set; } = "AAS";

    public string? Url { get; set; }
}

public sealed class KeyStoreSettings
{
    public AzureKeyVaultSettings? AzureKeyVault { get; set; }

    public List<CustomKeyStoreSettings> Custom { get; set; } = [];
}

public sealed class AzureKeyVaultSettings
{
    /// <summary>Entra flow for Key Vault access; defaults to the environment's own Entra identity, else entraDefault.</summary>
    public AuthenticationKind? Authentication { get; set; }

    public EntraOptions? Entra { get; set; }

    /// <summary>Allowed Key Vault / Managed HSM host suffixes (sovereign clouds, private endpoints).</summary>
    public List<string> TrustedEndpoints { get; set; } = [];
}

public sealed class CustomKeyStoreSettings
{
    /// <summary>KEY_STORE_PROVIDER_NAME, e.g. CONTOSO_HSM. Clients reading the data must register the same name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Path to the assembly (relative to the project directory) containing the provider.</summary>
    public string Assembly { get; set; } = string.Empty;

    /// <summary>Type deriving from Microsoft.Data.SqlClient.SqlColumnEncryptionKeyStoreProvider with a public parameterless constructor.</summary>
    public string Type { get; set; } = string.Empty;
}

public sealed class ColumnMasterKeySettings
{
    /// <summary>AZURE_KEY_VAULT, MSSQL_CERTIFICATE_STORE, MSSQL_CNG_STORE, MSSQL_CSP_PROVIDER or a custom store name (aliases: azureKeyVault, certificateStore, cng, csp).</summary>
    public string KeyStore { get; set; } = "AZURE_KEY_VAULT";

    /// <summary>Key Vault key URL, "CurrentUser/My/&lt;thumbprint&gt;", CNG/CSP "provider/key", or custom path.</summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>Allow secure-enclave computations; the CMK metadata is signed with the key.</summary>
    public bool EnclaveComputations { get; set; }
}

public sealed class ColumnEncryptionKeySettings
{
    /// <summary>The CMK that should protect this CEK in this environment. Changing it rotates the CEK to the new CMK.</summary>
    public string ColumnMasterKey { get; set; } = string.Empty;
}
