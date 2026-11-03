using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Connectivity;

/// <summary>
/// Entra ID tokens via Azure.Identity for every flow, including MFA (browser and device code). Interactive
/// sign-ins use the OS-encrypted token cache plus a saved authentication record, so MFA is only needed when
/// the refresh token expires, not on every run. Vendor-neutral: any provider that accepts Entra tokens can use it.
/// </summary>
public sealed class EntraTokenProvider : IAccessTokenProvider
{
    private const string CacheName = "badger-sql-migrate";

    private readonly TokenCredential credential;
    private readonly EntraOptions options;
    private readonly Func<CancellationToken, Task>? signIn;
    private readonly SemaphoreSlim signInLock = new(1, 1);
    private bool signedIn;

    private EntraTokenProvider(TokenCredential credential, EntraOptions options, Func<CancellationToken, Task>? signIn)
    {
        this.credential = credential;
        this.options = options;
        this.signIn = signIn;
    }

    public string DefaultScope => options.Scope ?? "https://database.windows.net/.default";

    /// <summary>The underlying credential, for libraries that take an Azure TokenCredential.</summary>
    public TokenCredential Credential => credential;

    /// <summary>True for kinds this class serves; Password, Integrated, EntraIntegrated and EntraPassword are handled by drivers.</summary>
    public static bool IsTokenBased(AuthenticationKind? kind) => kind is not (null or AuthenticationKind.Password or AuthenticationKind.Integrated or AuthenticationKind.EntraIntegrated or AuthenticationKind.EntraPassword);

    /// <param name="cacheKey">Distinguishes saved sign-ins (typically the environment name).</param>
    /// <param name="prompt">Where to show device-code instructions and sign-in notices.</param>
    public static IAccessTokenProvider? Create(AuthenticationKind? kind, EntraOptions options, string cacheKey, Action<string> prompt)
    {
        if (!IsTokenBased(kind))
        {
            return null;
        }

        var authority = ResolveAuthority(options.AuthorityHost);
        var persistence = options.TokenCache == TokenCacheMode.Persistent
            ? new TokenCachePersistenceOptions { Name = CacheName, UnsafeAllowUnencryptedStorage = options.AllowUnencryptedTokenCache }
            : null;
        var timeout = options.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;

        switch (kind)
        {
            case AuthenticationKind.EntraAccessToken:
                return new StaticTokenProvider(options.AccessToken ?? throw new MigrationException("entraAccessToken requires entra.accessToken (e.g. ${env:SQL_ACCESS_TOKEN})."), options.Scope);

            case AuthenticationKind.EntraInteractive:
            {
                var recordPath = RecordPath(cacheKey, options);
                var browserOptions = new InteractiveBrowserCredentialOptions
                {
                    TenantId = options.TenantId,
                    ClientId = options.ClientId,
                    LoginHint = options.LoginHint,
                    AuthorityHost = authority,
                    TokenCachePersistenceOptions = persistence,
                    AuthenticationRecord = LoadRecord(recordPath),
                    DisableAutomaticAuthentication = false,
                };
                AddTenants(browserOptions.AdditionallyAllowedTenants, options);
                var browser = new InteractiveBrowserCredential(browserOptions);
                return new EntraTokenProvider(browser, options, browserOptions.AuthenticationRecord is not null || persistence is null ? null : async ct =>
                {
                    prompt("Opening a browser for Entra ID sign-in (MFA supported)...");
                    var record = await browser.AuthenticateAsync(new TokenRequestContext([options.Scope ?? "https://database.windows.net/.default"], tenantId: options.TenantId), ct).ConfigureAwait(false);
                    await SaveRecordAsync(recordPath, record, ct).ConfigureAwait(false);
                });
            }

            case AuthenticationKind.EntraDeviceCode:
            {
                var recordPath = RecordPath(cacheKey, options);
                var deviceOptions = new DeviceCodeCredentialOptions
                {
                    TenantId = options.TenantId,
                    ClientId = options.ClientId,
                    AuthorityHost = authority,
                    TokenCachePersistenceOptions = persistence,
                    AuthenticationRecord = LoadRecord(recordPath),
                    DeviceCodeCallback = (info, _) =>
                    {
                        prompt(info.Message);
                        return Task.CompletedTask;
                    },
                };
                AddTenants(deviceOptions.AdditionallyAllowedTenants, options);
                var device = new DeviceCodeCredential(deviceOptions);
                return new EntraTokenProvider(device, options, deviceOptions.AuthenticationRecord is not null || persistence is null ? null : async ct =>
                {
                    var record = await device.AuthenticateAsync(new TokenRequestContext([options.Scope ?? "https://database.windows.net/.default"], tenantId: options.TenantId), ct).ConfigureAwait(false);
                    await SaveRecordAsync(recordPath, record, ct).ConfigureAwait(false);
                });
            }
        }

        TokenCredential credential = kind switch
        {
            AuthenticationKind.EntraDefault => new DefaultAzureCredential(WithTenants(new DefaultAzureCredentialOptions
            {
                TenantId = options.TenantId,
                ManagedIdentityClientId = options.ClientId,
                AuthorityHost = authority,
                ExcludeInteractiveBrowserCredential = true,
                CredentialProcessTimeout = timeout,
            }, options)),
            AuthenticationKind.EntraManagedIdentity => new ManagedIdentityCredential(ManagedIdentity(options)),
            AuthenticationKind.EntraServicePrincipal => new ClientSecretCredential(
                Require(options.TenantId, "tenantId"),
                Require(options.ClientId, "clientId"),
                Require(options.ClientSecret, "clientSecret"),
                WithTenants(new ClientSecretCredentialOptions { AuthorityHost = authority, TokenCachePersistenceOptions = persistence }, options)),
            AuthenticationKind.EntraServicePrincipalCertificate => new ClientCertificateCredential(
                Require(options.TenantId, "tenantId"),
                Require(options.ClientId, "clientId"),
                LoadCertificate(options),
                WithTenants(new ClientCertificateCredentialOptions { AuthorityHost = authority, SendCertificateChain = options.SendCertificateChain, TokenCachePersistenceOptions = persistence }, options)),
            AuthenticationKind.EntraWorkloadIdentity => new WorkloadIdentityCredential(WithTenants(new WorkloadIdentityCredentialOptions
            {
                TenantId = options.TenantId,
                ClientId = options.ClientId,
                AuthorityHost = authority,
            }, options)),
            AuthenticationKind.EntraAzureCli => new AzureCliCredential(WithTenants(new AzureCliCredentialOptions { TenantId = options.TenantId, ProcessTimeout = timeout }, options)),
            AuthenticationKind.EntraAzurePowerShell => new AzurePowerShellCredential(WithTenants(new AzurePowerShellCredentialOptions { TenantId = options.TenantId, ProcessTimeout = timeout }, options)),
            AuthenticationKind.EntraAzureDeveloperCli => new AzureDeveloperCliCredential(WithTenants(new AzureDeveloperCliCredentialOptions { TenantId = options.TenantId, ProcessTimeout = timeout }, options)),
            AuthenticationKind.EntraAzurePipelines => new AzurePipelinesCredential(
                Require(options.TenantId, "tenantId"),
                Require(options.ClientId, "clientId"),
                Require(options.ServiceConnectionId, "serviceConnectionId"),
                Require(options.SystemAccessToken, "systemAccessToken"),
                WithTenants(new AzurePipelinesCredentialOptions { AuthorityHost = authority }, options)),
            AuthenticationKind.EntraEnvironment => new EnvironmentCredential(new EnvironmentCredentialOptions { AuthorityHost = authority }),
            _ => throw new MigrationException($"Authentication '{kind}' is not an Entra token flow."),
        };

        return new EntraTokenProvider(credential, options, null);
    }

