using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Microsoft.Data.SqlClient;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// Builds SqlClient connections from an environment: SQL logins, Windows/Kerberos integrated auth, every Entra ID
/// flow (token flows via the host's <see cref="IAccessTokenProvider"/>; Entra Integrated/Password natively),
/// Always Encrypted key stores, TLS, and any SqlClient connection-string keyword via connectionProperties.
/// </summary>
public static class SqlServerConnectionFactory
{
    static SqlServerConnectionFactory()
    {
        // Lets raw connection strings use "Authentication=Active Directory ..." and powers entraIntegrated/entraPassword.
        var provider = new ActiveDirectoryAuthenticationProvider(info =>
        {
            Console.Error.WriteLine(info.Message);
            return Task.CompletedTask;
        });
        foreach (var method in new[]
        {
            SqlAuthenticationMethod.ActiveDirectoryIntegrated,
#pragma warning disable CS0618 // Deprecated by Microsoft but still offered (entraPassword warns at runtime).
            SqlAuthenticationMethod.ActiveDirectoryPassword,
#pragma warning restore CS0618
            SqlAuthenticationMethod.ActiveDirectoryInteractive,
            SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow,
            SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
            SqlAuthenticationMethod.ActiveDirectoryManagedIdentity,
            SqlAuthenticationMethod.ActiveDirectoryMSI,
            SqlAuthenticationMethod.ActiveDirectoryDefault,
            SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity,
        })
        {
            if (SqlAuthenticationProvider.GetProvider(method) is null)
            {
                SqlAuthenticationProvider.SetProvider(method, provider);
            }
        }
    }

    public static IReadOnlyList<string> Validate(DeploymentEnvironment environment)
    {
        var problems = new List<string>();
        var name = environment.Name;
        if (environment.ConnectionString is null && string.IsNullOrWhiteSpace(environment.Host))
        {
            problems.Add($"environment '{name}': set either 'connectionString' or 'host'.");
        }

        if (environment.ConnectionString is not null)
        {
            try
            {
                _ = new SqlConnectionStringBuilder(environment.ConnectionString);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"environment '{name}': invalid connectionString: {ex.Message}");
            }
        }

        foreach (var (key, value) in environment.ConnectionProperties)
        {
            try
            {
                var probe = new SqlConnectionStringBuilder { [key] = value };
                _ = probe.ConnectionString;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
            {
                problems.Add($"environment '{name}': connectionProperties '{key}': {ex.Message}");
            }
        }

        var entra = environment.Entra;
        switch (environment.Authentication)
        {
            case AuthenticationKind.Password when string.IsNullOrEmpty(environment.User):
                problems.Add($"environment '{name}': password authentication requires 'user'.");
                break;
            case AuthenticationKind.EntraPassword when string.IsNullOrEmpty(environment.User):
                problems.Add($"environment '{name}': entraPassword requires 'user' (user@domain) and 'password'.");
                break;
            case AuthenticationKind.EntraServicePrincipal when entra.ClientId is null || entra.ClientSecret is null || entra.TenantId is null:
                problems.Add($"environment '{name}': entraServicePrincipal requires entra.clientId, entra.clientSecret and entra.tenantId.");
                break;
            case AuthenticationKind.EntraServicePrincipalCertificate when entra.ClientId is null || entra.TenantId is null || (entra.CertificatePath is null && entra.CertificateThumbprint is null):
                problems.Add($"environment '{name}': entraServicePrincipalCertificate requires entra.clientId, entra.tenantId and entra.certificatePath or entra.certificateThumbprint.");
                break;
            case AuthenticationKind.EntraAccessToken when string.IsNullOrEmpty(entra.AccessToken):
                problems.Add($"environment '{name}': entraAccessToken requires entra.accessToken.");
                break;
            case AuthenticationKind.EntraAzurePipelines when entra.ServiceConnectionId is null || entra.SystemAccessToken is null || entra.ClientId is null || entra.TenantId is null:
                problems.Add($"environment '{name}': entraAzurePipelines requires entra.tenantId, entra.clientId, entra.serviceConnectionId and entra.systemAccessToken.");
                break;
        }

        return problems;
    }

    public static Endpoint ResolveEndpoint(DeploymentEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            return new Endpoint(environment.Host, environment.Port ?? 1433);
        }

        var dataSource = new SqlConnectionStringBuilder(environment.ConnectionString).DataSource;
        if (dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            dataSource = dataSource[4..];
        }

