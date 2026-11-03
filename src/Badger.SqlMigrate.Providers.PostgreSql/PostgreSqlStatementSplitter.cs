using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>
/// Splits a PostgreSQL script into statements the way psql reads it. Semicolons inside 'strings' (including
/// E'escapes'), "identifiers", -- and nested /* */ comments and $tag$ dollar-quoted bodies do not end a statement,
/// and statements made only of comments are dropped. psql's own syntax works too:
/// <list type="bullet">
/// <item>\set and \unset, and :name, :'name' (a quoted literal), :"name" (a quoted identifier) and :{?name}
/// outside strings and quoted bodies. bsm's variables are available as psql variables.</item>
/// <item>\if, \elif, \else, \endif with boolean values (after interpolation).</item>
/// <item>\echo, \qecho and \warn become server notices; \g ends a statement; \gexec runs a query's results.</item>
/// <item>COPY ... FROM STDIN takes the following lines up to \. as its data.</item>
/// <item>Display commands (\timing, \pset, \x, \d...) are ignored. Commands that would act outside the
/// script (\c, \o, \!, \gset, \prompt ...) are errors.</item>
/// </list>
/// \i, \ir and \copy ... from 'file' are expanded when the project loads (<see cref="PostgreSqlIncludes"/>).
/// </summary>
public sealed partial class PostgreSqlStatementSplitter : IScriptParser
{
    public bool ExpandsText => true;

    public IReadOnlyList<ScriptBatch> Split(string script) => Split(script, new Dictionary<string, string>());

    public IReadOnlyList<ScriptBatch> Split(string script, IReadOnlyDictionary<string, string> variables)
    {
        var result = new Scanner(script, variables, lint: false).Run();
        if (result.Problems.Count > 0)
        {
            var (line, message) = result.Problems[0];
            throw new MigrationException($"line {line}: {message}");
        }

        return result.Batches;
    }

    /// <summary>For the linter: every branch of every \if, and every problem rather than the first.</summary>
    internal ScanResult Analyze(string script) => new Scanner(script, new Dictionary<string, string>(), lint: true).Run();

    /// <summary>Character ranges of meta-commands (a backslash outside any string or body, to the end of its line).</summary>
    internal static IReadOnlyList<(int Start, int End, int Line)> MetaCommands(string script) => new Scanner(script, new Dictionary<string, string>(), lint: true).Run().Metas;

    internal sealed record ScanResult(IReadOnlyList<ScriptBatch> Batches, IReadOnlyList<(int Line, string Message)> Problems, IReadOnlyList<(int Start, int End, int Line)> Metas);

    private sealed class Scanner(string s, IReadOnlyDictionary<string, string> initial, bool lint)
    {
        private readonly Dictionary<string, string> vars = new(initial, StringComparer.Ordinal);
        private readonly List<ScriptBatch> batches = [];
        private readonly List<(int, string)> problems = [];
        private readonly List<(int, int, int)> metas = [];
        private readonly Stack<(bool Active, bool Taken, bool SawElse)> conditions = new();
        private readonly StringBuilder current = new();
        private int i;
        private int line = 1;
        private int startLine;
        private bool hasCode;

        private bool Active => lint || conditions.All(c => c.Active);

