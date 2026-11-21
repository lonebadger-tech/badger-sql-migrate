using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Oracle.ManagedDataAccess.Client;

namespace Badger.Trek.Providers.Oracle;

/// <summary>
/// Builds ODP.NET connections from a vendor-neutral environment: host, port and service name ('database'), or a
/// connectionString (EZConnect, a TNS alias or a full descriptor), user and password, TLS (TCPS), SSH tunnels, and any
/// ODP.NET connection-string keyword under connectionProperties (Wallet_Location, TNS_Admin ...), applied last. The
/// environment's 'schema' setting names the schema trek deploys to; it defaults to the user's own.
/// </summary>
public static partial class OracleConnectionFactory
{
    public const int DefaultPort = 1521;

    /// <summary>The environment setting that names the schema trek deploys to.</summary>
    public const string SchemaSetting = "schema";

    public static readonly IReadOnlySet<AuthenticationKind> Supported = new HashSet<AuthenticationKind> { AuthenticationKind.Password };

    public static IReadOnlyList<string> Validate(DeploymentEnvironment environment)
    {
        var problems = new List<string>();
        var name = environment.Name;
        if (environment.ConnectionString is null && string.IsNullOrWhiteSpace(environment.Host))
        {
            problems.Add($"environment '{name}': set either 'connectionString' or 'host' (with 'database': the service name, such as FREEPDB1).");
        }

        if (!string.IsNullOrWhiteSpace(environment.Host) && string.IsNullOrWhiteSpace(environment.Database))
        {
            problems.Add($"environment '{name}': set 'database' to the service name to connect to (a pluggable database such as FREEPDB1 or ORCLPDB1).");
        }

        if (environment.ConnectionString is not null)
        {
            try
            {
                _ = new OracleConnectionStringBuilder(environment.ConnectionString);
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
                _ = new OracleConnectionStringBuilder { [key] = value }.ConnectionString;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
            {
                problems.Add($"environment '{name}': connectionProperties '{key}': {ex.Message}");
            }
        }

        if (environment.Authentication is { } kind && !Supported.Contains(kind))
        {
            problems.Add($"environment '{name}': authentication '{kind}' is not available for Oracle. Use password (a wallet goes under connectionProperties: Wallet_Location).");
        }

        if (environment.Authentication == AuthenticationKind.Password && string.IsNullOrEmpty(environment.User) && !HasUser(environment.ConnectionString))
        {
            problems.Add($"environment '{name}': password authentication requires 'user'.");
        }

        if (environment.Tls.HostNameInCertificate is not null || environment.Tls.ServerCertificate is not null)
        {
            problems.Add($"environment '{name}': Oracle checks server certificates against a wallet: set connectionProperties Wallet_Location instead of tls.serverCertificate or tls.hostNameInCertificate.");
        }

        foreach (var (key, value) in environment.ProviderSettings)
        {
            if (!string.Equals(key, SchemaSetting, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"environment '{name}': unknown Oracle setting '{key}'. Known: schema.");
            }
            else if (value is not JsonValue v || !v.TryGetValue<string>(out var schema) || !SchemaRegex().IsMatch(schema))
            {
                problems.Add($"environment '{name}': 'schema' must be a schema (user) name such as SHOP.");
            }
        }

        return problems;
    }

    public static Endpoint ResolveEndpoint(DeploymentEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            return new Endpoint(environment.Host, environment.Port ?? DefaultPort);
        }

        var source = new OracleConnectionStringBuilder(environment.ConnectionString ?? string.Empty).DataSource ?? string.Empty;
        if (DescriptorHostRegex().Match(source) is { Success: true } d)
        {
            var port = DescriptorPortRegex().Match(source);
            return new Endpoint(d.Groups[1].Value, port.Success ? int.Parse(port.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : DefaultPort);
        }

        if (EzConnectRegex().Match(source) is { Success: true } m)
        {
            return new Endpoint(m.Groups["host"].Value, m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) : DefaultPort);
        }

        throw new MigrationException($"environment '{environment.Name}': cannot tell the host and port from Data Source '{source}' (a TNS alias); set 'host' and 'database' to use an SSH tunnel.");
    }

    /// <summary>The configured schema, upper case unless quoted; null to use the user's own.</summary>
    public static string? ConfiguredSchema(DeploymentEnvironment environment) =>
        environment.ProviderSettings.TryGetPropertyValue(SchemaSetting, out var node) && node is JsonValue v && v.TryGetValue<string>(out var schema)
            ? Normalize(schema)
            : null;

