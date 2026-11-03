using System.Text;
using System.Text.Json.Nodes;
using Badger.SqlMigrate.Core.Import;

namespace Badger.SqlMigrate.Cli;

/// <summary>Writes bsm.yaml for an imported project (block style, every string double-quoted).</summary>
public static class ImportYaml
{
    public static string Write(ImportedProject imported, string providerId)
    {
        var sb = new StringBuilder();
        sb.Append($"# Badger SQL Migrate configuration, imported from {imported.Tool}. Review every environment before deploying.\n");
        sb.Append($"provider: {providerId}\n");
        var defaultEnvironment = imported.DefaultEnvironment ?? (imported.Environments.Count == 0 ? "local" : null);
        if (defaultEnvironment is not null)
        {
            sb.Append($"defaultEnvironment: {Quote(defaultEnvironment)}\n");
        }

        if (imported.DisableSqlCmdSyntax)
        {
            sb.Append($"\n# Scripts from {imported.Tool} contain literal $(name) text, so sqlcmd-style variables are off.\npreprocessor:\n  sqlcmdSyntax: false\n");
        }

        sb.Append("\n# {name} in scripts. Values are not part of change hashes.\n");
        var variables = Nest(imported.Variables.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
        if (variables.Count == 0)
        {
            sb.Append("variables: {}\n");
        }
        else
        {
            sb.Append("variables:\n");
            WriteObject(sb, variables, 1);
        }

        if (imported.UndefinedVariables.Count > 0)
        {
            sb.Append($"  # Used by scripts but {imported.Tool} did not define a value; 'bsm plan check' fails until you set them:\n");
            foreach (var name in imported.UndefinedVariables)
            {
                sb.Append($"  # {name}: \"...\"\n");
            }
        }

        sb.Append("\nenvironments:\n");
        if (imported.Environments.Count == 0)
        {
            sb.Append("""
                  local:                               # where to deploy: edit to match your server
                    host: localhost
                    port: 1433
                    database: MyDatabase
                    authentication: password
                    user: sa
                    password: ${env:BSM_LOCAL_PASSWORD}
                    tls:
                      mode: mandatory
                      trustServerCertificate: true     # local development only

                """.Replace("\r\n", "\n", StringComparison.Ordinal));
        }

        foreach (var environment in imported.Environments)
        {
            sb.Append($"  {Key(environment.Name)}:\n");
            var settings = (JsonObject)environment.Settings.DeepClone();
            if (settings["variables"] is JsonObject vars)
            {
                settings["variables"] = Nest(vars.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
            }

            if (settings.Count == 0)
            {
                sb.Append("    {}\n");
            }

            WriteObject(sb, settings, 2);
        }

        return sb.ToString();
    }

    /// <summary>Dotted names become nested groups: flyway.user -> flyway: { user }.</summary>
    private static JsonObject Nest(IEnumerable<KeyValuePair<string, JsonNode?>> flat)
    {
        var root = new JsonObject();
        foreach (var (name, value) in flat.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var parts = name.Split('.');
            var node = root;
            foreach (var part in parts[..^1])
            {
                if (node[part] is not JsonObject child)
                {
                    child = new JsonObject();
                    node[part] = child;
                }

                node = child;
            }

            node[parts[^1]] = value;
        }

        return root;
    }

    private static void WriteObject(StringBuilder sb, JsonObject obj, int depth)
    {
        var indent = new string(' ', depth * 2);
        foreach (var (key, value) in obj)
        {
            if (value is JsonObject child)
            {
                sb.Append($"{indent}{Key(key)}:\n");
                WriteObject(sb, child, depth + 1);
            }
            else
            {
                sb.Append($"{indent}{Key(key)}: {Scalar(value)}\n");
            }
        }
    }

    private static string Scalar(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<int>(out var i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Quote(value.ToString()),
    };

    private static string Key(string key) => key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? key : Quote(key);

    private static string Quote(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }
}
