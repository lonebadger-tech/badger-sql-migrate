using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Providers;
using Npgsql;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>
/// Builds Npgsql connections from a vendor-neutral environment: host/port/database/user, TLS (sslmode),
/// password, Kerberos (GSS) and Entra ID tokens (Azure Database for PostgreSQL), SSH tunnels, and any Npgsql
/// connection-string keyword under connectionProperties (applied last).
/// </summary>
public static class PostgreSqlConnectionFactory
{
    /// <summary>The Entra resource Azure Database for PostgreSQL accepts tokens for.</summary>
    public const string EntraScope = "https://ossrdbms-aad.database.windows.net/.default";

    public static readonly IReadOnlySet<AuthenticationKind> Supported = new HashSet<AuthenticationKind>
    {
        AuthenticationKind.Password,
        AuthenticationKind.Integrated,
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
                _ = new NpgsqlConnectionStringBuilder(environment.ConnectionString);
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
                _ = new NpgsqlConnectionStringBuilder { [key] = value }.ConnectionString;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add($"environment '{name}': connectionProperties '{key}': {ex.Message}");
            }
        }

        if (environment.Authentication is { } kind && !Supported.Contains(kind))
        {
            problems.Add($"environment '{name}': authentication '{kind}' is not available for PostgreSQL (it is specific to SQL Server). Use password, integrated (Kerberos), or an Entra token flow such as entraDefault.");
        }

        var tokenBased = environment.Authentication is { } k && k.ToString().StartsWith("Entra", StringComparison.Ordinal);
        if ((environment.Authentication == AuthenticationKind.Password || tokenBased) && string.IsNullOrEmpty(environment.User) && !HasUser(environment.ConnectionString))
        {
            problems.Add(tokenBased
                ? $"environment '{name}': Entra sign-in to PostgreSQL needs 'user': the Entra user, group or managed identity name the server knows."
                : $"environment '{name}': password authentication requires 'user'.");
        }

        if (environment.Tls.HostNameInCertificate is not null)
        {
            problems.Add($"environment '{name}': tls.hostNameInCertificate is not supported by PostgreSQL; with an SSH tunnel use tls.mode 'mandatory' (encrypt without host-name check) or connect by the certificate's host name.");
        }

        foreach (var key in environment.ProviderSettings.Select(kv => kv.Key).Where(k => !string.Equals(k, PostgreSqlFingerprintSettings.SectionName, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"environment '{name}': unknown PostgreSQL setting '{key}'. Known: {PostgreSqlFingerprintSettings.SectionName}.");
        }

        try
        {
            PostgreSqlFingerprintSettings.From(environment);
        }
        catch (MigrationException ex)
        {
            problems.Add(ex.Message);
        }

        return problems;
    }

    public static Endpoint ResolveEndpoint(DeploymentEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            return new Endpoint(environment.Host, environment.Port ?? 5432);
        }

        var builder = new NpgsqlConnectionStringBuilder(environment.ConnectionString ?? string.Empty);
        var host = (builder.Host ?? "localhost").Split(',')[0];
        var colon = host.LastIndexOf(':');
        return colon > 0 && int.TryParse(host[(colon + 1)..], out var port) ? new Endpoint(host[..colon], port) : new Endpoint(host, builder.Port);
    }

    public static string? ResolveDatabaseName(DeploymentEnvironment environment) =>
        !string.IsNullOrWhiteSpace(environment.Database) ? environment.Database
        : environment.ConnectionProperties.TryGetValue("Database", out var db) ? db
        : environment.ConnectionString is null ? null : new NpgsqlConnectionStringBuilder(environment.ConnectionString).Database;

    /// <summary>The connection string to use and a description without secrets.</summary>
    public static async Task<(NpgsqlConnectionStringBuilder Builder, string Description)> BuildAsync(DeploymentEnvironment environment, SessionContext context, CancellationToken cancellationToken)
    {
        var b = environment.ConnectionString is null ? new NpgsqlConnectionStringBuilder() : new NpgsqlConnectionStringBuilder(environment.ConnectionString);
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            b.Host = environment.Host;
            b.Port = environment.Port ?? 5432;
        }

        if (!string.IsNullOrWhiteSpace(environment.Database))
        {
            b.Database = environment.Database;
        }

        var target = ResolveEndpoint(environment);
        if (context.EndpointOverride is { } tunnel)
        {
            b.Host = tunnel.Host;
            b.Port = tunnel.Port;
        }

        if (!string.IsNullOrEmpty(environment.User))
        {
            b.Username = environment.User;
        }

        switch (environment.Authentication)
        {
            case AuthenticationKind.Password:
                b.Password = environment.Password;
                break;
            case AuthenticationKind.Integrated:
                b.Password = null;
                break;
            case { } kind when kind.ToString().StartsWith("Entra", StringComparison.Ordinal):
                var tokens = context.Tokens ?? throw new MigrationException($"environment '{environment.Name}': {kind} needs a token source, but none was supplied.");
                b.Password = (await tokens.GetTokenAsync([EntraScope], cancellationToken).ConfigureAwait(false)).Token;
                b.SslMode = b.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer ? SslMode.Require : b.SslMode;
                break;
        }

        b.SslMode = environment.Tls.Mode switch
        {
            TlsMode.Disabled => SslMode.Disable,
            TlsMode.Optional => SslMode.Prefer,
            TlsMode.Mandatory => environment.Tls.ServerCertificate is null ? SslMode.Require : SslMode.VerifyCA,
            TlsMode.Strict => SslMode.VerifyFull,
            _ => b.SslMode,
        };
        if (environment.Tls.ServerCertificate is { } rootCertificate)
        {
            b.RootCertificate = rootCertificate;
        }

        if (string.IsNullOrEmpty(b.ApplicationName))
        {
            b.ApplicationName = "Badger SQL Migrate";
        }

        foreach (var (key, value) in environment.ConnectionProperties)
        {
            b[key] = value;
        }

        var description = $"postgresql://{target.Host}:{target.Port}/{b.Database}{(context.EndpointOverride is null ? string.Empty : " (via ssh)")}";
        return (b, description);
    }

    private static bool HasUser(string? connectionString) =>
        connectionString is not null && !string.IsNullOrEmpty(new NpgsqlConnectionStringBuilder(connectionString).Username);
}
