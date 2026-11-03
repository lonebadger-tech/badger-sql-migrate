using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Import;

namespace Badger.SqlMigrate.Cli;

/// <summary>
/// Maps a Microsoft JDBC driver URL (jdbc:sqlserver://host\instance:port;databaseName=X;encrypt=true;...) to
/// bsm.yaml environment settings. Passwords are never copied: they become ${env:...} references.
/// </summary>
public static partial class JdbcSqlServer
{
    public static JsonObject? Map(ConnectionToImport connection, List<string> warnings)
    {
        var url = connection.Url.Trim();
        var where = $"environment '{connection.EnvironmentName}'";
        if (!url.StartsWith("jdbc:sqlserver:", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"{where}: '{Redact(url)}' is not a SQL Server JDBC URL; set the connection in bsm.yaml by hand.");
            return null;
        }

        var rest = url["jdbc:sqlserver:".Length..];
        var parts = rest.Split(';');
        var server = parts[0].StartsWith("//", StringComparison.Ordinal) ? parts[0][2..] : parts[0];
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1).Where(p => p.Contains('=')))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            props[part[..eq].Trim()] = part[(eq + 1)..].Trim().Trim('{', '}');
        }

        if (server.Length == 0 && props.Remove("serverName", out var serverName))
        {
            server = serverName + (props.Remove("instanceName", out var instance) ? "\\" + instance : string.Empty) + (props.Remove("portNumber", out var portNumber) ? ":" + portNumber : string.Empty);
        }

        var settings = new JsonObject();
        var m = ServerRegex().Match(server);
        if (!m.Success || m.Groups["host"].Value.Length == 0)
        {
            warnings.Add($"{where}: could not read the server from the JDBC URL; set host by hand.");
        }
        else
        {
            settings["host"] = m.Groups["host"].Value + (m.Groups["instance"].Success ? "\\" + m.Groups["instance"].Value : string.Empty);
            if (m.Groups["port"].Success)
            {
                settings["port"] = int.Parse(m.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (Take(props, "databaseName", "database") is { } database)
        {
            settings["database"] = database;
        }

        var user = Secret(connection.User) ?? Take(props, "user", "userName");
        var passwordRef = PasswordReference(connection, props, warnings);
        var integrated = string.Equals(Take(props, "integratedSecurity"), "true", StringComparison.OrdinalIgnoreCase);
        var authentication = Take(props, "authentication");
        Take(props, "authenticationScheme");
        var entra = new JsonObject();
        switch (authentication?.ToLowerInvariant())
        {
            case "activedirectorydefault":
                settings["authentication"] = "entraDefault";
                break;
            case "activedirectoryinteractive":
                settings["authentication"] = "entraInteractive";
                if (user is not null)
                {
                    entra["loginHint"] = user;
                }

                user = null;
                break;
            case "activedirectoryintegrated":
                settings["authentication"] = "entraIntegrated";
                user = null;
                break;
            case "activedirectorypassword":
                settings["authentication"] = "entraPassword";
                break;
            case "activedirectorymsi" or "activedirectorymanagedidentity":
                settings["authentication"] = "entraManagedIdentity";
                if ((Take(props, "msiClientId") ?? user) is { } clientId)
                {
                    entra["clientId"] = clientId;
                }

                user = null;
                break;
            case "activedirectoryserviceprincipal":
                settings["authentication"] = "entraServicePrincipal";
                if (user is not null)
                {
                    entra["clientId"] = user;
                }

                entra["clientSecret"] = passwordRef;
                user = null;
                passwordRef = null;
                warnings.Add($"{where}: set entra.tenantId for the service principal.");
                break;
            case "activedirectoryserviceprincipalcertificate":
                settings["authentication"] = "entraServicePrincipalCertificate";
                if (user is not null)
                {
                    entra["clientId"] = user;
                }

                user = null;
                passwordRef = null;
                warnings.Add($"{where}: set entra.tenantId and entra.certificatePath for the service principal certificate.");
                break;
            default:
                settings["authentication"] = integrated ? "integrated" : "password";
                if (integrated)
                {
                    user = null;
                    passwordRef = null;
                }

                break;
        }

        if (user is not null)
        {
            settings["user"] = user;
        }

        if (passwordRef is not null && (string?)settings["authentication"] is "password" or "entraPassword")
        {
            settings["password"] = passwordRef;
        }

        if (entra.Count > 0)
        {
            settings["entra"] = entra;
        }

        var tls = new JsonObject();
        switch (Take(props, "encrypt")?.ToLowerInvariant())
        {
            case "strict":
                tls["mode"] = "strict";
                break;
            case "true" or "mandatory":
                tls["mode"] = "mandatory";
                break;
            case "false" or "optional":
                tls["mode"] = "optional";
                break;
        }

        if (string.Equals(Take(props, "trustServerCertificate"), "true", StringComparison.OrdinalIgnoreCase))
        {
            tls["trustServerCertificate"] = true;
        }

        if (Take(props, "hostNameInCertificate") is { } hostName)
        {
            tls["hostNameInCertificate"] = hostName;
        }

        if (tls.Count > 0)
        {
            settings["tls"] = tls;
        }

        var connectionProperties = new JsonObject();
        foreach (var (jdbc, sqlClient) in Equivalents)
        {
            if (Take(props, jdbc) is { } value)
            {
                connectionProperties[sqlClient] = value;
            }
        }

        if (connectionProperties.Count > 0)
        {
            settings["connectionProperties"] = connectionProperties;
        }

        Take(props, "password");
        if (props.Count > 0)
        {
            warnings.Add($"{where}: JDBC settings without a bsm equivalent were left out: {string.Join(", ", props.Keys.Order(StringComparer.OrdinalIgnoreCase))}. Any SqlClient keyword can go under connectionProperties.");
        }

        return settings;
    }

    /// <summary>JDBC property names with a SqlClient keyword of the same meaning.</summary>
    private static readonly (string Jdbc, string SqlClient)[] Equivalents =
    [
        ("applicationIntent", "Application Intent"),
        ("applicationName", "Application Name"),
        ("multiSubnetFailover", "Multi Subnet Failover"),
        ("loginTimeout", "Connect Timeout"),
        ("failoverPartner", "Failover Partner"),
        ("packetSize", "Packet Size"),
        ("workstationID", "Workstation ID"),
        ("serverSpn", "Server SPN"),
        ("connectRetryCount", "Connect Retry Count"),
        ("connectRetryInterval", "Connect Retry Interval"),
        ("columnEncryptionSetting", "Column Encryption Setting"),
        ("enclaveAttestationUrl", "Enclave Attestation Url"),
        ("enclaveAttestationProtocol", "Attestation Protocol"),
    ];

    private static string? PasswordReference(ConnectionToImport connection, Dictionary<string, string> props, List<string> warnings)
    {
        var password = connection.Password ?? (props.TryGetValue("password", out var p) ? p : null);
        if (password is null)
        {
            return null;
        }

        if (Secret(password) is { } reference && reference.StartsWith("${env:", StringComparison.Ordinal))
        {
            return reference;
        }

        var variable = connection.EnvironmentName == "default" ? "FLYWAY_PASSWORD" : $"FLYWAY_{EnvVarRegex().Replace(connection.EnvironmentName.ToUpperInvariant(), "_")}_PASSWORD";
        warnings.Add($"environment '{connection.EnvironmentName}': the password was not copied; bsm reads it from the {variable} environment variable.");
        return $"${{env:{variable}}}";
    }

    /// <summary>Flyway's ${env.NAME} / ${NAME} references become ${env:NAME}; plain values pass through.</summary>
    private static string? Secret(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var m = FlywayEnvRegex().Match(value);
        return m.Success ? $"${{env:{m.Groups["name"].Value}}}" : value;
    }

    private static string? Take(Dictionary<string, string> props, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (props.Remove(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static string Redact(string url) => Regex.Replace(url, "(?i)(password=)[^;]*", "$1********");

    [GeneratedRegex(@"^(?<host>[^\\:;]*)(?:\\(?<instance>[^:;]+))?(?::(?<port>\d+))?$")]
    private static partial Regex ServerRegex();

    [GeneratedRegex(@"^\$\{(?:env\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}$")]
    private static partial Regex FlywayEnvRegex();

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex EnvVarRegex();
}