        public ScanResult Run()
        {
            while (i < s.Length)
            {
                var c = s[i];
                var next = i + 1 < s.Length ? s[i + 1] : '\0';

                // A backslash outside strings and bodies starts a psql command, at the start of a line or after SQL
                // ("SELECT ... \\gexec"); it runs to the end of the line.
                if (c == '\\')
                {
                    Meta();
                    continue;
                }

                if (c == '-' && next == '-')
                {
                    var end = s.IndexOf('\n', i);
                    Take((end < 0 ? s.Length : end) - i);
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    var depth = 0;
                    do
                    {
                        if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*')
                        {
                            depth++;
                            Take(2);
                        }
                        else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '/')
                        {
                            depth--;
                            Take(2);
                        }
                        else
                        {
                            Take(1);
                        }
                    }
                    while (depth > 0 && i < s.Length);
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    Take(1);
                    continue;
                }

                if (c == ';')
                {
                    // An empty statement (";" alone) is dropped with whatever comments preceded it.
                    Take(1);
                    Flush(executeResults: false);
                    continue;
                }

                Code();
                if (c == '\'')
                {
                    var escapes = i > 0 && (s[i - 1] is 'E' or 'e') && (i < 2 || !IsIdentifierChar(s[i - 2]));
                    Take(1);
                    while (i < s.Length)
                    {
                        if (escapes && s[i] == '\\')
                        {
                            Take(2);
                        }
                        else if (s[i] == '\'' && i + 1 < s.Length && s[i + 1] == '\'')
                        {
                            Take(2);
                        }
                        else if (s[i] == '\'')
                        {
                            Take(1);
                            break;
                        }
                        else
                        {
                            Take(1);
                        }
                    }

                    continue;
                }

                if (c == '"')
                {
                    Take(1);
                    while (i < s.Length)
                    {
                        if (s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"')
                        {
                            Take(2);
                        }
                        else if (s[i] == '"')
                        {
                            Take(1);
                            break;
                        }
                        else
                        {
                            Take(1);
                        }
                    }

                    continue;
                }

                if (c == '$' && (i == 0 || !IsIdentifierChar(s[i - 1])) && DollarTag(s, i) is { } tag)
                {
                    var close = s.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                    Take((close < 0 ? s.Length : close + tag.Length) - i);
                    continue;
                }

                if (c == ':' && next != ':' && (i == 0 || s[i - 1] != ':') && Interpolate())
                {
                    continue;
                }

                Take(1);
            }

            Flush(executeResults: false);
            if (conditions.Count > 0)
            {
                Problem(line, "\\if without \\endif.");
            }

            return new ScanResult(batches, problems, metas);
        }

        private void Code()
        {
            if (!hasCode)
            {
                hasCode = true;
                startLine = line;
            }
        }

        private void Take(int count)
        {
            for (var k = 0; k < count && i < s.Length; k++, i++)
            {
                if (s[i] == '\n')
                {
                    line++;
                }

                current.Append(s[i]);
            }
        }

        private void Flush(bool executeResults)
        {
            var text = current.ToString().Trim();
            var keep = hasCode && Active;
            var at = startLine;
            current.Clear();
            hasCode = false;
            startLine = 0;
            if (!keep)
            {
                if (hasCopyData(text))
                {
                    ReadCopyData();
                }

                return;
            }

            var batch = new ScriptBatch(text, at) { ExecuteResults = executeResults };
            if (hasCopyData(text))
            {
                batch = batch with { CopyData = ReadCopyData() };
            }

            batches.Add(batch);

            static bool hasCopyData(string sql) => CopyFromStdinRegex().IsMatch(CommentRegex().Replace(sql, " "));
        }

        /// <summary>The lines after a COPY ... FROM STDIN statement, up to a line holding only \. (or the end).</summary>
        private string ReadCopyData()
        {
            var lineEnd = s.IndexOf('\n', i);
            if (lineEnd < 0)
            {
                i = s.Length;
                return string.Empty;
            }

            i = lineEnd + 1;
            line++;
            var data = new StringBuilder();
            while (i < s.Length)
            {
                var end = s.IndexOf('\n', i);
                var text = end < 0 ? s[i..] : s[i..end];
                i = end < 0 ? s.Length : end + 1;
                line++;
                if (text.TrimEnd('\r') == "\\.")
                {
                    break;
                }

                data.Append(text).Append('\n');
            }

            return data.ToString();
        }

        /// <summary>:name, :'name', :"name" and :{?name}; undefined names are left as written, as psql does.</summary>
        private bool Interpolate()
        {
            var m = VariableRegex().Match(s, i);
            if (!m.Success || m.Index != i)
            {
                return false;
            }

            var name = m.Groups["name"].Value;
            string? value;
            if (m.Groups["test"].Success)
            {
                value = vars.ContainsKey(name) ? "TRUE" : "FALSE";
            }
            else if (!vars.TryGetValue(name, out var raw))
            {
                return false;
            }
            else
            {
                value = m.Groups["literal"].Success ? QuoteLiteral(raw) : m.Groups["identifier"].Success ? QuoteIdentifier(raw) : raw;
            }

            Code();
            current.Append(value);
            i += m.Length;
            return true;
        }

