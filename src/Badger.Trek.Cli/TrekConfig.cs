using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Badger.Trek.Connectivity;
using Badger.Trek.Core;
using Badger.Trek.Core.Configuration;
using Badger.Trek.Core.Providers;
using Badger.Trek.Core.Preprocessing;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Badger.Trek.Cli;

/// <summary>
/// trek.yaml: provider, shared variables and named environments.
/// <code>
/// provider: sqlserver
/// defaultEnvironment: dev
/// preprocessor: { unknown: error }    # or keep: leave unknown {name} tokens as-is
/// variables:                           # {name} / $(name) in scripts; nested groups become {group.name}
///   AppRole: app_user
///   schemas:                           # block style; inside inline { } maps, quote values containing {..}
///     calc: calc_{env}
/// environments:
///   _azure:                        # names starting with '_' are templates (not deployable)
///     tls: { mode: strict }
///     authentication: entraInteractive
///   dev:
///     extends: _azure
///     host: dev-sql.database.windows.net
///     database: App
///   prod:
///     extends: _azure
///     protected: true
///     allowRevert: false
///     ordering: strict
///     alwaysEncrypted: { ... }     # provider-specific blocks pass through to the provider
/// </code>
/// Secrets are referenced as ${env:NAME} or ${file:path}; they are resolved only for the environment in use.
/// </summary>
public sealed class TrekConfig
{
    public const string FileName = "trek.yaml";

    /// <summary>The project file of Badger SQL Migrate (0.3.0 and earlier); only used to explain the rename.</summary>
    public const string LegacyFileName = "bsm.yaml";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> RootKeys = new(StringComparer.OrdinalIgnoreCase) { "provider", "defaultEnvironment", "variables", "environments", "preprocessor" };

    private TrekConfig(string provider, string? defaultEnvironment, Dictionary<string, string> variables, Dictionary<string, JsonObject> environments, PreprocessorOptions preprocessor, JsonObject? providerSettings)
    {
        ProviderSettings = providerSettings;
        Preprocessor = preprocessor;
        Provider = provider;
        DefaultEnvironment = defaultEnvironment;
        Variables = variables;
        Environments = environments;
    }

    public string Provider { get; }

    /// <summary>The provider's project-level block (e.g. 'sqlite:'), when it takes one and the file has it.</summary>
    public JsonObject? ProviderSettings { get; }

    /// <summary>The project's provider, with its project-level settings.</summary>
    public IDatabaseProvider CreateProvider() => Commands.Providers.Resolve(Provider, ProviderSettings);

    public PreprocessorOptions Preprocessor { get; }

    public string? DefaultEnvironment { get; }

    public IReadOnlyDictionary<string, string> Variables { get; }

    /// <summary>Raw (unmerged, unresolved) environment blocks, including templates.</summary>
    public IReadOnlyDictionary<string, JsonObject> Environments { get; }

    public IEnumerable<string> DeployableEnvironments => Environments.Keys.Where(k => !IsTemplate(k)).Order(StringComparer.Ordinal);

    public static bool IsTemplate(string name) => name.StartsWith('_');

