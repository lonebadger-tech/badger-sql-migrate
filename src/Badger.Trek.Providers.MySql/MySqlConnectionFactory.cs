using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using MySqlConnector;

namespace Badger.Trek.Providers.MySql;

/// <summary>
/// Builds MySqlConnector connections from a vendor-neutral environment: host/port/database/user, TLS (SslMode),
/// password, Entra ID tokens (Azure Database for MySQL), SSH tunnels, and any MySqlConnector connection-string
/// keyword under connectionProperties (applied last).
/// </summary>
public static class MySqlConnectionFactory
{
    /// <summary>The Entra resource Azure Database for MySQL accepts tokens for.</summary>
    public const string EntraScope = "https://ossrdbms-aad.database.windows.net/.default";

    public const int DefaultPort = 3306;

    public static readonly IReadOnlySet<AuthenticationKind> Supported = new HashSet<AuthenticationKind>
    {
        AuthenticationKind.Password,
        AuthenticationKind.EntraDefault,
        AuthenticationKind.EntraInteractive,
        AuthenticationKind.EntraDeviceCode,
        AuthenticationKind.EntraManagedIdentity,
        AuthenticationKind.EntraServicePrincipal,
        AuthenticationKind.EntraServicePrincipalCertificate,
        AuthenticationKind.EntraWorkloadIdentity,
        AuthenticationKind.EntraAzureCli,
        AuthenticationKind.EntraAzurePowerShell,
        AuthenticationKind.EntraAzureDeveloperCli,
        AuthenticationKind.EntraAzurePipelines,
        AuthenticationKind.EntraEnvironment,
        AuthenticationKind.EntraAccessToken,
    };

    /// <summary>MariaDB has no managed service with Entra sign-in (Azure Database for MariaDB is retired): passwords only.</summary>
    public static readonly IReadOnlySet<AuthenticationKind> SupportedMariaDb = new HashSet<AuthenticationKind> { AuthenticationKind.Password };

    public static IReadOnlySet<AuthenticationKind> SupportedFor(MySqlFlavor flavor) => flavor == MySqlFlavor.MariaDb ? SupportedMariaDb : Supported;

