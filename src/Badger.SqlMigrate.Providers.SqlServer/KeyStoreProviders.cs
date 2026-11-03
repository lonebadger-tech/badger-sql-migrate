using System.Collections.Concurrent;
using System.Reflection;
using Azure.Core;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.AlwaysEncrypted.AzureKeyVaultProvider;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>Builds the Always Encrypted key store providers for an environment (built-in, Azure Key Vault and custom).</summary>
public static class KeyStoreProviders
{
    public const string AzureKeyVault = "AZURE_KEY_VAULT";
    public const string CertificateStore = "MSSQL_CERTIFICATE_STORE";
    public const string Cng = "MSSQL_CNG_STORE";
    public const string Csp = "MSSQL_CSP_PROVIDER";

    private static readonly ConcurrentDictionary<string, SqlColumnEncryptionKeyStoreProvider> Registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Library hook: register a key store provider instance (e.g. an HSM) for every environment in this process.</summary>
    public static void Register(string name, SqlColumnEncryptionKeyStoreProvider provider) => Registered[name] = provider;

    public static string Normalize(string keyStore) => keyStore.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
    {
        "azurekeyvault" or "akv" => AzureKeyVault,
        "certificatestore" or "mssqlcertificatestore" or "certstore" => CertificateStore,
        "cng" or "mssqlcngstore" => Cng,
        "csp" or "mssqlcspprovider" => Csp,
        _ => keyStore,
    };

    public static Dictionary<string, SqlColumnEncryptionKeyStoreProvider> Build(AlwaysEncryptedSettings settings, DeploymentEnvironment environment, SessionContext context)
    {
        var providers = new Dictionary<string, SqlColumnEncryptionKeyStoreProvider>(StringComparer.OrdinalIgnoreCase);

        // The Windows system stores, for provisioning and rotating keys. The driver always has its own copies,
        // so they're never registered on a connection (see ForConnection).
        if (OperatingSystem.IsWindows())
        {
            providers[CertificateStore] = new SqlColumnEncryptionCertificateStoreProvider();
            providers[Cng] = new SqlColumnEncryptionCngProvider();
            providers[Csp] = new SqlColumnEncryptionCspProvider();
        }

        var needsKeyVault = settings.KeyStores.AzureKeyVault is not null
            || settings.ColumnMasterKeys.Values.Any(k => Normalize(k.KeyStore) == AzureKeyVault);
        if (needsKeyVault)
        {
            providers[AzureKeyVault] = CreateKeyVaultProvider(settings.KeyStores.AzureKeyVault, environment, context);
        }

        foreach (var custom in settings.KeyStores.Custom)
        {
            providers[custom.Name] = LoadCustom(custom, context.BaseDirectory);
        }

        foreach (var (name, provider) in Registered)
        {
            providers[name] = provider;
        }

        return providers;
    }

    /// <summary>The providers to register on a connection: all but the system stores. The driver supplies those
    /// itself, ahead of any registered provider, and rejects registrations whose names start with MSSQL_.</summary>
    public static Dictionary<string, SqlColumnEncryptionKeyStoreProvider> ForConnection(IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider> providers) =>
        providers.Where(p => !IsSystemStore(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    public static bool IsSystemStore(string name) => name.StartsWith("MSSQL_", StringComparison.OrdinalIgnoreCase);

    public static SqlColumnEncryptionKeyStoreProvider Get(IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider> providers, string keyStore)
    {
        var name = Normalize(keyStore);
        if (providers.TryGetValue(name, out var provider))
        {
            return provider;
        }

        throw new MigrationException(name is CertificateStore or Cng or Csp && !OperatingSystem.IsWindows()
            ? $"Key store {name} is only available on Windows. Use AZURE_KEY_VAULT (or a custom key store) on macOS/Linux."
            : $"No key store provider named '{name}'. Configure alwaysEncrypted.keyStores (azureKeyVault or custom).");
    }

    private static SqlColumnEncryptionAzureKeyVaultProvider CreateKeyVaultProvider(AzureKeyVaultSettings? settings, DeploymentEnvironment environment, SessionContext context)
    {
        IAccessTokenProvider? tokens = null;
        if (settings?.Authentication is { } kind)
        {
            tokens = context.CreateTokenProvider?.Invoke(kind, settings.Entra ?? environment.Entra)
                ?? throw new MigrationException($"Key Vault authentication '{kind}' is not available in this host.");
        }
        else if (context.Tokens is not null)
        {
            tokens = context.Tokens;
        }
        else
        {
            tokens = context.CreateTokenProvider?.Invoke(AuthenticationKind.EntraDefault, environment.Entra)
                ?? throw new MigrationException("Azure Key Vault needs an Entra identity; set alwaysEncrypted.keyStores.azureKeyVault.authentication.");
        }

        var credential = new TokenProviderCredential(tokens);
        return settings?.TrustedEndpoints is { Count: > 0 } endpoints
            ? new SqlColumnEncryptionAzureKeyVaultProvider(credential, [.. endpoints])
            : new SqlColumnEncryptionAzureKeyVaultProvider(credential);
    }

    private static SqlColumnEncryptionKeyStoreProvider LoadCustom(CustomKeyStoreSettings custom, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(custom.Name) || string.IsNullOrWhiteSpace(custom.Assembly) || string.IsNullOrWhiteSpace(custom.Type))
        {
            throw new MigrationException("alwaysEncrypted.keyStores.custom entries need name, assembly and type.");
        }

        var path = Path.IsPathRooted(custom.Assembly) ? custom.Assembly : Path.GetFullPath(Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), custom.Assembly));
        if (!File.Exists(path))
        {
            throw new MigrationException($"Custom key store assembly '{path}' does not exist.");
        }

        var type = Assembly.LoadFrom(path).GetType(custom.Type, throwOnError: false)
            ?? throw new MigrationException($"Type '{custom.Type}' not found in '{path}'.");
        if (!typeof(SqlColumnEncryptionKeyStoreProvider).IsAssignableFrom(type))
        {
            throw new MigrationException($"'{custom.Type}' does not derive from SqlColumnEncryptionKeyStoreProvider.");
        }

        return (SqlColumnEncryptionKeyStoreProvider)(Activator.CreateInstance(type)
            ?? throw new MigrationException($"Could not create '{custom.Type}'."));
    }

    /// <summary>Adapts the vendor-neutral token source to Azure.Core for the Key Vault provider.</summary>
    private sealed class TokenProviderCredential(IAccessTokenProvider tokens) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var token = await tokens.GetTokenAsync(requestContext.Scopes, cancellationToken).ConfigureAwait(false);
            return new AccessToken(token.Token, token.ExpiresOn);
        }
    }
}
