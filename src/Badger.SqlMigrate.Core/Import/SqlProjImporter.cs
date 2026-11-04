using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Badger.SqlMigrate.Core.Plan;
using Badger.SqlMigrate.Core.Preprocessing;

namespace Badger.SqlMigrate.Core.Import;

public sealed record SqlProjImportOptions
{
    /// <summary>The .sqlproj file, or the folder holding exactly one.</summary>
    public required string Path { get; init; }

    /// <summary>Reads the scripts (the SQL Server provider's ScriptDom parser).</summary>
    public required ISqlObjectParser Parser { get; init; }
}

/// <summary>
/// Converts an SSDT / SQL Database Project (.sqlproj, classic or SDK-style Microsoft.Build.Sql) into bsm's layout.
/// The project is a desired state, so each object becomes its own piece, in dependency order:
/// <list type="bullet">
/// <item>Schemas, tables, types, sequences, synonyms, users, roles, indexes, constraints and other state: one change
/// per object (changes/create-&lt;schema&gt;-&lt;name&gt;/), with a generated revert and [requires] on what it needs.</item>
/// <item>Views, procedures, functions and triggers: repeatables (repeatable/&lt;kind&gt;/&lt;schema&gt;.&lt;name&gt;.sql)
/// as CREATE OR ALTER, with -- bsm:requires for references between them. One a change needs (a function used by a
/// computed column, an indexed view) becomes a change instead.</item>
/// <item>Standalone GRANT and role memberships: repeatables under permissions/ (they are safe to run again).</item>
/// <item>The post-deployment script: a repeatable (post-deploy/), with :r includes inlined.</item>
/// <item>SqlCmdVariables: bsm variables ($(name) keeps working); $(DatabaseName) becomes $(db).</item>
/// </list>
/// Cycles (tables whose foreign keys reference each other) are broken by moving those foreign keys into a later change.
/// Notes start with "sqlproj " so 'bsm baseline --from sqlproj' can adopt databases deployed from the dacpac.
/// </summary>
public static partial class SqlProjImporter
{
    public const string Tool = "sqlproj";

    /// <summary>Every imported change's note starts with this.</summary>
    public const string NotePrefix = "sqlproj ";

    /// <summary>Variables SSDT defines for every publish.</summary>
    private static readonly string[] PublishVariables = ["DefaultDataPath", "DefaultLogPath", "DefaultFilePrefix"];

    public static ImportedProject Import(SqlProjImportOptions options)
    {
        var projectFile = FindProjectFile(options.Path);
        var projectDir = System.IO.Path.GetDirectoryName(projectFile)!;
        XDocument xml;
        try
        {
            xml = XDocument.Load(projectFile);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new MigrationException($"'{projectFile}' is not a valid project file: {ex.Message}", ex);
        }

        var result = new ImportedProject { Tool = Tool };
        var project = new SqlProject(xml, projectDir);
        var variables = ReadVariables(project, result);
        var context = new Context(options.Parser, result, projectDir, project.Property("DefaultSchema") ?? "dbo", variables);

        var postDeploy = project.Files("PostDeploy", result).ToList();
        var preDeploy = project.Files("PreDeploy", result).ToList();
        var deployScripts = new List<(string Path, string Text, List<string> Included)>();
        foreach (var path in postDeploy)
        {
            var included = new List<string>();
            deployScripts.Add((path, Inline(path, projectDir, included, result, 0), included));
        }

        var excluded = new HashSet<string>(postDeploy.Concat(preDeploy), StringComparer.OrdinalIgnoreCase);
        foreach (var path in preDeploy)
        {
            var included = new List<string>();
            Inline(path, projectDir, included, result, 0);
            excluded.UnionWith(included);
            result.Warnings.Add($"{Relative(projectDir, path)}: the pre-deployment script was not imported. bsm has no step before changes; turn its work into a change ('bsm add') if the database still needs it.");
        }

        excluded.UnionWith(deployScripts.SelectMany(d => d.Included));
        var buildFiles = project.BuildFiles(result).Where(f => !excluded.Contains(f)).ToList();
        if (buildFiles.Count == 0)
        {
            throw new MigrationException($"'{projectFile}' builds no .sql scripts.");
        }

        foreach (var file in buildFiles)
        {
            context.AddFile(file);
        }

        context.Build();
        foreach (var (path, text, _) in deployScripts)
        {
            context.AddPostDeploy(Relative(projectDir, path), text);
        }

        WarnReferences(project, result);
        context.FinishVariables();
        return result;
    }