        private void Meta()
        {
            var start = i;
            var end = s.IndexOf('\n', i);
            var text = (end < 0 ? s[i..] : s[i..end]).TrimEnd('\r').Trim();
            var metaLine = line;
            metas.Add((start, end < 0 ? s.Length : end, metaLine));
            i = end < 0 ? s.Length : end + 1;
            if (end >= 0)
            {
                line++;
            }

            var m = CommandRegex().Match(text);
            var command = m.Groups["cmd"].Value;
            var args = Arguments(m.Groups["args"].Value, metaLine);

            switch (command)
            {
                case "if":
                {
                    var parent = Active;
                    var value = parent && !lint && Condition(args, metaLine);
                    conditions.Push((parent && value, parent && value, false));
                    return;
                }

                case "elif":
                case "else":
                {
                    if (conditions.Count == 0)
                    {
                        Problem(metaLine, $"\\{command} without \\if.");
                        return;
                    }

                    var top = conditions.Pop();
                    if (top.SawElse)
                    {
                        Problem(metaLine, $"\\{command} after \\else.");
                    }

                    var parent = conditions.All(c => c.Active);
                    var take = parent && !top.Taken && (command == "else" || (!lint && Condition(args, metaLine)));
                    conditions.Push((take, top.Taken || take, command == "else"));
                    return;
                }

                case "endif":
                    if (conditions.Count == 0)
                    {
                        Problem(metaLine, "\\endif without \\if.");
                    }
                    else
                    {
                        conditions.Pop();
                    }

                    return;
            }

            if (!Active)
            {
                return;
            }

            switch (command)
            {
                case "set":
                    if (args.Count > 0)
                    {
                        if (!NameRegex().IsMatch(args[0]))
                        {
                            Problem(metaLine, $"invalid variable name '{args[0]}'.");
                        }
                        else
                        {
                            vars[args[0]] = string.Concat(args.Skip(1));
                        }
                    }

                    return;
                case "unset":
                    foreach (var name in args)
                    {
                        vars.Remove(name);
                    }

                    return;
                case "getenv" when args.Count == 2:
                    if (Environment.GetEnvironmentVariable(args[1]) is { } env)
                    {
                        vars[args[0]] = env;
                    }
                    else
                    {
                        vars.Remove(args[0]);
                    }

                    return;
                case "echo" or "qecho" or "warn":
                    // psql prints at once, before a statement that is still being typed: so the notice goes first.
                    var words = args.SkipWhile(a => a == "-n").ToList();
                    batches.Add(new ScriptBatch($"DO $bsm_echo$ BEGIN RAISE NOTICE '%', {QuoteLiteral(string.Join(' ', words))}; END $bsm_echo$;", metaLine));
                    return;
                case "g" or "gx" when args.Count == 0:
                    Flush(executeResults: false);
                    return;
                case "gexec":
                    Flush(executeResults: true);
                    return;
                case "r" or "reset":
                    current.Clear();
                    hasCode = false;
                    return;
            }

            if (Ignored.Contains(command) || command.StartsWith('d') && DescribeRegex().IsMatch(command))
            {
                return;
            }

            if (command is "gset" or "g" or "gx" or "gdesc" or "crosstabview" or "watch")
            {
                // These send the query buffer: end the statement so the next one stands on its own.
                Flush(executeResults: false);
            }

            Problem(metaLine, command switch
            {
                "copy" => "\\copy can only read a file or STDIN into a table (\\copy ... from 'file'); writing files on the client is not supported.",
                "i" or "ir" or "include" or "include_relative" => $"\\{command} is expanded when the project loads; this script was split without it.",
                "c" or "connect" => "\\connect would switch databases; bsm deploys to the environment's database. Remove it.",
                "gset" => "\\gset sets variables from query results at run time; bsm expands variables before running. Use a DO block or a SQL variable instead.",
                "g" or "gx" => "\\g with an argument writes or pipes output; remove the argument.",
                _ => $"\\{command} is not supported in bsm scripts (it would {(Interactive.Contains(command) ? "ask for input or open an editor" : "act outside the database")}).",
            });
        }

