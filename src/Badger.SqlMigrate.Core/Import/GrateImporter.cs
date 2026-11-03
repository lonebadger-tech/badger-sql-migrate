using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Import;

public sealed record GrateImportOptions
{
    /// <summary>grate's --sqlfilesdirectory: the folder holding up/, views/, sprocs/ ...</summary>
    public required string Path { get; init; }

    /// <summary>Renamed folders, as grate's KnownFolderNames: key (up, views, ...) to folder name.</summary>
    public IReadOnlyDictionary<string, string> Folders { get; init; } = new Dictionary<string, string>();

    /// <summary>grate --ut user tokens: name to value.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; init; } = new Dictionary<string, string>();

    /// <summary>Environment names used in *.ENV.* file names (grate --env values). Inferred from upper-case name parts when empty.</summary>
    public IReadOnlyList<string> Environments { get; init; } = [];

    /// <summary>grate --ignoredirectorynames: script names are file names only.</summary>
    public bool IgnoreDirectoryNames { get; init; }

    /// <summary>grate --disabletokenreplacement.</summary>
    public bool TokenReplacementDisabled { get; init; }
}

/// <summary>
/// Converts a grate (or RoundhousE) scripts folder into bsm's layout, following grate's run order:
/// <list type="bullet">
/// <item>runAfterCreateDatabase/ and alterDatabase/: changes at the start of the plan (alterDatabase ones !notx).</item>
/// <item>up/: one change per script, ordered as grate orders them; note "grate up/&lt;name&gt;".</item>
/// <item>runBeforeUp/, runFirstAfterUp/, functions/, views/, sprocs/, triggers/, indexes/,
/// runAfterOtherAnyTimeScripts/ (anytime) and beforeMigration/, permissions/, afterMigration/ (everytime):
/// repeatables under numbered folders that keep grate's folder order.</item>
/// <item>*.ENV.* scripts: environments/&lt;name&gt;/ for each environment in the file name.</item>
/// <item>{{Token}}: {variables}; grate's built-ins map to bsm built-ins where one exists.</item>
/// </list>
/// grate has no down scripts, so changes are !irreversible.
/// </summary>
public static partial class GrateImporter
{
    public const string Tool = "grate";

    private enum Kind
    {
        Change,
        NoTransactionChange,
        Repeatable,
    }

    /// <summary>grate's folders in run order, with how bsm imports each.</summary>
    private static readonly (string Key, Kind Kind, string Type)[] Folders =
    [
        ("beforeMigration", Kind.Repeatable, "everytime"),
        ("alterDatabase", Kind.NoTransactionChange, "anytime"),
        ("runAfterCreateDatabase", Kind.Change, "anytime"),
        ("runBeforeUp", Kind.Repeatable, "anytime"),
        ("up", Kind.Change, "once"),
        ("runFirstAfterUp", Kind.Repeatable, "anytime"),
        ("functions", Kind.Repeatable, "anytime"),
        ("views", Kind.Repeatable, "anytime"),
        ("sprocs", Kind.Repeatable, "anytime"),
        ("triggers", Kind.Repeatable, "anytime"),
        ("indexes", Kind.Repeatable, "anytime"),
        ("runAfterOtherAnyTimeScripts", Kind.Repeatable, "anytime"),
        ("permissions", Kind.Repeatable, "everytime"),
        ("afterMigration", Kind.Repeatable, "everytime"),
    ];

