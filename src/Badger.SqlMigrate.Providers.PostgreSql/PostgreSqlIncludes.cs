using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>
/// psql's file commands, expanded when the project loads so the files become part of the change (hashed, stored in
/// the journal, bundled):
/// <list type="bullet">
/// <item>\i and \include: a file relative to the project folder (psql's working directory when Sqitch runs it).</item>
/// <item>\ir and \include_relative: a file relative to the including script.</item>
/// <item>\copy table [(columns)] from 'file' [options]: COPY ... FROM STDIN with the file's contents as data.</item>
/// </list>
/// Included files may include others; every file must be inside the project folder.
/// </summary>
public sealed partial class PostgreSqlIncludes : IScriptIncludes
{
    private const int MaxDepth = 16;

    public string Expand(string script, string scriptPath, string projectRoot, ISet<string> included) =>
        Expand(script, Path.GetFullPath(scriptPath), Path.GetFullPath(projectRoot), included, []);

    private static string Expand(string script, string scriptPath, string root, ISet<string> included, List<string> stack)
    {
        var metas = PostgreSqlStatementSplitter.MetaCommands(script);
        if (metas.Count == 0)
        {
            return script;
        }

        var sb = new StringBuilder();
        var last = 0;
        foreach (var (start, end, line) in metas)
        {
            var text = script[start..end].TrimEnd('\r').Trim();
            string? replacement = null;
            if (IncludeRegex().Match(text) is { Success: true } inc)
            {
                var relative = inc.Groups["cmd"].Value is "ir" or "include_relative";
                var file = Resolve(Unquote(inc.Groups["path"].Value), relative ? Path.GetDirectoryName(scriptPath)! : root, root, line);
                if (stack.Contains(file, StringComparer.Ordinal) || stack.Count >= MaxDepth)
                {
                    throw new MigrationException($"line {line}: {text} includes itself (directly or through other files).");
                }

                included.Add(file);
                var content = Read(file, line);
                replacement = Expand(content, file, root, included, [.. stack, scriptPath]).TrimEnd() + "\n";
            }
            else if (CopyRegex().Match(text) is { Success: true } copy)
            {
                var source = copy.Groups["source"].Value;
                var options = copy.Groups["options"].Value.Trim().TrimEnd(';');
                var statement = $"COPY {copy.Groups["target"].Value.Trim()} FROM STDIN{(options.Length == 0 ? string.Empty : " " + options)};";
                if (source.Equals("stdin", StringComparison.OrdinalIgnoreCase) || source.Equals("pstdin", StringComparison.OrdinalIgnoreCase))
                {
                    replacement = statement + "\n";
                }
                else if (source.StartsWith("program", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MigrationException($"line {line}: \\copy from a program runs a shell command; copy from a file instead.");
                }
                else
                {
                    var file = Resolve(Unquote(source), root, root, line);
                    included.Add(file);
                    var data = Read(file, line).Replace("\r\n", "\n", StringComparison.Ordinal);
                    replacement = statement + "\n" + data + (data.EndsWith('\n') || data.Length == 0 ? string.Empty : "\n") + "\\.\n";
                }
            }

            if (replacement is null)
            {
                continue;
            }

            sb.Append(script, last, start - last).Append(replacement);
            last = end < script.Length && script[end] == '\n' ? end + 1 : end;
        }

        sb.Append(script, last, script.Length - last);
        return sb.ToString();
    }

    private static string Resolve(string path, string baseDir, string root, int line)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
        var inside = full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (!inside)
        {
            throw new MigrationException($"line {line}: '{path}' is outside the project folder; included files must be inside it so they are versioned and bundled with the change.");
        }

        if (!File.Exists(full))
        {
            throw new MigrationException($"line {line}: '{path}' does not exist.");
        }

        return full;
    }

    private static string Read(string file, int line)
    {
        try
        {
            return File.ReadAllText(file, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            throw new MigrationException($"line {line}: cannot read '{file}': {ex.Message}", ex);
        }
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1].Replace("''", "'", StringComparison.Ordinal) : value;

    [GeneratedRegex(@"^\\(?<cmd>i|ir|include|include_relative)\s+(?<path>'(?:[^']|'')*'|\S+)\s*$")]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(@"^\\copy\s+(?<target>.+?)\s+from\s+(?<source>'(?:[^']|'')*'|program\b.*|\S+)(?<options>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyRegex();
}