    /// <summary>The schema trek deploys to (for the {db} built-in variable).</summary>
    public static string? ResolveDatabaseName(DeploymentEnvironment environment) =>
        ConfiguredSchema(environment)
        ?? (environment.User is { Length: > 0 } user ? Normalize(user)
        : environment.ConnectionString is null ? null : new OracleConnectionStringBuilder(environment.ConnectionString).UserID is { Length: > 0 } id ? Normalize(id) : null);

    /// <summary>The connection string and a description without secrets.</summary>
    public static (OracleConnectionStringBuilder Builder, string Description) Build(DeploymentEnvironment environment, SessionContext context)
    {
        var b = environment.ConnectionString is null ? new OracleConnectionStringBuilder() : new OracleConnectionStringBuilder(environment.ConnectionString);
        var tcps = environment.Tls.Mode is TlsMode.Mandatory or TlsMode.Strict;
        if (!string.IsNullOrWhiteSpace(environment.Host))
        {
            var endpoint = context.EndpointOverride ?? ResolveEndpoint(environment);
            b.DataSource = Descriptor(endpoint.Host, endpoint.Port, environment.Database!, tcps, environment.Tls.Mode == TlsMode.Strict);
        }
        else if (context.EndpointOverride is { } tunnel)
        {
            var service = ServiceName(b.DataSource)
                ?? throw new MigrationException($"environment '{environment.Name}': set 'host' and 'database' to use an SSH tunnel.");
            b.DataSource = Descriptor(tunnel.Host, tunnel.Port, service, tcps, environment.Tls.Mode == TlsMode.Strict);
        }

        if (!string.IsNullOrEmpty(environment.User))
        {
            b.UserID = environment.User;
        }

        if (environment.Authentication == AuthenticationKind.Password)
        {
            b.Password = environment.Password;
        }

        foreach (var (key, value) in environment.ConnectionProperties)
        {
            b[key] = value;
        }

        var target = string.IsNullOrWhiteSpace(environment.Host) && environment.ConnectionString is not null && ServiceName(b.DataSource) is null
            ? b.DataSource
            : $"{ResolveEndpoint(environment).Host}:{ResolveEndpoint(environment).Port}/{environment.Database ?? ServiceName(b.DataSource)}";
        var description = $"oracle://{target}{(context.EndpointOverride is null ? string.Empty : " (via ssh)")}";
        return (b, description);
    }

    /// <summary>Upper case, as Oracle stores an unquoted name; a "quoted" name keeps its case.</summary>
    public static string Normalize(string name) =>
        name.Length > 1 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : name.ToUpperInvariant();

    private static string Descriptor(string host, int port, string service, bool tcps, bool matchName) =>
        $"(DESCRIPTION=(ADDRESS=(PROTOCOL={(tcps ? "TCPS" : "TCP")})(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={service})){(tcps ? $"(SECURITY=(SSL_SERVER_DN_MATCH={(matchName ? "YES" : "NO")}))" : string.Empty)})";

    private static string? ServiceName(string? dataSource)
    {
        if (string.IsNullOrEmpty(dataSource))
        {
            return null;
        }

        if (DescriptorServiceRegex().Match(dataSource) is { Success: true } d)
        {
            return d.Groups[1].Value;
        }

        return EzConnectRegex().Match(dataSource) is { Success: true } m && m.Groups["service"].Success ? m.Groups["service"].Value : null;
    }

    private static bool HasUser(string? connectionString) =>
        connectionString is not null && !string.IsNullOrEmpty(new OracleConnectionStringBuilder(connectionString).UserID);

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9_$#]{0,127}|""[^""]{1,128}"")$")]
    private static partial Regex SchemaRegex();

    [GeneratedRegex(@"\A(?:tcps?://)?(?<host>[^:/()\s]+)(?::(?<port>\d+))?(?:/(?<service>[^:/\s]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex EzConnectRegex();

    [GeneratedRegex(@"\(\s*HOST\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptorHostRegex();

    [GeneratedRegex(@"\(\s*PORT\s*=\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptorPortRegex();

    [GeneratedRegex(@"\(\s*SERVICE_NAME\s*=\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex DescriptorServiceRegex();
}