    public async Task<AccessTokenResult> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        using var timeout = options.TimeoutSeconds is { } seconds ? new CancellationTokenSource(TimeSpan.FromSeconds(seconds)) : null;
        using var linked = timeout is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var ct = linked?.Token ?? cancellationToken;

        if (signIn is not null && !signedIn)
        {
            await signInLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!signedIn)
                {
                    await signIn(ct).ConfigureAwait(false);
                    signedIn = true;
                }
            }
            finally
            {
                signInLock.Release();
            }
        }

        var requested = scopes.Count > 0 ? scopes.ToArray() : [DefaultScope];
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(requested, tenantId: options.TenantId), ct).ConfigureAwait(false);
            return new AccessTokenResult(token.Token, token.ExpiresOn);
        }
        catch (CredentialUnavailableException ex)
        {
            throw new MigrationException($"Entra ID credential unavailable: {ex.Message.Split('\n')[0]}", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new MigrationException($"Entra ID sign-in failed: {ex.Message.Split('\n')[0]}", ex);
        }
    }

    /// <summary>Forgets saved interactive/device-code sign-ins (token cache entries expire on their own).</summary>
    public static int ClearSavedSignIns()
    {
        var dir = AuthDirectory();
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        var files = Directory.GetFiles(dir, "*.json");
        foreach (var file in files)
        {
            File.Delete(file);
        }

        return files.Length;
    }

    internal static Uri? ResolveAuthority(string? host) => host?.ToLowerInvariant() switch
    {
        null or "" or "azurepubliccloud" or "public" => null,
        "azuregovernment" or "usgov" => AzureAuthorityHosts.AzureGovernment,
        "azurechina" or "china" => AzureAuthorityHosts.AzureChina,
        _ => Uri.TryCreate(host, UriKind.Absolute, out var uri) ? uri : throw new MigrationException($"Unknown entra.authorityHost '{host}'. Use azurePublicCloud, azureGovernment, azureChina or a URL."),
    };

    private static ManagedIdentityId ManagedIdentity(EntraOptions o) =>
        o.ClientId is not null ? ManagedIdentityId.FromUserAssignedClientId(o.ClientId)
        : o.ManagedIdentityObjectId is not null ? ManagedIdentityId.FromUserAssignedObjectId(o.ManagedIdentityObjectId)
        : o.ManagedIdentityResourceId is not null ? ManagedIdentityId.FromUserAssignedResourceId(new ResourceIdentifier(o.ManagedIdentityResourceId))
        : ManagedIdentityId.SystemAssigned;

    private static X509Certificate2 LoadCertificate(EntraOptions o)
    {
        if (!string.IsNullOrEmpty(o.CertificateThumbprint))
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new MigrationException("entra.certificateThumbprint (certificate store lookup) is Windows only; use entra.certificatePath on macOS/Linux.");
            }

            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, o.CertificateThumbprint.Replace(" ", string.Empty, StringComparison.Ordinal), validOnly: false);
                if (found.Count > 0)
                {
                    return found[0];
                }
            }

            throw new MigrationException($"Certificate with thumbprint {o.CertificateThumbprint} not found in CurrentUser\\My or LocalMachine\\My.");
        }

        var path = SshTunnel.ExpandHome(Require(o.CertificatePath, "certificatePath or certificateThumbprint"));
        if (!File.Exists(path))
        {
            throw new MigrationException($"entra.certificatePath '{path}' does not exist.");
        }

        if (path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(o.CertificatePassword)
                ? X509Certificate2.CreateFromPemFile(path)
                : X509Certificate2.CreateFromEncryptedPemFile(path, o.CertificatePassword);
        }

        return X509CertificateLoader.LoadPkcs12FromFile(path, o.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
    }

    private static T WithTenants<T>(T credentialOptions, EntraOptions o)
        where T : TokenCredentialOptions
    {
        var list = credentialOptions switch
        {
            DefaultAzureCredentialOptions d => d.AdditionallyAllowedTenants,
            ClientSecretCredentialOptions c => c.AdditionallyAllowedTenants,
            ClientCertificateCredentialOptions c => c.AdditionallyAllowedTenants,
            WorkloadIdentityCredentialOptions w => w.AdditionallyAllowedTenants,
            AzureCliCredentialOptions a => a.AdditionallyAllowedTenants,
            AzurePowerShellCredentialOptions a => a.AdditionallyAllowedTenants,
            AzureDeveloperCliCredentialOptions a => a.AdditionallyAllowedTenants,
            AzurePipelinesCredentialOptions a => a.AdditionallyAllowedTenants,
            _ => null,
        };
        if (list is not null)
        {
            AddTenants(list, o);
        }

        return credentialOptions;
    }

    private static void AddTenants(IList<string> list, EntraOptions o)
    {
        foreach (var tenant in o.AdditionallyAllowedTenants)
        {
            list.Add(tenant);
        }
    }

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new MigrationException($"This Entra authentication mode requires entra.{name}.") : value;

    private static string AuthDirectory() =>
        Path.Combine(Environment.GetEnvironmentVariable("BADGER_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".badger"), "auth");

    private static string RecordPath(string cacheKey, EntraOptions o)
    {
        var key = string.Join('|', cacheKey, o.TenantId, o.ClientId, o.LoginHint, o.AuthorityHost);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(AuthDirectory(), hash + ".json");
    }

    private static AuthenticationRecord? LoadRecord(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return AuthenticationRecord.Deserialize(stream, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task SaveRecordAsync(string path, AuthenticationRecord record, CancellationToken ct)
    {
        // The record holds account/tenant identifiers only, no tokens; tokens stay in the encrypted OS cache.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await record.SerializeAsync(stream, ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed class StaticTokenProvider(string token, string? scope) : IAccessTokenProvider
    {
        public string DefaultScope => scope ?? "https://database.windows.net/.default";

        public Task<AccessTokenResult> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken) =>
            Task.FromResult(new AccessTokenResult(token, ReadExpiry(token)));

        /// <summary>Reads "exp" from a JWT so the driver knows when the token lapses; falls back to one hour.</summary>
        private static DateTimeOffset ReadExpiry(string jwt)
        {
            try
            {
                var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
                using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
                return DateTimeOffset.FromUnixTimeSeconds(doc.RootElement.GetProperty("exp").GetInt64());
            }
            catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                return DateTimeOffset.UtcNow.AddHours(1);
            }
        }
    }
}