    /// <summary>The last imported change in a plan (by its "sqlproj " note), which 'bsm baseline --from sqlproj' records up to.</summary>
    public static string? BaselineTarget(Project.MigrationProject project) =>
        project.Changes.LastOrDefault(c => c.Plan.Note?.StartsWith(NotePrefix, StringComparison.Ordinal) == true)?.Name;

    /// <summary>The tables the imported changes create ("sqlproj file: table dbo.Customer" notes), as schema.name.</summary>
    public static IReadOnlyList<string> ImportedTables(Project.MigrationProject project) =>
        project.Changes
            .Select(c => c.Plan.Note is { } note ? TableNoteRegex().Match(note) : Match.Empty)
            .Where(m => m.Success && !m.Groups["name"].Value.Contains("$(", StringComparison.Ordinal))
            .Select(m => m.Groups["name"].Value)
            .ToList();

    /// <summary>
    /// Which of the imported tables a database has, given its table names as schema.name, so 'bsm baseline --from
    /// sqlproj' only adopts a database that was really published from the project.
    /// </summary>
    public static (IReadOnlyList<string> Present, IReadOnlyList<string> Missing) CheckTables(Project.MigrationProject project, IEnumerable<string> liveTables)
    {
        var live = liveTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tables = ImportedTables(project);
        return (tables.Where(live.Contains).ToList(), tables.Where(t => !live.Contains(t)).ToList());
    }

    private static string FindProjectFile(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        if (File.Exists(full))
        {
            return full;
        }

        if (!Directory.Exists(full))
        {
            throw new MigrationException($"'{path}' is not a .sqlproj file or a folder.");
        }

        var candidates = Directory.GetFiles(full, "*.sqlproj");
        return candidates.Length switch
        {
            1 => candidates[0],
            0 => throw new MigrationException($"No .sqlproj file in '{path}'."),
            _ => throw new MigrationException($"'{path}' holds several .sqlproj files; pass one of them: {string.Join(", ", candidates.Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal))}."),
        };
    }