    public static IReadOnlyList<string> Validate(DeploymentEnvironment environment, MySqlFlavor flavor = MySqlFlavor.MySql)
    {
        var product = flavor == MySqlFlavor.MariaDb ? "MariaDB" : "MySQL";
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
                _ = new MySqlConnectionStringBuilder(environment.ConnectionString);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"environment '{name}': invalid connectionString: {ex.Message}");
            }
        }

        if (ResolveDatabaseName(environment) is null or "")
        {
            problems.Add($"environment '{name}': set 'database': trek deploys to one {product} database (schema) and keeps its journal there.");
        }

        foreach (var (key, value) in environment.ConnectionProperties)
        {
            try
            {
                _ = new MySqlConnectionStringBuilder { [key] = value }.ConnectionString;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add($"environment '{name}': connectionProperties '{key}': {ex.Message}");
            }
        }

        if (environment.Authentication is { } kind && !SupportedFor(flavor).Contains(kind))
        {
            problems.Add($"environment '{name}': authentication '{kind}' is not available for {product}. " + (flavor == MySqlFlavor.MariaDb ? "Use password." : "Use password, or an Entra token flow such as entraDefault (Azure Database for MySQL)."));
        }

        var tokenBased = environment.Authentication is { } k && k.ToString().StartsWith("Entra", StringComparison.Ordinal);
        if ((environment.Authentication == AuthenticationKind.Password || tokenBased) && string.IsNullOrEmpty(environment.User) && !HasUser(environment.ConnectionString))
        {
            problems.Add(tokenBased
                ? $"environment '{name}': Entra sign-in to MySQL needs 'user': the Entra user, group or managed identity name the server knows."
                : $"environment '{name}': password authentication requires 'user'.");
        }

        if (environment.Tls.HostNameInCertificate is not null)
        {
            problems.Add($"environment '{name}': tls.hostNameInCertificate is not supported by {product}; with an SSH tunnel use tls.mode 'mandatory' (encrypt without host-name check) or connect by the certificate's host name.");
        }

        foreach (var key in environment.ProviderSettings.Select(kv => kv.Key))
        {
            problems.Add($"environment '{name}': unknown {product} setting '{key}'.");
        }

        return problems;
    }

    public static Endpoint ResolveEndpoint(DeploymentEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            return new Endpoint(environment.Host, environment.Port ?? DefaultPort);
        }

        var builder = new MySqlConnectionStringBuilder(environment.ConnectionString ?? string.Empty);
        var host = (string.IsNullOrEmpty(builder.Server) ? "localhost" : builder.Server).Split(',')[0];
        return new Endpoint(host, (int)builder.Port);
    }

    public static string? ResolveDatabaseName(DeploymentEnvironment environment) =>
        !string.IsNullOrWhiteSpace(environment.Database) ? environment.Database
        : environment.ConnectionProperties.TryGetValue("Database", out var db) ? db
        : environment.ConnectionString is null ? null : new MySqlConnectionStringBuilder(environment.ConnectionString).Database;

    /// <summary>The connection string to use and a description without secrets.</summary>
    public static async Task<(MySqlConnectionStringBuilder Builder, string Description)> BuildAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var b = environment.ConnectionString is null ? new MySqlConnectionStringBuilder() : new MySqlConnectionStringBuilder(environment.ConnectionString);
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            b.Server = environment.Host;
            b.Port = (uint)(environment.Port ?? DefaultPort);
        }

        if (!string.IsNullOrWhiteSpace(environment.Database))
        {
            b.Database = environment.Database;
        }

        var target = ResolveEndpoint(environment);
        if (context.EndpointOverride is { } tunnel)
        {
            b.Server = tunnel.Host;
            b.Port = (uint)tunnel.Port;
        }

        if (!string.IsNullOrEmpty(environment.User))
        {
            b.UserID = environment.User;
        }

        switch (environment.Authentication)
        {
            case AuthenticationKind.Password:
                b.Password = environment.Password;
                break;
            case { } kind when kind.ToString().StartsWith("Entra", StringComparison.Ordinal):
                // Azure Database for MySQL takes the token as the password, sent in clear text: only over TLS.
                var tokens = context.Tokens ?? throw new MigrationException($"environment '{environment.Name}': {kind} needs a token source, but none was supplied.");
                b.Password = (await tokens.GetTokenAsync([EntraScope], cancellationToken).ConfigureAwait(false)).Token;
                b.SslMode = b.SslMode is MySqlSslMode.None or MySqlSslMode.Preferred ? MySqlSslMode.Required : b.SslMode;
                break;
        }

        b.SslMode = environment.Tls.Mode switch
        {
            TlsMode.Disabled => MySqlSslMode.None,
            TlsMode.Optional => MySqlSslMode.Preferred,
            TlsMode.Mandatory => environment.Tls.ServerCertificate is null ? MySqlSslMode.Required : MySqlSslMode.VerifyCA,
            TlsMode.Strict => MySqlSslMode.VerifyFull,
            _ => b.SslMode,
        };
        if (environment.Tls.ServerCertificate is { } rootCertificate)
        {
            b.SslCa = rootCertificate;
        }

        // Scripts use @user variables; a deploy's own transaction must be the only one.
        b.AllowUserVariables = true;
        b.ApplicationName = string.IsNullOrEmpty(b.ApplicationName) ? "Badger Trek" : b.ApplicationName;
        foreach (var (key, value) in environment.ConnectionProperties)
        {
            b[key] = value;
        }

        var description = $"mysql://{target.Host}:{target.Port}/{b.Database}{(context.EndpointOverride is null ? string.Empty : " (via ssh)")}";
        return (b, description);
    }

    private static bool HasUser(string? connectionString) =>
        connectionString is not null && !string.IsNullOrEmpty(new MySqlConnectionStringBuilder(connectionString).UserID);
}
