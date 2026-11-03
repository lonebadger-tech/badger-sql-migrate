using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Import;

namespace Badger.SqlMigrate.Cli;

/// <summary>
/// Maps a PostgreSQL JDBC URL (jdbc:postgresql://host:port/database?sslmode=require&amp;currentSchema=app) to
/// bsm.yaml environment settings. Passwords are never copied: they become ${env:...} references.
/// </summary>
public static partial class JdbcPostgreSql
{
    public static JsonObject? Map(ConnectionToImport connection, List<string> warnings)
    {
        var where = $"environment '{connection.EnvironmentName}'";
        var m = UrlRegex().Match(connection.Url.Trim());
        if (!m.Success)
        {
            warnings.Add($"{where}: '{Regex.Replace(connection.Url, "(?i)(password=)[^&]*", "$1********")}' is not a PostgreSQL JDBC URL; set the connection in bsm.yaml by hand.");
            return null;
        }

        var settings = new JsonObject();
        var hosts = m.Groups["hosts"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (hosts.Length > 1)
        {
            warnings.Add($"{where}: the URL lists several hosts; using the first. Put all of them in connectionProperties 'Host' for failover.");
        }

        if (hosts.Length > 0)
        {
            var host = hosts[0];
            var colon = host.LastIndexOf(':');
            if (colon > 0 && !host.EndsWith(']') && int.TryParse(host[(colon + 1)..], out var port))
            {
                settings["host"] = host[..colon];
                settings["port"] = port;
            }
            else
            {
                settings["host"] = host;
            }
        }

        if (m.Groups["db"].Success && m.Groups["db"].Value.Length > 0)
        {
            settings["database"] = Uri.UnescapeDataString(m.Groups["db"].Value);
        }

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in m.Groups["query"].Value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            props[Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "true" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        var user = Env(connection.User) ?? Take(props, "user");
        var password = connection.Password ?? Take(props, "password");
        var plugin = Take(props, "authenticationPluginClassName");
        if (plugin is not null && plugin.Contains("azure", StringComparison.OrdinalIgnoreCase))
        {
            settings["authentication"] = "entraDefault";
            password = null;
        }
        else if (Take(props, "gsslib", "jaasApplicationName") is not null || (user is null && password is null))
        {
            settings["authentication"] = user is null && password is null ? "integrated" : "password";
        }
        else
        {
            settings["authentication"] = "password";
        }

        if (user is not null)
        {
            settings["user"] = user;
        }

        if (password is not null)
        {
            if (Env(password) is { } reference && reference.StartsWith("${env:", StringComparison.Ordinal))
            {
                settings["password"] = reference;
            }
            else
            {
                var variable = connection.EnvironmentName == "default" ? "FLYWAY_PASSWORD" : $"FLYWAY_{Regex.Replace(connection.EnvironmentName.ToUpperInvariant(), "[^A-Z0-9]+", "_")}_PASSWORD";
                warnings.Add($"{where}: the password was not copied; bsm reads it from the {variable} environment variable.");
                settings["password"] = $"${{env:{variable}}}";
            }
        }

        var tls = new JsonObject();
        var ssl = Take(props, "sslmode");
        if (ssl is null && string.Equals(Take(props, "ssl"), "true", StringComparison.OrdinalIgnoreCase))
        {
            ssl = "require";
        }

        switch (ssl?.ToLowerInvariant())
        {
            case "disable":
                tls["mode"] = "disabled";
                break;
            case "allow" or "prefer":
                tls["mode"] = "optional";
                break;
            case "require":
                tls["mode"] = "mandatory";
                break;
            case "verify-ca":
                tls["mode"] = "mandatory";
                warnings.Add($"{where}: sslmode=verify-ca: set tls.serverCertificate to the CA certificate file so bsm verifies it.");
                break;
            case "verify-full":
                tls["mode"] = "strict";
                break;
        }

        if (Take(props, "sslrootcert") is { } root)
        {
            tls["serverCertificate"] = root;
        }

        if (tls.Count > 0)
        {
            settings["tls"] = tls;
        }

        var connectionProperties = new JsonObject();
        foreach (var (jdbc, npgsql) in new[] { ("currentSchema", "Search Path"), ("ApplicationName", "Application Name"), ("connectTimeout", "Timeout"), ("loginTimeout", "Timeout"), ("socketTimeout", "Command Timeout"), ("targetServerType", "Target Session Attributes") })
        {
            if (Take(props, jdbc) is { } value)
            {
                connectionProperties[npgsql] = value;
            }
        }

        if (connectionProperties.Count > 0)
        {
            settings["connectionProperties"] = connectionProperties;
        }

        if (props.Count > 0)
        {
            warnings.Add($"{where}: JDBC settings without a bsm equivalent were left out: {string.Join(", ", props.Keys.Order(StringComparer.OrdinalIgnoreCase))}. Any Npgsql keyword can go under connectionProperties.");
        }

        return settings;
    }

    private static string? Env(string? value) =>
        value is null ? null : EnvRegex().Match(value) is { Success: true } m ? $"${{env:{m.Groups["name"].Value}}}" : value;

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

    [GeneratedRegex(@"^jdbc:postgresql://(?<hosts>[^/?]*)(?:/(?<db>[^?]*))?(?:\?(?<query>.*))?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^\$\{(?:env\.)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}$")]
    private static partial Regex EnvRegex();
}