    public static ImportedProject Import(GrateImportOptions options)
    {
        var root = System.IO.Path.GetFullPath(options.Path);
        if (!Directory.Exists(root))
        {
            throw new MigrationException($"'{options.Path}' is not a folder.");
        }

        var result = new ImportedProject { Tool = Tool };
        var tokens = new Tokens(options, result);
        var environmentNames = new SortedSet<string>(options.Environments, StringComparer.OrdinalIgnoreCase);
        var frontChanges = new List<ImportedChange>();
        var found = 0;

        // Changes in plan order: runAfterCreateDatabase and alterDatabase first (they run before up/ in grate), then up/.
        foreach (var (key, kind, type) in Folders.OrderBy(f => f.Key == "up" ? 1 : 0))
        {
            var folderName = options.Folders.TryGetValue(key, out var renamed) ? renamed : key;
            var dir = System.IO.Path.Combine(root, folderName);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var index = Array.FindIndex(Folders, f => f.Key == key);
            foreach (var file in Scripts(dir, options.IgnoreDirectoryNames, result))
            {
                found++;
                var text = File.ReadAllText(file.Path, Encoding.UTF8);
                var source = $"{folderName}/{file.Relative}";
                var converted = tokens.Convert(text, source);
                if (IsEnvironmentFile(file.Relative))
                {
                    var targets = EnvironmentsOf(file.Relative, options.Environments).ToList();
                    if (targets.Count == 0)
                    {
                        result.Warnings.Add($"{source}: no environment name recognised in the file name; not imported. Pass --environment names.");
                        continue;
                    }

                    foreach (var environment in targets)
                    {
                        environmentNames.Add(environment);
                        result.Repeatables.Add(new ImportedRepeatable($"{ImportedProject.EnvironmentScriptPrefix}{environment}/{index:00}-{key}/{file.Relative}", converted, source));
                    }

                    if (kind != Kind.Repeatable)
                    {
                        result.Warnings.Add($"{source}: an environment-specific {type} script became an environment script, which bsm runs again when it changes.");
                    }

                    continue;
                }

                if (kind == Kind.Repeatable || IsEverytimeFile(file.Relative))
                {
                    result.Repeatables.Add(new ImportedRepeatable($"{index:00}-{key}/{file.Relative}", converted, source));
                    continue;
                }

                var name = result.UniqueName(System.IO.Path.ChangeExtension(file.Relative, null).Replace('/', '-'), "script", key.ToLowerInvariant());
                var flags = ChangeFlags.Irreversible | (kind == Kind.NoTransactionChange || OwnsTransaction(converted) ? ChangeFlags.NoTransaction : ChangeFlags.None);
                var change = new ImportedChange(name, converted, null, flags, $"grate {key}/{file.ScriptName}", source);
                if (key == "up")
                {
                    result.Changes.Add(change);
                }
                else
                {
                    frontChanges.Add(change);
                }
            }
        }

        if (found == 0)
        {
            throw new MigrationException($"No grate scripts found under '{options.Path}'. Pass the folder holding up/, views/, sprocs/ ...");
        }

        result.Changes.InsertRange(0, frontChanges);
        if (frontChanges.Count > 0)
        {
            result.Warnings.Add($"{frontChanges.Count} runAfterCreateDatabase/alterDatabase script(s) became the first changes: they run once per database, not again when edited.");
        }

        Warn(result, root, options);
        foreach (var name in environmentNames)
        {
            result.Environments.Add(new ImportedEnvironment(name, new JsonObject()));
        }

        if (environmentNames.Count > 0)
        {
            result.Warnings.Add($"environments {string.Join(", ", environmentNames)} were created for the *.ENV.* scripts; add their connection settings to bsm.yaml.");
        }

        tokens.Finish();
        return result;
    }

    /// <summary>
    /// grate's text_hash: base64 MD5 of the script (after token replacement) with line endings normalized to LF and
    /// quotes doubled. Older RoundhousE rows hashed the text as written, so CRLF and CR forms also match.
    /// </summary>
    public static bool HashMatches(string renderedScript, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var normalized = renderedScript.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Hash(normalized) == storedHash || Hash(renderedScript) == storedHash
            || Hash(normalized.Replace("\n", "\r\n", StringComparison.Ordinal)) == storedHash || Hash(normalized.Replace('\n', '\r')) == storedHash;

        static string Hash(string text) => Convert.ToBase64String(MD5.HashData(Encoding.UTF8.GetBytes(text.Replace("'", "''", StringComparison.Ordinal))));
    }

