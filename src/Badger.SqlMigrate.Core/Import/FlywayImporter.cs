using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Import;

/// <param name="Url">The JDBC URL.</param>
/// <param name="User">The user setting, if any.</param>
/// <param name="Password">The password setting, if any (only ever turned into a secret reference).</param>
public sealed record ConnectionToImport(string EnvironmentName, string Url, string? User, string? Password);

/// <summary>Maps a JDBC connection to bsm.yaml environment settings for the target provider, or null when it cannot.</summary>
public delegate JsonObject? ConnectionMapper(ConnectionToImport connection, List<string> warnings);

public sealed record FlywayImportOptions
{
    /// <summary>A Flyway project folder (with flyway.conf / flyway.toml and sql/), or a folder of migrations.</summary>
    public required string Path { get; init; }

    /// <summary>flyway.conf or flyway.toml; found in <see cref="Path"/> (or its conf/ folder) when null.</summary>
    public string? ConfigFile { get; init; }

    /// <summary>Migration folders; overrides flyway.locations.</summary>
    public IReadOnlyList<string> Locations { get; init; } = [];

    public ConnectionMapper? MapConnection { get; init; }
}

/// <summary>
/// Converts a Flyway project into bsm's layout:
/// <list type="bullet">
/// <item>V&lt;version&gt;__&lt;description&gt;.sql: a change named after the description, in version order; the note
/// "flyway V&lt;version&gt; checksum &lt;n&gt;" records where it came from.</item>
/// <item>U&lt;version&gt;__*.sql (undo): the change's revert.sql. Changes without one are marked !irreversible.</item>
/// <item>R__&lt;description&gt;.sql: repeatable/&lt;description&gt;.sql (same order: by description).</item>
/// <item>&lt;script&gt;.sql.conf with executeInTransaction=false: !notx.</item>
/// <item>${name} placeholders: {name} variables, with values from flyway.placeholders.*, per environment from
/// flyway.toml environments; ${flyway:*} built-ins map to bsm built-ins where one exists.</item>
/// <item>flyway.url / [environments.*]: bsm environments (passwords become ${env:...} references).</item>
/// </list>
/// Baseline (B) migrations, callbacks and Java migrations are reported, not imported.
/// </summary>
public static partial class FlywayImporter
{
    public const string Tool = "Flyway";

