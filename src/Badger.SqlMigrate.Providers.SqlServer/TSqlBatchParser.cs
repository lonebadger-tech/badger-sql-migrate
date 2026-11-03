using System.Globalization;
using System.Text;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// Splits T-SQL on "GO" separator lines the way sqlcmd/SSMS do, but ignores GO inside block comments
/// (which nest in T-SQL), string literals and bracketed identifiers. Supports "GO n" repeat counts and
/// a trailing "-- comment" on the separator line.
/// </summary>
public sealed class TSqlBatchParser : IScriptParser
{
    public IReadOnlyList<ScriptBatch> Split(string script)
    {
        var batches = new List<ScriptBatch>();
        var current = new StringBuilder();
        var batchStartLine = 1;
        var commentDepth = 0;
        var inString = false;
        var inBracket = false;

        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var startsInCode = commentDepth == 0 && !inString && !inBracket;
            if (startsInCode && TryParseSeparator(line, out var repeat))
            {
                Add(batches, current, batchStartLine, repeat);
                current.Clear();
                batchStartLine = i + 2;
                continue;
            }

            Scan(line, ref commentDepth, ref inString, ref inBracket);
            current.Append(line);
            if (i < lines.Length - 1)
            {
                current.Append('\n');
            }
        }

        Add(batches, current, batchStartLine, 1);
        return batches;
    }

    private static void Add(List<ScriptBatch> batches, StringBuilder text, int startLine, int repeat)
    {
        var sql = text.ToString();
        if (!string.IsNullOrWhiteSpace(sql))
        {
            batches.Add(new ScriptBatch(sql.Trim('\n'), startLine, repeat));
        }
    }

    private static bool TryParseSeparator(string line, out int repeat)
    {
        repeat = 1;
        var text = line.Trim();
        var dash = text.IndexOf("--", StringComparison.Ordinal);
        if (dash >= 0)
        {
            text = text[..dash].TrimEnd();
        }

        if (text.Length < 2 || !text.StartsWith("GO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = text[2..].Trim();
        if (rest.Length == 0)
        {
            return true;
        }

        return int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out repeat) && repeat > 0;
    }

    /// <summary>Tracks lexical state across a line so GO inside comments/strings is not treated as a separator.</summary>
    private static void Scan(string line, ref int commentDepth, ref bool inString, ref bool inBracket)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (commentDepth > 0)
            {
                if (c == '/' && next == '*')
                {
                    commentDepth++;
                    i++;
                }
                else if (c == '*' && next == '/')
                {
                    commentDepth--;
                    i++;
                }

                continue;
            }

            if (inString)
            {
                if (c == '\'')
                {
                    if (next == '\'')
                    {
                        i++;
                    }
                    else
                    {
                        inString = false;
                    }
                }

                continue;
            }

            if (inBracket)
            {
                if (c == ']')
                {
                    if (next == ']')
                    {
                        i++;
                    }
                    else
                    {
                        inBracket = false;
                    }
                }

                continue;
            }

            switch (c)
            {
                case '-' when next == '-':
                    return;
                case '/' when next == '*':
                    commentDepth = 1;
                    i++;
                    break;
                case '\'':
                    inString = true;
                    break;
                case '[':
                    inBracket = true;
                    break;
            }
        }
    }
}