    private static string Relative(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Reads a script as text: any BOM encoding (SSDT often writes UTF-8 with BOM or UTF-16), line endings as LF.</summary>
    private static string ReadScript(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>A post- or pre-deployment script with its :r includes inlined; sqlcmd commands bsm does not run are commented out.</summary>
    private static string Inline(string path, string projectDir, List<string> included, ImportedProject result, int depth)
    {
        if (depth > 20)
        {
            throw new MigrationException($"{Relative(projectDir, path)}: :r includes nest too deeply (a cycle?).");
        }

        var sb = new StringBuilder();
        foreach (var line in ReadScript(path).Split('\n'))
        {
            var command = SqlCmdCommandRegex().Match(line);
            if (!command.Success)
            {
                sb.Append(line).Append('\n');
                continue;
            }

            var verb = command.Groups["verb"].Value.ToLowerInvariant();
            var argument = command.Groups["arg"].Value.Trim().Trim('"');
            if (verb == "r")
            {
                var target = ProjectPaths.Resolve(System.IO.Path.GetDirectoryName(path)!, argument);
                if (target is null)
                {
                    result.Warnings.Add($"{Relative(projectDir, path)}: ':r {argument}' was not found; left as a comment.");
                    sb.Append("-- ").Append(line).Append('\n');
                    continue;
                }

                included.Add(target);
                sb.Append($"-- :r {argument}\n");
                sb.Append(Inline(target, projectDir, included, result, depth + 1).TrimEnd('\n')).Append('\n');
                continue;
            }

            if (verb == "setvar")
            {
                result.Warnings.Add($"{Relative(projectDir, path)}: ':setvar {argument}' was commented out; define the variable in bsm.yaml instead.");
            }

            sb.Append("-- ").Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static Dictionary<string, string> ReadVariables(SqlProject project, ImportedProject result)
    {
        // SSDT name -> bsm name (renamed when bsm reserves the name).
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in project.Items("SqlCmdVariable"))
        {
            var name = item.Attribute("Include")?.Value.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var bsmName = ImportedProject.VariableName(name, "sqlproj");
            names[name] = bsmName;
            var value = Child(item, "DefaultValue") is { Length: > 0 } d ? d : Child(item, "Value") is { Length: > 0 } v && !v.Contains("$(", StringComparison.Ordinal) ? v : null;
            if (value is null)
            {
                result.UndefinedVariables.Add(bsmName);
            }
            else
            {
                result.Variables[bsmName] = ScriptPreprocessor.EscapeLiteral(value, sqlCmdSyntax: false);
            }

            if (!string.Equals(name, bsmName, StringComparison.Ordinal))
            {
                result.Warnings.Add($"SqlCmdVariable {name} became $({bsmName}): bsm reserves that name.");
            }
        }

        if (result.UndefinedVariables.Count > 0)
        {
            result.Warnings.Add($"these SqlCmdVariables have no default value (they come from the publish profile): {string.Join(", ", result.UndefinedVariables)}. Set them in bsm.yaml, per environment where they differ.");
        }

        return names;
    }

    private static string? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static void WarnReferences(SqlProject project, ImportedProject result)
    {
        foreach (var item in project.Items("ArtifactReference").Concat(project.Items("ProjectReference")).Concat(project.Items("PackageReference")))
        {
            var include = item.Attribute("Include")?.Value ?? string.Empty;
            var file = System.IO.Path.GetFileName(include.Replace('\\', '/'));
            if (Regex.IsMatch(file, @"^(master|msdb|model|tempdb)\.dacpac$", RegexOptions.IgnoreCase) || include.StartsWith("Microsoft.SqlServer.Dacpacs", StringComparison.OrdinalIgnoreCase)
                || (item.Name.LocalName == "PackageReference" && !include.Contains("dacpac", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var database = Child(item, "DatabaseSqlCmdVariable") is { Length: > 0 } dbVar ? $"$({dbVar})" : Child(item, "DatabaseVariableLiteralValue") is { Length: > 0 } literal ? literal : null;
            result.Warnings.Add(database is null
                ? $"{item.Name.LocalName} {file}: a reference to the same database. Its objects are not imported here; import that project too, or deploy it first."
                : $"{item.Name.LocalName} {file}: objects in database {database} are not managed by this project; dependencies on them are not ordered.");
        }

        foreach (var item in project.Items("RefactorLog"))
        {
            result.Warnings.Add($"{item.Attribute("Include")?.Value}: refactor log renames are not carried over. Databases already published have them; new databases get the final names.");
        }
    }

    [GeneratedRegex(@"^\s*:(?<verb>r|setvar|on\s+error|connect|out|error|exit|quit|reset|ed|serverlist|listvar|xml|perftrace|help)\b(?<arg>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SqlCmdCommandRegex();

    [GeneratedRegex(@"^sqlproj .+?: table (?<name>.+)$")]
    private static partial Regex TableNoteRegex();

    [GeneratedRegex(@"\$\((?<name>[A-Za-z_][A-Za-z0-9_.]*)\)")]
    internal static partial Regex VariableRegex();

    /// <summary>The project file: properties and items, in either format.</summary>
    private sealed class SqlProject(XDocument xml, string dir)
    {
        private readonly XElement root = xml.Root!;

        /// <summary>SDK-style projects (Microsoft.Build.Sql) build every .sql file in the folder by default.</summary>
        public bool SdkStyle =>
            (root.Attribute("Sdk")?.Value.Contains("Microsoft.Build.Sql", StringComparison.OrdinalIgnoreCase) ?? false)
            || root.Elements().Any(e => e.Name.LocalName is "Sdk" or "Import" && ((e.Attribute("Name")?.Value ?? e.Attribute("Sdk")?.Value)?.Contains("Microsoft.Build.Sql", StringComparison.OrdinalIgnoreCase) ?? false));

        public string? Property(string name) =>
            root.Elements().Where(e => e.Name.LocalName == "PropertyGroup").SelectMany(g => g.Elements()).LastOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;

        public IEnumerable<XElement> Items(string type) =>
            root.Elements().Where(e => e.Name.LocalName == "ItemGroup").SelectMany(g => g.Elements()).Where(e => e.Name.LocalName == type);

        public IEnumerable<string> Files(string type, ImportedProject result) =>
            Items(type).Select(i => i.Attribute("Include")?.Value).OfType<string>().SelectMany(p => ProjectPaths.Expand(dir, p, result)).Distinct(StringComparer.OrdinalIgnoreCase);

        private IEnumerable<string> Removed(string type, ImportedProject result) =>
            Items(type).Select(i => i.Attribute("Remove")?.Value).OfType<string>().SelectMany(p => ProjectPaths.Expand(dir, p, result));

        /// <summary>Build items in a stable order: explicit Build items, or for SDK-style projects every .sql except None/Remove items.</summary>
        public IEnumerable<string> BuildFiles(ImportedProject result)
        {
            var files = Files("Build", result).ToList();
            if (SdkStyle && !string.Equals(Property("EnableDefaultSqlItems") ?? Property("EnableDefaultItems"), "false", StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*.sql", SearchOption.AllDirectories)
                    .Where(f => !Relative(dir, f).Split('/').Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase))));
            }

            var removed = new HashSet<string>(Removed("Build", result).Concat(Files("None", result)).Concat(Files("Content", result)), StringComparer.OrdinalIgnoreCase);
            return files.Where(f => !removed.Contains(f) && f.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => Relative(dir, f), StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>MSBuild-style item paths ("Tables\*.sql", "**\*.sql") resolved on any OS: '\' is a separator and case is ignored when needed.</summary>
internal static class ProjectPaths
{
    public static IEnumerable<string> Expand(string dir, string include, ImportedProject result)
    {
        foreach (var raw in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = raw.Replace('\\', '/');
            if (pattern.Contains("$(", StringComparison.Ordinal))
            {
                result.Warnings.Add($"item '{raw}' uses an MSBuild property; not imported.");
                continue;
            }

            if (pattern.IndexOfAny(['*', '?']) < 0)
            {
                if (Resolve(dir, pattern) is { } file)
                {
                    yield return file;
                }
                else if (!pattern.EndsWith('/'))
                {
                    result.Warnings.Add($"'{raw}' is listed in the project but does not exist.");
                }

                continue;
            }

            // The folder part before the first wildcard, then a regex for the rest.
            var parts = pattern.Split('/');
            var fixedCount = Array.FindIndex(parts, p => p.IndexOfAny(['*', '?']) >= 0);
            var baseDir = fixedCount == 0 ? dir : ResolveDirectory(dir, string.Join('/', parts.Take(fixedCount)));
            if (baseDir is null || !Directory.Exists(baseDir))
            {
                continue;
            }

            var regex = new Regex("^" + string.Join("/", parts.Skip(fixedCount).Select(p => p == "**" ? "\u0001" : Regex.Escape(p).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal)))
                .Replace("\u0001/", "(?:.*/)?", StringComparison.Ordinal).Replace("\u0001", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase);
            foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (regex.IsMatch(System.IO.Path.GetRelativePath(baseDir, file).Replace('\\', '/')))
                {
                    yield return System.IO.Path.GetFullPath(file);
                }
            }
        }
    }

    /// <summary>A file path relative to <paramref name="dir"/>, matching each part's case-insensitively when the exact path does not exist.</summary>
    public static string? Resolve(string dir, string relative)
    {
        relative = relative.Replace('\\', '/');
        var exact = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, relative));
        if (File.Exists(exact))
        {
            return exact;
        }

        var folder = ResolveDirectory(dir, System.IO.Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty);
        if (folder is null)
        {
            return null;
        }

        var name = System.IO.Path.GetFileName(relative);
        return Directory.EnumerateFiles(folder).FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) is { } match
            ? System.IO.Path.GetFullPath(match)
            : null;
    }

    private static string? ResolveDirectory(string dir, string relative)
    {
        var current = System.IO.Path.GetFullPath(dir);
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = System.IO.Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var exact = System.IO.Path.Combine(current, part);
            if (Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            var match = Directory.Exists(current) ? Directory.EnumerateDirectories(current).FirstOrDefault(d => string.Equals(System.IO.Path.GetFileName(d), part, StringComparison.OrdinalIgnoreCase)) : null;
            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }
}