    public static ImportedProject Import(FlywayImportOptions options)
    {
        var root = System.IO.Path.GetFullPath(options.Path);
        if (!Directory.Exists(root))
        {
            throw new MigrationException($"'{options.Path}' is not a folder.");
        }

        var configFile = options.ConfigFile ?? FindConfig(root);
        var config = configFile is null ? FlywayConfig.Empty : FlywayConfig.Load(configFile);
        var result = new ImportedProject { Tool = Tool };
        if (configFile is not null)
        {
            result.Warnings.Add($"read settings from {System.IO.Path.GetRelativePath(Environment.CurrentDirectory, configFile)}.");
        }

        var syntax = new Syntax(
            config.Get("flyway.sqlMigrationPrefix", "V"),
            config.Get("flyway.undoSqlMigrationPrefix", "U"),
            config.Get("flyway.repeatableSqlMigrationPrefix", "R"),
            config.Get("flyway.baselineMigrationPrefix", "B"),
            config.Get("flyway.sqlMigrationSeparator", "__"),
            config.GetList("flyway.sqlMigrationSuffixes") is { Count: > 0 } suffixes ? suffixes : [".sql"]);

        var files = Locations(root, options.Locations.Count > 0 ? options.Locations : config.GetList("flyway.locations"), result)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            .Where(f => syntax.Suffixes.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var versioned = new SortedDictionary<Version, Script>();
        var undo = new Dictionary<Version, Script>();
        var repeatable = new SortedDictionary<string, Script>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var file in files)
        {
            var name = System.IO.Path.GetFileName(file);
            var suffix = syntax.Suffixes.First(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
            var stem = name[..^suffix.Length];
            var script = new Script(file, name);
            if (TryVersioned(stem, syntax.Versioned, syntax.Separator, out var version, out var description))
            {
                if (versioned.TryGetValue(version, out var other))
                {
                    throw new MigrationException($"Flyway version {version} is used by both {other.FileName} and {name}.");
                }

                versioned[version] = script with { Description = description };
            }
            else if (TryVersioned(stem, syntax.Undo, syntax.Separator, out version, out description))
            {
                undo[version] = script with { Description = description };
            }
            else if (stem.StartsWith(syntax.Repeatable + syntax.Separator, StringComparison.Ordinal))
            {
                var desc = stem[(syntax.Repeatable.Length + syntax.Separator.Length)..];
                if (!repeatable.TryAdd(desc, script with { Description = desc }))
                {
                    throw new MigrationException($"Two repeatable migrations have the description '{desc}'.");
                }
            }
            else if (TryVersioned(stem, syntax.Baseline, syntax.Separator, out _, out _))
            {
                result.Warnings.Add($"{name}: baseline migrations are not imported. New databases get the V scripts it replaces; adopt existing databases with 'bsm baseline --from flyway'.");
            }
            else
            {
                skipped.Add(name);
            }
        }

        if (skipped.Count > 0)
        {
            result.Warnings.Add($"not imported (callbacks or other files): {string.Join(", ", skipped.Order(StringComparer.Ordinal))}. bsm has no callbacks; turn scripts that must run on every deploy into repeatables.");
        }

        if (versioned.Count == 0 && repeatable.Count == 0)
        {
            throw new MigrationException($"No Flyway migrations found under '{options.Path}'. Pass the folder holding V1__*.sql files, or --location.");
        }

        var placeholders = new Placeholders(config, result);
        foreach (var (version, script) in versioned)
        {
            var text = Read(script.Path);
            var flags = ChangeFlags.None;
            if (ScriptConfig(script.Path, result) is { } conf && conf.GetBool("executeInTransaction", true) == false)
            {
                flags |= ChangeFlags.NoTransaction;
            }

            string? revert = null;
            if (undo.Remove(version, out var undoScript))
            {
                revert = placeholders.Convert(Read(undoScript.Path), undoScript.FileName);
            }
            else
            {
                flags |= ChangeFlags.Irreversible;
            }

            var changeName = result.UniqueName(script.Description!.Replace('_', ' '), $"v{version}", $"v{version}");
            result.Changes.Add(new ImportedChange(
                changeName,
                placeholders.Convert(text, script.FileName),
                revert,
                flags,
                $"flyway V{version} checksum {Crc32.Flyway(text).ToString(CultureInfo.InvariantCulture)}",
                script.FileName));
        }

        foreach (var orphan in undo.Values)
        {
            result.Warnings.Add($"{orphan.FileName}: undo migration without a matching versioned migration; not imported.");
        }

        foreach (var (description, script) in repeatable)
        {
            result.Repeatables.Add(new ImportedRepeatable(description + ".sql", placeholders.Convert(Read(script.Path), script.FileName), script.FileName));
        }

        if (repeatable.Count > 0)
        {
            result.Warnings.Add("'bsm baseline --from flyway' records the repeatable migrations Flyway already applied with the same text; any others run on the first bsm deploy.");
        }

        var irreversible = result.Changes.Count(c => c.Flags.HasFlag(ChangeFlags.Irreversible));
        if (irreversible > 0)
        {
            result.Warnings.Add($"{irreversible} of {result.Changes.Count} change(s) had no undo migration and are marked !irreversible. To add a revert later, write revert.sql and remove the flag before baselining existing databases; afterwards use 'bsm repair'.");
        }

        placeholders.Finish();
        Environments(config, placeholders, options.MapConnection, result);
        return result;
    }

    private static string? FindConfig(string root) =>
        new[] { "flyway.toml", "flyway.conf", "conf/flyway.toml", "conf/flyway.conf" }
            .Select(f => System.IO.Path.Combine(root, f))
            .FirstOrDefault(File.Exists);

    private static IEnumerable<string> Locations(string root, IReadOnlyList<string> configured, ImportedProject result)
    {
        var found = new List<string>();
        foreach (var location in configured)
        {
            if (location.StartsWith("classpath:", StringComparison.OrdinalIgnoreCase))
            {
                var path = System.IO.Path.Combine(root, "src", "main", "resources", location["classpath:".Length..].TrimStart('/'));
                if (Directory.Exists(path))
                {
                    found.Add(path);
                }
                else
                {
                    result.Warnings.Add($"location '{location}' is on the Java classpath; pass the folder with --location.");
                }

                continue;
            }

            var dir = location.StartsWith("filesystem:", StringComparison.OrdinalIgnoreCase) ? location["filesystem:".Length..] : location;
            dir = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(dir) ? dir : System.IO.Path.Combine(root, dir));
            if (Directory.Exists(dir))
            {
                found.Add(dir);
            }
            else
            {
                result.Warnings.Add($"location '{location}' does not exist; skipped.");
            }
        }

        if (found.Count == 0 && configured.Count == 0)
        {
            foreach (var candidate in new[] { "sql", System.IO.Path.Combine("src", "main", "resources", "db", "migration"), System.IO.Path.Combine("db", "migration") })
            {
                var dir = System.IO.Path.Combine(root, candidate);
                if (Directory.Exists(dir))
                {
                    found.Add(dir);
                    break;
                }
            }

            if (found.Count == 0)
            {
                found.Add(root);
            }
        }

        return found;
    }

