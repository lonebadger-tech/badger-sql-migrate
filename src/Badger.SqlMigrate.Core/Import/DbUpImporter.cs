using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Import;

public sealed record DbUpImportOptions
{
    /// <summary>The folder holding the scripts (embedded resources or WithScriptsFromFileSystem).</summary>
    public required string Path { get; init; }

    /// <summary>
    /// Script names are file names only (WithScriptsFromFileSystem without subfolders). Default: the path with
    /// folders joined by '.', the way embedded resource names end.
    /// </summary>
    public bool FileNamesOnly { get; init; }

    /// <summary>Globs (relative paths, * and **) of scripts DbUp runs every time (RunAlways): imported as repeatables.</summary>
    public IReadOnlyList<string> RunAlways { get; init; } = [];

    /// <summary>Values for $name$ variables, which DbUp sets in code.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    /// <summary>The project called WithVariablesDisabled(): leave $name$ text alone.</summary>
    public bool VariablesDisabled { get; init; }
}

/// <summary>
/// Converts DbUp scripts into bsm's layout. DbUp runs scripts it has not run before, ordered by script name
/// (ordinal), and records only the name. Each script becomes a change in that order with the note "dbup &lt;name&gt;";
/// DbUp has no down scripts, so every change is !irreversible. $name$ variables become {name}.
/// </summary>
public static partial class DbUpImporter
{
    public const string Tool = "DbUp";

    public static ImportedProject Import(DbUpImportOptions options)
    {
        var root = System.IO.Path.GetFullPath(options.Path);
        if (!Directory.Exists(root))
        {
            throw new MigrationException($"'{options.Path}' is not a folder.");
        }

        var result = new ImportedProject { Tool = Tool };
        var runAlways = options.RunAlways.Select(Glob).ToList();
        var scripts = Directory.EnumerateFiles(root, "*.sql", options.FileNamesOnly ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
                return (Path: path, Relative: relative, Name: options.FileNamesOnly ? System.IO.Path.GetFileName(path) : relative.Replace('/', '.'));
            })
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        if (scripts.Count == 0)
        {
            throw new MigrationException($"No .sql files found under '{options.Path}'.");
        }

        foreach (var duplicate in scripts.GroupBy(s => s.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            throw new MigrationException($"Two scripts have the DbUp name '{duplicate.Key}'.");
        }

        var used = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var script in scripts)
        {
            var text = Convert(File.ReadAllText(script.Path, Encoding.UTF8), script.Name, options, result, used);
            if (runAlways.Any(r => r.IsMatch(script.Relative)))
            {
                result.Repeatables.Add(new ImportedRepeatable(script.Relative, text, script.Name));
                continue;
            }

            var stem = script.Name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? script.Name[..^4] : script.Name;
            var name = result.UniqueName(stem, "script", null);
            result.Changes.Add(new ImportedChange(name, text, null, ChangeFlags.Irreversible, $"dbup {script.Name}", script.Name));
        }

        foreach (var variable in used)
        {
            var bsmName = ImportedProject.VariableName(variable, "dbup");
            if (options.Variables.TryGetValue(variable, out var value))
            {
                result.Variables[bsmName] = result.EscapeLiteral(value, $"variable {variable}");
            }
            else
            {
                result.UndefinedVariables.Add(bsmName);
            }
        }

        if (result.UndefinedVariables.Count > 0)
        {
            result.Warnings.Add($"DbUp sets $variables$ in code, so their values are unknown: {string.Join(", ", result.UndefinedVariables)}. Define them under 'variables:' in bsm.yaml (or import with --var name=value).");
        }

        result.Warnings.Add("DbUp has no down scripts, so every change is !irreversible. To add a revert, write revert.sql and remove the flag before baselining existing databases; afterwards use 'bsm repair'.");
        result.Warnings.Add("DbUp runs scripts without a transaction unless configured to; bsm runs each change in one. 'bsm plan check' lists statements that need !notx.");
        if (runAlways.Count > 0 && result.Repeatables.Count == 0)
        {
            result.Warnings.Add("no script matched --run-always.");
        }
        else if (result.Repeatables.Count > 0)
        {
            result.Warnings.Add("run-always scripts became repeatables: bsm runs them when they change, not on every deploy.");
        }

        return result;
    }

    private static string Convert(string text, string source, DbUpImportOptions options, ImportedProject result, SortedSet<string> used)
    {
        if (options.VariablesDisabled)
        {
            return result.EscapeLiteral(text, source);
        }

        var sb = new StringBuilder(text.Length);
        var last = 0;
        foreach (Match m in VariableRegex().Matches(text))
        {
            sb.Append(result.EscapeLiteral(text[last..m.Index], source));
            used.Add(m.Groups["name"].Value);
            sb.Append('{').Append(ImportedProject.VariableName(m.Groups["name"].Value, "dbup")).Append('}');
            last = m.Index + m.Length;
        }

        sb.Append(result.EscapeLiteral(text[last..], source));
        return sb.ToString();
    }

    /// <summary>Relative-path glob: ** any folders, * any characters within a name, ? one character.</summary>
    private static Regex Glob(string glob)
    {
        var pattern = Regex.Escape(glob.Replace('\\', '/')).Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal).Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal);
        return new Regex($"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>DbUp's default variable syntax: $name$.</summary>
    [GeneratedRegex(@"\$(?<name>\w+)\$")]
    private static partial Regex VariableRegex();
}