    public static TrekConfig Load(string projectDir)
    {
        var path = Path.Combine(projectDir, FileName);
        if (!File.Exists(path))
        {
            if (File.Exists(Path.Combine(projectDir, LegacyFileName)))
            {
                throw new MigrationException(
                    $"No {FileName} found in '{projectDir}', but {LegacyFileName} is there: this is a Badger SQL Migrate project. " +
                    $"Badger Trek renamed the project files: rename {LegacyFileName} to {FileName} and bsm.plan to trek.plan, and the names inside the scripts " +
                    "(-- bsm: directives, {bsm.*} variables, BSM_* environment variables). Databases it deployed need their journal moved once: " +
                    "see https://lonebadger.tech/badger-trek/docs/upgrading/");
            }

            throw new MigrationException($"No {FileName} found in '{projectDir}'. Run 'trek init' or pass --project <dir>.");
        }

        JsonObject root;
        try
        {
            root = YamlJson.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (YamlException ex)
        {
            throw new MigrationException($"{FileName} line {ex.Start.Line}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        if (root.ContainsKey("profiles"))
        {
            throw new MigrationException($"{FileName}: 'profiles' was renamed to 'environments' (and 'defaultProfile' to 'defaultEnvironment').");
        }

        var providerSections = Commands.Providers.SettingsSections.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in root.Select(kv => kv.Key).Where(k => !RootKeys.Contains(k) && !providerSections.Contains(k)))
        {
            throw new MigrationException($"{FileName}: unknown top-level key '{key}'. Known: {string.Join(", ", RootKeys.Concat(providerSections))}.");
        }

        var provider = Scalar(root["provider"]) is { Length: > 0 } p ? p : "sqlserver";
        var section = Commands.Providers.SettingsSection(provider);
        foreach (var other in providerSections.Where(k => root.ContainsKey(k) && !string.Equals(k, section, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MigrationException($"{FileName}: '{other}' settings apply only to provider: {other}, and this project's provider is {provider}.");
        }

        JsonObject? providerSettings = null;
        if (section is not null && root[section] is { } sectionNode)
        {
            providerSettings = sectionNode as JsonObject ?? throw new MigrationException($"{FileName}: '{section}' must be a mapping.");
        }

        var environments = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (root["environments"] is JsonObject envs)
        {
            foreach (var (name, node) in envs)
            {
                environments[name] = node as JsonObject ?? throw new MigrationException($"{FileName}: environment '{name}' must be a mapping.");
            }
        }

        var variables = Flatten(root["variables"] as JsonObject);
        var preprocessor = new PreprocessorOptions();
        if (root["preprocessor"] is JsonObject pre)
        {
            foreach (var (key, value) in pre)
            {
                preprocessor = key.ToLowerInvariant() switch
                {
                    "unknown" => preprocessor with { Unknown = Enum.TryParse<UnknownVariablePolicy>(Scalar(value), true, out var u) ? u : throw new MigrationException($"{FileName}: preprocessor.unknown must be 'error' or 'keep'.") },
                    "sqlcmdsyntax" => preprocessor with { SqlCmdSyntax = value?.GetValue<bool>() ?? true },
                    _ => throw new MigrationException($"{FileName}: unknown preprocessor setting '{key}'. Known: unknown, sqlcmdSyntax."),
                };
            }
        }

        return new TrekConfig(provider, root["defaultEnvironment"]?.ToString(), variables, environments, preprocessor, providerSettings);
    }

    /// <summary>Picks --env, then TREK_ENV, then defaultEnvironment, then the only deployable environment.</summary>
    public string SelectEnvironmentName(string? requested)
    {
        var name = requested ?? Environment.GetEnvironmentVariable("TREK_ENV") ?? DefaultEnvironment;
        if (name is null)
        {
            var deployable = DeployableEnvironments.ToList();
            name = deployable.Count == 1 ? deployable[0] : throw new MigrationException($"Choose an environment with --env. Available: {string.Join(", ", deployable)}.");
        }

        if (!Environments.ContainsKey(name))
        {
            throw new MigrationException($"Environment '{name}' is not defined in {FileName}. Available: {string.Join(", ", DeployableEnvironments)}.");
        }

        if (IsTemplate(name))
        {
            throw new MigrationException($"'{name}' is a template (names starting with '_' are only for 'extends').");
        }

        return name;
    }

    /// <summary>The environment with its extends chain merged, before secrets are resolved.</summary>
    public JsonObject Merge(string name)
    {
        var chain = new List<string>();
        var current = name;
        while (current is not null)
        {
            if (chain.Contains(current, StringComparer.Ordinal))
            {
                throw new MigrationException($"environment '{name}': 'extends' cycle: {string.Join(" -> ", chain)} -> {current}.");
            }

            chain.Add(current);
            if (!Environments.TryGetValue(current, out var block))
            {
                throw new MigrationException($"environment '{chain[^2]}' extends unknown environment '{current}'.");
            }

            current = block["extends"]?.ToString();
        }

        var merged = new JsonObject();
        foreach (var block in chain.AsEnumerable().Reverse().Select(n => Environments[n]))
        {
            DeepMerge(merged, block);
        }

        merged.Remove("extends");
        return merged;
    }

    /// <param name="offline">
    /// For commands that never connect (vars, render, trek mcp): sign-in settings are left out, so their secrets
    /// (password, connection string, SSH keys, Entra client secrets) need not be set. Variables still resolve.
    /// </param>
    /// <param name="offline">Nothing will connect: leave out connection secrets.</param>
    /// <param name="keepUnresolved">Keep unset ${env:...} / missing ${file:...} references as written instead of failing (for showing an environment).</param>
    public DeploymentEnvironment ResolveEnvironment(string? requested, string? connectionStringOverride, bool offline = false, bool keepUnresolved = false)
    {
        var name = SelectEnvironmentName(requested);
        var merged = Merge(name);
        if (offline)
        {
            foreach (var key in new[] { "password", "connectionString", "ssh", "entra", "alwaysEncrypted" })
            {
                merged.Remove(key);
            }
        }

        if (connectionStringOverride is not null)
        {
            // An explicit --connection-string means "connect exactly here, as it says": the environment's server,
            // sign-in and TLS settings no longer apply (they used to override the string's own user, password and
            // Encrypt / TrustServerCertificate).
            merged["connectionString"] = connectionStringOverride;
            foreach (var key in new[] { "host", "port", "database", "user", "password", "authentication", "tls" })
            {
                merged.Remove(key);
            }
        }

        ResolveSecrets(merged, name, keepUnresolved);

        EnvironmentDto dto;
        try
        {
            dto = merged.Deserialize<EnvironmentDto>(JsonOptions) ?? new EnvironmentDto();
        }
        catch (JsonException ex)
        {
            throw new MigrationException($"environment '{name}': {ex.Message}", ex);
        }

        var variables = new Dictionary<string, string>(Variables, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Flatten(dto.Variables is null ? null : new JsonObject(dto.Variables.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())))))
        {
            variables[k] = v;
        }

        return new DeploymentEnvironment
        {
            Name = name,
            Description = dto.Description,
            ConnectionString = dto.ConnectionString,
            Host = dto.Host,
            Port = dto.Port,
            Database = dto.Database,
            Authentication = dto.Authentication ?? (dto.User is not null && dto.Password is not null ? AuthenticationKind.Password : null),
            User = dto.User,
            Password = dto.Password,
            Entra = dto.Entra ?? new EntraOptions(),
            Tls = dto.Tls ?? new TlsOptions(),
            Ssh = dto.Ssh,
            ConnectionProperties = (dto.ConnectionProperties ?? []).ToDictionary(kv => kv.Key, kv => Scalar(kv.Value), StringComparer.OrdinalIgnoreCase),
            Ordering = dto.Ordering ?? OrderingPolicy.Dependency,
            Protected = dto.Protected ?? false,
            AllowRevert = dto.AllowRevert ?? true,
            StateSchema = dto.StateSchema ?? "trek",
            CommandTimeoutSeconds = dto.CommandTimeoutSeconds,
            LockTimeoutSeconds = dto.LockTimeoutSeconds ?? 300,
            Variables = variables,
            ProviderSettings = dto.ProviderSettings is null ? [] : JsonSerializer.SerializeToNode(dto.ProviderSettings)!.AsObject(),
        };
    }

    /// <summary>Variable names defined for an environment (global + its extends chain), without resolving secrets.</summary>
    public IReadOnlySet<string> VariableNames(string environment)
    {
        var names = new HashSet<string>(Variables.Keys, StringComparer.OrdinalIgnoreCase);
        names.UnionWith(Flatten(Merge(environment)["variables"] as JsonObject).Keys);
        return names;
    }

    /// <summary>Nested variable groups become dotted names: schemas: { calc: x } is {schemas.calc}.</summary>
    public static Dictionary<string, string> Flatten(JsonObject? node, string prefix = "")
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is null)
        {
            return result;
        }

        foreach (var (key, value) in node)
        {
            var name = prefix + key;
            if (value is JsonObject child)
            {
                foreach (var (k, v) in Flatten(child, name + "."))
                {
                    result[k] = v;
                }
            }
            else if (value is JsonArray)
            {
                throw new MigrationException($"Variable '{name}' is a list; variables must be scalars or groups.");
            }
            else
            {
                result[name] = Scalar(value);
            }
        }

        return result;
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceChild && target[key] is JsonObject targetChild)
            {
                DeepMerge(targetChild, sourceChild);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    private static void ResolveSecrets(JsonNode? node, string environment, bool lenient)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Resolve(text, environment, lenient);
                    }
                    else
                    {
                        ResolveSecrets(obj[key], environment, lenient);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = Resolve(text, environment, lenient);
                    }
                    else
                    {
                        ResolveSecrets(array[i], environment, lenient);
                    }
                }

                break;
        }
    }

    private static string? Resolve(string text, string environment, bool lenient)
    {
        try
        {
            return SecretResolver.Resolve(text);
        }
        catch (MigrationException) when (lenient)
        {
            return text;
        }
        catch (MigrationException ex)
        {
            throw new MigrationException($"environment '{environment}': {ex.Message}", ex);
        }
    }

    private static string Scalar(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<long>(out var l) => l.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };

    private sealed class EnvironmentDto
    {
        public string? Description { get; set; }

        public string? ConnectionString { get; set; }

        public string? Host { get; set; }

        public int? Port { get; set; }

        public string? Database { get; set; }

        public AuthenticationKind? Authentication { get; set; }

        public string? User { get; set; }

        public string? Password { get; set; }

        public EntraOptions? Entra { get; set; }

        public TlsOptions? Tls { get; set; }

        public SshOptions? Ssh { get; set; }

        public Dictionary<string, JsonNode?>? ConnectionProperties { get; set; }

        public OrderingPolicy? Ordering { get; set; }

        public bool? Protected { get; set; }

        public bool? AllowRevert { get; set; }

        public string? StateSchema { get; set; }

        public int? CommandTimeoutSeconds { get; set; }

        public int? LockTimeoutSeconds { get; set; }

        public Dictionary<string, JsonNode?>? Variables { get; set; }

        /// <summary>Everything else (e.g. alwaysEncrypted) belongs to the provider, which reports unknown keys.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ProviderSettings { get; set; }
    }
}

/// <summary>YAML to JSON with YAML 1.2 core-schema typing for plain scalars (quoted scalars stay strings).</summary>
public static class YamlJson
{
    public static JsonNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents.Count == 0 ? null : Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value ?? string.Empty, Convert(kv.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(Convert).ToArray()),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null,
    };

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style is not ScalarStyle.Plain)
        {
            return JsonValue.Create(value);
        }

        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && value.Any(char.IsDigit))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(value);
    }
}