    private static IEnumerable<(string Path, string Relative, string ScriptName)> Scripts(string dir, bool ignoreDirectoryNames, ImportedProject result)
    {
        var files = Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: System.IO.Path.GetRelativePath(dir, path).Replace('\\', '/')))
            .ToList();
        var key = ignoreDirectoryNames
            ? (Func<(string Path, string Relative), string>)(f => System.IO.Path.GetFileNameWithoutExtension(f.Path))
            : f => System.IO.Path.ChangeExtension(f.Relative, null);
        var ordered = files.OrderBy(key, StringComparer.InvariantCultureIgnoreCase).ToList();
        if (!ordered.Select(f => f.Relative).SequenceEqual(files.OrderBy(f => f.Relative, StringComparer.Ordinal).Select(f => f.Relative)))
        {
            result.Warnings.Add($"{System.IO.Path.GetFileName(dir)}/: grate orders these scripts ignoring case; check the order in bsm.plan (repeatables run in path order).");
        }

        return ordered.Select(f => (f.Path, f.Relative, ignoreDirectoryNames ? System.IO.Path.GetFileName(f.Path) : f.Relative));
    }

    private static bool IsEnvironmentFile(string name) => System.IO.Path.GetFileName(name).Contains(".ENV.", StringComparison.InvariantCultureIgnoreCase);

    private static bool IsEverytimeFile(string name)
    {
        var file = System.IO.Path.GetFileName(name);
        return file.StartsWith("everytime.", StringComparison.InvariantCultureIgnoreCase) || file.Contains(".everytime.", StringComparison.InvariantCultureIgnoreCase);
    }

    /// <summary>
    /// The environments a *.ENV.* file is for. grate checks the file name against --env at run time; here the
    /// names come from --environment, or are the upper-case parts of the name ("LOCAL.grants.ENV.sql").
    /// </summary>
    private static IEnumerable<string> EnvironmentsOf(string relative, IReadOnlyList<string> known)
    {
        var parts = System.IO.Path.GetFileNameWithoutExtension(relative).Split('.');
        return known.Count > 0
            ? known.Where(e => parts.Contains(e, StringComparer.InvariantCultureIgnoreCase))
            : parts.Where(p => !p.Equals("ENV", StringComparison.InvariantCultureIgnoreCase) && UpperRegex().IsMatch(p)).Distinct(StringComparer.Ordinal);
    }

    private static bool OwnsTransaction(string sql) => BeginTransactionRegex().IsMatch(sql);

    private static void Warn(ImportedProject result, string root, GrateImportOptions options)
    {
        var known = Folders.Select(f => options.Folders.TryGetValue(f.Key, out var renamed) ? renamed : f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in Directory.EnumerateDirectories(root).Select(System.IO.Path.GetFileName).Where(d => !known.Contains(d!)).Order(StringComparer.Ordinal))
        {
            result.Warnings.Add(other is "createDatabase" or "dropDatabase"
                ? $"{other}/: bsm does not create or drop databases; not imported."
                : $"{other}/: not a grate folder; not imported (use --folder key=name if it is a renamed one).");
        }

        if (result.Changes.Count > 0)
        {
            result.Warnings.Add("grate has no down scripts, so every change is !irreversible. To add a revert, write revert.sql and remove the flag before baselining existing databases; afterwards use 'bsm repair'.");
        }

        if (result.Repeatables.Any(r => r.Path.Contains("-beforeMigration/", StringComparison.Ordinal) || r.Path.Contains("-permissions/", StringComparison.Ordinal) || r.Path.Contains("-afterMigration/", StringComparison.Ordinal) || IsEverytimeFile(r.Path)))
        {
            result.Warnings.Add("everytime scripts (beforeMigration/, permissions/, afterMigration/, everytime.*) became repeatables: bsm runs them when they change, not on every deploy.");
        }

        if (result.Repeatables.Any(r => r.Path.Contains("-runBeforeUp/", StringComparison.Ordinal)))
        {
            result.Warnings.Add("runBeforeUp/ scripts became repeatables, which run after the changes; if one must run before them, turn it into a change.");
        }
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9_\-]*$")]
    private static partial Regex UpperRegex();

    [GeneratedRegex(@"^\s*BEGIN\s+TRAN(SACTION)?\b", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BeginTransactionRegex();

    /// <summary>Converts {{Token}} and collects the variables needed.</summary>
    private sealed partial class Tokens(GrateImportOptions options, ImportedProject result)
    {
        private readonly SortedSet<string> used = new(StringComparer.OrdinalIgnoreCase);

        public string Convert(string text, string source)
        {
            if (options.TokenReplacementDisabled)
            {
                return result.EscapeLiteral(text, source);
            }

            var sb = new StringBuilder(text.Length);
            var last = 0;
            foreach (Match m in TokenRegex().Matches(text))
            {
                sb.Append(result.EscapeLiteral(text[last..m.Index], source));
                sb.Append('{').Append(Map(m.Groups["key"].Value, source)).Append('}');
                last = m.Index + m.Length;
            }

            sb.Append(result.EscapeLiteral(text[last..], source));
            return sb.ToString();
        }

        public void Finish()
        {
            foreach (var token in used)
            {
                var name = ImportedProject.VariableName(token, "grate");
                if (options.Tokens.FirstOrDefault(t => string.Equals(t.Key, token, StringComparison.OrdinalIgnoreCase)) is { Key: not null } provided)
                {
                    result.Variables[name] = result.EscapeLiteral(provided.Value, $"token {token}");
                }
                else
                {
                    result.UndefinedVariables.Add(name);
                }
            }

            if (result.UndefinedVariables.Count > 0)
            {
                result.Warnings.Add($"grate takes user tokens on the command line (--ut), so their values are unknown: {string.Join(", ", result.UndefinedVariables)}. Define them in bsm.yaml, or import with --var name=value.");
            }
        }

        private string Map(string token, string source)
        {
            switch (token.ToLowerInvariant())
            {
                case "databasename":
                    return "db";
                case "servername":
                    return "db.host";
                case "environmentname" or "environmentnames":
                    return "env";
                case "schemaname":
                    result.Variables["grate.SchemaName"] = "grate";
                    return "grate.SchemaName";
                case "scriptsruntablename" or "scriptsrunerrorstablename" or "versiontablename":
                    var table = token.ToLowerInvariant() switch { "scriptsruntablename" => "ScriptsRun", "scriptsrunerrorstablename" => "ScriptsRunErrors", _ => "Version" };
                    result.Variables[$"grate.{token}"] = $"grate.{table}";
                    return $"grate.{token}";
                case "connectionstring" or "connectionstringadmin":
                    result.UndefinedVariables.Add($"grate.{token}");
                    Note($"{source}: {{{{{token}}}}} became {{grate.{token}}}; bsm never puts connection strings into scripts, so set a value yourself if you really need it.");
                    return $"grate.{token}";
            }

            if (token.EndsWith("FolderName", StringComparison.OrdinalIgnoreCase))
            {
                var key = token[..^"FolderName".Length];
                var folder = Folders.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key ?? key;
                result.Variables[$"grate.{token}"] = options.Folders.TryGetValue(folder, out var renamed) ? renamed : folder;
                return $"grate.{token}";
            }

            if (BuiltInSettingRegex().IsMatch(token))
            {
                result.UndefinedVariables.Add($"grate.{token}");
                Note($"{source}: grate setting token {{{{{token}}}}} became {{grate.{token}}}, which you must define.");
                return $"grate.{token}";
            }

            used.Add(token);
            return ImportedProject.VariableName(token, "grate");
        }

        private void Note(string message)
        {
            if (!result.Warnings.Contains(message))
            {
                result.Warnings.Add(message);
            }
        }

        [GeneratedRegex(@"\{\{(?<key>\w+)\}\}")]
        private static partial Regex TokenRegex();

        [GeneratedRegex(@"^(Baseline|CommandTimeout|CommandTimeoutAdmin|DatabaseType|DisableTokenReplacement|DoNotAlterDatabase|DoNotCreateDatabase|DoNotStoreScriptsRunText|Drop|DryRun|OutputPath|RepositoryPath|Restore|RunAllAnyTimeScripts|Silent|SqlFilesDirectory|Version|WarnAndIgnoreOnOneTimeScriptChanges|WarnOnOneTimeScriptChanges|WithTransaction)$", RegexOptions.IgnoreCase)]
        private static partial Regex BuiltInSettingRegex();
    }
}