        var comma = dataSource.LastIndexOf(',');
        return comma > 0 && int.TryParse(dataSource[(comma + 1)..], out var port)
            ? new Endpoint(dataSource[..comma], port)
            : new Endpoint(dataSource, 1433);
    }

    public static (SqlConnection Connection, string Description, IReadOnlyDictionary<string, SqlColumnEncryptionKeyStoreProvider>? KeyStores) Create(DeploymentEnvironment environment, SessionContext context)
    {
        var builder = environment.ConnectionString is null ? new SqlConnectionStringBuilder() : new SqlConnectionStringBuilder(environment.ConnectionString);
        var target = ResolveEndpoint(environment);

        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            builder.DataSource = environment.Port is { } port ? $"{environment.Host},{port}" : environment.Host;
        }

        if (!string.IsNullOrWhiteSpace(environment.Database))
        {
            builder.InitialCatalog = environment.Database;
        }

        if (string.IsNullOrEmpty(builder.ApplicationName) || builder.ApplicationName == "Core Microsoft SqlClient Data Provider")
        {
            builder.ApplicationName = "Badger SQL Migrate";
        }

        ApplyTls(builder, environment.Tls);
        var alwaysEncrypted = AlwaysEncryptedSettings.From(environment);
        if (alwaysEncrypted is { Enabled: true })
        {
            builder.ColumnEncryptionSetting = SqlConnectionColumnEncryptionSetting.Enabled;
            if (alwaysEncrypted.Attestation is { } attestation)
            {
                builder.AttestationProtocol = Enum.Parse<SqlConnectionAttestationProtocol>(attestation.Protocol, ignoreCase: true);
                if (attestation.Url is not null)
                {
                    builder.EnclaveAttestationUrl = attestation.Url;
                }
            }
        }

        var useToken = false;
        switch (environment.Authentication)
        {
            case null:
                break;
            case AuthenticationKind.Password:
                builder.IntegratedSecurity = false;
                builder.UserID = environment.User;
                builder.Password = environment.Password ?? string.Empty;
                break;
            case AuthenticationKind.Integrated:
                // SSPI on Windows; GSSAPI/Kerberos on macOS and Linux (requires a ticket from kinit).
                builder.IntegratedSecurity = true;
                break;
            case AuthenticationKind.EntraIntegrated:
                builder.IntegratedSecurity = false;
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryIntegrated;
                if (environment.User is not null)
                {
                    builder.UserID = environment.User;
                }

                break;
            case AuthenticationKind.EntraPassword:
                builder.IntegratedSecurity = false;
#pragma warning disable CS0618 // Deprecated by Microsoft; kept for tenants that still need it.
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
#pragma warning restore CS0618
                builder.UserID = environment.User;
                builder.Password = environment.Password ?? string.Empty;
                context.Warn?.Invoke("entraPassword does not support MFA and is deprecated by Microsoft; prefer entraInteractive, entraDeviceCode or a service principal.");
                break;
            default:
                builder.IntegratedSecurity = false;
                builder.Remove("Authentication");
                builder.Remove("Password");
                useToken = true;
                break;
        }

        // Connection properties are the escape hatch for any SqlClient keyword and win over everything above.
        foreach (var (key, value) in environment.ConnectionProperties)
        {
            builder[key] = value;
        }

        if (context.EndpointOverride is { } tunnel)
        {
            builder.DataSource = $"{tunnel.Host},{tunnel.Port}";

            // Through a tunnel the TCP peer is localhost; validate TLS against the real server name.
            if (string.IsNullOrEmpty(builder.HostNameInCertificate))
            {
                builder.HostNameInCertificate = target.Host;
            }
        }

        var connection = new SqlConnection(builder.ConnectionString);
        if (useToken)
        {
            var tokens = context.Tokens ?? throw new MigrationException($"Authentication '{environment.Authentication}' needs an Entra token source; the host did not provide one.");
            connection.AccessTokenCallback = async (parameters, cancellationToken) =>
            {
                var scope = environment.Entra.Scope ?? parameters.Resource.TrimEnd('/') + "/.default";
                var token = await tokens.GetTokenAsync([scope], cancellationToken).ConfigureAwait(false);
                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            };
        }

        Dictionary<string, SqlColumnEncryptionKeyStoreProvider>? keyStores = null;
        if (alwaysEncrypted is not null)
        {
            keyStores = KeyStoreProviders.Build(alwaysEncrypted, environment, context);
            connection.RegisterColumnEncryptionKeyStoreProvidersOnConnection(keyStores);
        }

        var description = $"sqlserver://{target.Host}:{target.Port}/{builder.InitialCatalog}{(context.EndpointOverride is null ? string.Empty : " (via ssh)")}";
        return (connection, description, keyStores);
    }

    private static void ApplyTls(SqlConnectionStringBuilder builder, TlsOptions tls)
    {
        switch (tls.Mode)
        {
            case TlsMode.Disabled:
            case TlsMode.Optional:
                builder.Encrypt = SqlConnectionEncryptOption.Optional;
                break;
            case TlsMode.Mandatory:
                builder.Encrypt = SqlConnectionEncryptOption.Mandatory;
                break;
            case TlsMode.Strict:
                builder.Encrypt = SqlConnectionEncryptOption.Strict;
                break;
        }

        if (tls.TrustServerCertificate)
        {
            builder.TrustServerCertificate = true;
        }

        if (!string.IsNullOrEmpty(tls.HostNameInCertificate))
        {
            builder.HostNameInCertificate = tls.HostNameInCertificate;
        }

        if (!string.IsNullOrEmpty(tls.ServerCertificate))
        {
            builder.ServerCertificate = tls.ServerCertificate;
        }
    }
}