    private static bool TryVersioned(string stem, string prefix, string separator, out Version version, out string description)
    {
        version = default;
        description = string.Empty;
        if (!stem.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var sep = stem.IndexOf(separator, prefix.Length, StringComparison.Ordinal);
        if (sep < 0)
        {
            return false;
        }

        var raw = stem[prefix.Length..sep];
        if (!VersionRegex().IsMatch(raw))
        {
            return false;
        }

        version = Version.Parse(raw);
        description = stem[(sep + separator.Length)..];
        return true;
    }

    private static FlywayConfig? ScriptConfig(string scriptPath, ImportedProject result)
    {
        var path = scriptPath + ".conf";
        if (!File.Exists(path))
        {
            return null;
        }

        var conf = FlywayConfig.ParseConf(File.ReadAllText(path, Encoding.UTF8));
        foreach (var key in conf.WithPrefix(string.Empty).Select(kv => kv.Key).Where(k => !string.Equals(k, "executeInTransaction", StringComparison.OrdinalIgnoreCase)))
        {
            result.Warnings.Add($"{System.IO.Path.GetFileName(path)}: setting '{key}' has no bsm equivalent; ignored.");
        }

        return conf;
    }

    private static string Read(string path) => File.ReadAllText(path, Encoding.UTF8);

    private static void Environments(FlywayConfig config, Placeholders placeholders, ConnectionMapper? map, ImportedProject result)
    {
        var names = config.EnvironmentNames;
        var connections = new List<(string Name, string Prefix)>();
        if (names.Count > 0)
        {
            connections.AddRange(names.Select(n => (n, $"environments.{n}.")));
        }
        else if (config["flyway.url"] is not null)
        {
            connections.Add(("default", "flyway."));
        }

        foreach (var (name, prefix) in connections)
        {
            var settings = new JsonObject();
            if (config[prefix + "url"] is { Length: > 0 } url)
            {
                if (map is null)
                {
                    result.Warnings.Add($"environment '{name}': no connection mapping available; set host and database by hand.");
                }
                else if (map(new ConnectionToImport(name, url, config[prefix + "user"], config[prefix + "password"]), result.Warnings) is { } mapped)
                {
                    foreach (var (k, v) in mapped)
                    {
                        settings[k] = v?.DeepClone();
                    }
                }
            }

            if (placeholders.EnvironmentVariables(name, prefix) is { Count: > 0 } variables)
            {
                settings["variables"] = variables;
            }

            result.Environments.Add(new ImportedEnvironment(ValidEnvironmentName(name), settings));
        }

        result.DefaultEnvironment = config["flyway.environment"] is { } env && result.Environments.Any(e => e.Name == env)
            ? env
            : result.Environments.FirstOrDefault()?.Name;
    }

    private static string ValidEnvironmentName(string name) => name.TrimStart('_') is { Length: > 0 } n ? n : "default";

    [GeneratedRegex(@"^[0-9]+([._][0-9]+)*$")]
    private static partial Regex VersionRegex();

    private sealed record Syntax(string Versioned, string Undo, string Repeatable, string Baseline, string Separator, IReadOnlyList<string> Suffixes);

    private sealed record Script(string Path, string FileName)
    {
        public string? Description { get; init; }
    }

    /// <summary>A Flyway version: dot- or underscore-separated integers, compared numerically (1.10 &gt; 1.9, 1.0 = 1).</summary>
    public readonly record struct Version(BigInteger[] Parts) : IComparable<Version>
    {
        public static Version Parse(string text) =>
            new(text.Split('.', '_').Select(p => BigInteger.Parse(p, CultureInfo.InvariantCulture)).ToArray());

        public int CompareTo(Version other)
        {
            for (var i = 0; i < Math.Max(Parts.Length, other.Parts.Length); i++)
            {
                var c = (i < Parts.Length ? Parts[i] : BigInteger.Zero).CompareTo(i < other.Parts.Length ? other.Parts[i] : BigInteger.Zero);
                if (c != 0)
                {
                    return c;
                }
            }

            return 0;
        }

        public bool Equals(Version other) => CompareTo(other) == 0;

        public override int GetHashCode() => string.Join('.', Trimmed()).GetHashCode(StringComparison.Ordinal);

        public override string ToString() => string.Join('.', Parts.Select(p => p.ToString(CultureInfo.InvariantCulture)));

        private IEnumerable<BigInteger> Trimmed()
        {
            var last = Parts.Length - 1;
            while (last > 0 && Parts[last].IsZero)
            {
                last--;
            }

            return Parts.Take(last + 1);
        }
    }

    /// <summary>Converts ${name} placeholders and collects the variables they need.</summary>
    private sealed class Placeholders
    {
        private readonly FlywayConfig config;
        private readonly ImportedProject result;
        private readonly bool enabled;
        private readonly Regex token;
        private readonly Dictionary<string, string> global = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        private bool usesUser;

        public Placeholders(FlywayConfig config, ImportedProject result)
        {
            this.config = config;
            this.result = result;
            enabled = config.GetBool("flyway.placeholderReplacement", true);
            token = new Regex(Regex.Escape(config.Get("flyway.placeholderPrefix", "${")) + "(?<name>[^\\r\\n]+?)" + Regex.Escape(config.Get("flyway.placeholderSuffix", "}")), RegexOptions.CultureInvariant);
            foreach (var (key, value) in config.WithPrefix("flyway.placeholders."))
            {
                global[key] = value;
            }

            if (!enabled)
            {
                result.Warnings.Add("flyway.placeholderReplacement is false: placeholders were left as text.");
            }
        }

        public string Convert(string text, string source)
        {
            if (!enabled)
            {
                return result.EscapeLiteral(text, source);
            }

            var sb = new StringBuilder(text.Length);
            var last = 0;
            foreach (Match m in token.Matches(text))
            {
                sb.Append(result.EscapeLiteral(text[last..m.Index], source));
                sb.Append('{').Append(Map(m.Groups["name"].Value, source)).Append('}');
                last = m.Index + m.Length;
            }

            sb.Append(result.EscapeLiteral(text[last..], source));
            return sb.ToString();
        }

        /// <summary>Defines the shared variables that scripts use; reports those with no value anywhere.</summary>
        public void Finish()
        {
            foreach (var name in used)
            {
                var variable = ImportedProject.VariableName(name, "flyway");
                if (global.TryGetValue(name, out var value))
                {
                    result.Variables[variable] = result.EscapeLiteral(value, $"placeholder {name}");
                }
                else if (!config.EnvironmentNames.All(e => EnvironmentValue(e, name) is not null) || config.EnvironmentNames.Count == 0)
                {
                    result.UndefinedVariables.Add(variable);
                }
            }

            if (usesUser)
            {
                if (config["flyway.user"] is { } user)
                {
                    result.Variables["flyway.user"] = user;
                }
                else if (config.EnvironmentNames.Count == 0 || config.EnvironmentNames.Any(e => config[$"environments.{e}.user"] is null))
                {
                    result.UndefinedVariables.Add("flyway.user");
                    Note("${flyway:user} became {flyway.user}; set it to the database user in bsm.yaml.");
                }
            }

            foreach (var (name, _) in global.Where(kv => !used.Contains(kv.Key)))
            {
                result.Warnings.Add($"placeholder '{name}' is defined but no script uses it; not imported.");
            }
        }

        public JsonObject EnvironmentVariables(string environment, string prefix)
        {
            var variables = new JsonObject();
            foreach (var name in used)
            {
                if (EnvironmentValue(environment, name) is { } value)
                {
                    variables[ImportedProject.VariableName(name, "flyway")] = result.EscapeLiteral(value, $"placeholder {name}");
                }
            }

            if (usesUser && prefix != "flyway." && config[prefix + "user"] is { } user)
            {
                variables["flyway.user"] = user;
            }

            return variables;
        }

        private string? EnvironmentValue(string environment, string name) =>
            config[$"environments.{environment}.flyway.placeholders.{name}"] ?? config[$"environments.{environment}.placeholders.{name}"];

        private string Map(string name, string source)
        {
            if (!name.StartsWith("flyway:", StringComparison.OrdinalIgnoreCase))
            {
                used.Add(name);
                return ImportedProject.VariableName(name, "flyway");
            }

            switch (name["flyway:".Length..].ToLowerInvariant())
            {
                case "database":
                    return "db";
                case "environment":
                    return "env";
                case "timestamp":
                    Note($"${{flyway:timestamp}} became {{deploy.utc}} (ISO 8601 UTC, e.g. 2026-11-03T14:10:00Z; Flyway wrote local 'yyyy-MM-dd HH:mm:ss').");
                    return "deploy.utc";
                case "filename":
                    Note("${flyway:filename} became {change}, the bsm change or repeatable name, not the original file name.");
                    return "change";
                case "defaultschema":
                    result.Variables["flyway.defaultSchema"] = config["flyway.defaultSchema"] ?? config.GetList("flyway.schemas").FirstOrDefault() ?? "dbo";
                    return "flyway.defaultSchema";
                case "table":
                    result.Variables["flyway.table"] = config.Get("flyway.table", "flyway_schema_history");
                    return "flyway.table";
                case "user":
                    usesUser = true;
                    return "flyway.user";
                case "workingdirectory":
                    result.Variables["flyway.workingDirectory"] = ".";
                    Note("${flyway:workingDirectory} became {flyway.workingDirectory} (set to '.'); check its value.");
                    return "flyway.workingDirectory";
                default:
                    var variable = ImportedProject.VariableName(name.Replace(':', '.'), "flyway");
                    result.UndefinedVariables.Add(variable);
                    Note($"{source}: ${{{name}}} has no bsm equivalent; it became {{{variable}}}, which you must define.");
                    return variable;
            }
        }

        private void Note(string message)
        {
            if (!result.Warnings.Contains(message))
            {
                result.Warnings.Add(message);
            }
        }
    }
}