        /// <summary>psql argument rules: whitespace-separated, 'quoted' with '' for a quote, :variables expanded.</summary>
        private List<string> Arguments(string text, int metaLine)
        {
            var args = new List<string>();
            var k = 0;
            while (k < text.Length)
            {
                while (k < text.Length && char.IsWhiteSpace(text[k]))
                {
                    k++;
                }

                if (k >= text.Length)
                {
                    break;
                }

                var sb = new StringBuilder();
                while (k < text.Length && !char.IsWhiteSpace(text[k]))
                {
                    if (text[k] == '\'')
                    {
                        k++;
                        while (k < text.Length)
                        {
                            if (text[k] == '\'' && k + 1 < text.Length && text[k + 1] == '\'')
                            {
                                sb.Append('\'');
                                k += 2;
                            }
                            else if (text[k] == '\'')
                            {
                                k++;
                                break;
                            }
                            else if (text[k] == '\\' && k + 1 < text.Length)
                            {
                                sb.Append(text[k + 1] switch { 'n' => '\n', 't' => '\t', _ => text[k + 1] });
                                k += 2;
                            }
                            else
                            {
                                sb.Append(text[k++]);
                            }
                        }
                    }
                    else if (text[k] == '`')
                    {
                        Problem(metaLine, "backquoted shell commands are not supported.");
                        k = text.Length;
                    }
                    else if (text[k] == ':' && VariableRegex().Match(text, k) is { Success: true } v && v.Index == k && vars.TryGetValue(v.Groups["name"].Value, out var raw) && !v.Groups["test"].Success)
                    {
                        sb.Append(v.Groups["literal"].Success ? QuoteLiteral(raw) : v.Groups["identifier"].Success ? QuoteIdentifier(raw) : raw);
                        k += v.Length;
                    }
                    else if (text[k] == ':' && VariableRegex().Match(text, k) is { Success: true } t && t.Index == k && t.Groups["test"].Success)
                    {
                        sb.Append(vars.ContainsKey(t.Groups["name"].Value) ? "TRUE" : "FALSE");
                        k += t.Length;
                    }
                    else
                    {
                        sb.Append(text[k++]);
                    }
                }

                args.Add(sb.ToString());
            }

            return args;
        }

        private bool Condition(List<string> args, int metaLine)
        {
            var value = string.Join(' ', args).Trim();
            if (TryBool(value, out var result))
            {
                return result;
            }

            Problem(metaLine, $"\\if needs a boolean (true/false, on/off, yes/no, 1/0); got '{value}'. Is the variable defined?");
            return false;
        }

        private static bool TryBool(string value, out bool result)
        {
            var v = value.ToLowerInvariant();
            result = false;
            if (v.Length == 0)
            {
                return false;
            }

            if ("true".StartsWith(v, StringComparison.Ordinal) || "yes".StartsWith(v, StringComparison.Ordinal) || v is "on" or "1")
            {
                result = true;
                return true;
            }

            return "false".StartsWith(v, StringComparison.Ordinal) || "no".StartsWith(v, StringComparison.Ordinal) || v is "off" or "of" or "0";
        }

        private void Problem(int at, string message) => problems.Add((at, message));
    }

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "timing", "pset", "x", "t", "a", "f", "H", "T", "C", "encoding", "conninfo", "l", "list", "z", "sf", "sv", "errverbose",
        "p", "print", "?", "h", "help", "s", "q", "quit",
    };

    private static readonly HashSet<string> Interactive = new(StringComparer.Ordinal) { "prompt", "password", "e", "edit", "ef", "ev", "watch" };

    public static string QuoteLiteral(string value) =>
        value.Contains('\\', StringComparison.Ordinal)
            ? "E'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'"
            : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>"$$" or "$tag$" starting at <paramref name="i"/>, or null ($1 parameters are not tags).</summary>
    private static string? DollarTag(string s, int i)
    {
        var j = i + 1;
        if (j < s.Length && s[j] == '$')
        {
            return "$$";
        }

        if (j >= s.Length || !(char.IsLetter(s[j]) || s[j] == '_'))
        {
            return null;
        }

        while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_'))
        {
            j++;
        }

        return j < s.Length && s[j] == '$' ? s[i..(j + 1)] : null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    [GeneratedRegex(@":(?:'(?<name>[A-Za-z_][A-Za-z0-9_]*)'(?<literal>)|""(?<name>[A-Za-z_][A-Za-z0-9_]*)""(?<identifier>)|\{\?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}(?<test>)|(?<name>[A-Za-z_][A-Za-z0-9_]*))")]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"^\\(?<cmd>[A-Za-z_]+|[!?;])(?<args>.*)$", RegexOptions.Singleline)]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^d[A-Za-z+]*$")]
    private static partial Regex DescribeRegex();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"\A\s*COPY\b[\s\S]*\bFROM\s+STDIN\b", RegexOptions.IgnoreCase)]
    private static partial Regex CopyFromStdinRegex();
}
