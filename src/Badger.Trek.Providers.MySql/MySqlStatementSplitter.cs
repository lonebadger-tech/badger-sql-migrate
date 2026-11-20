using System.Text;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql;

/// <summary>
/// Splits a script into statements the way the mysql client does: a statement ends at the delimiter (';' unless a
/// 'DELIMITER //' line changes it, so procedure, function and trigger bodies stay whole), outside quotes ('...',
/// "...", `...`), comments ('-- ', '#', '/* */') and versioned comments ('/*!80000 ... */'). DELIMITER lines are the
/// client's, not SQL: they are not sent. Statements that are only comments are dropped (MySQL rejects an empty query).
/// </summary>
public sealed class MySqlStatementSplitter : IScriptParser
{
    public IReadOnlyList<ScriptBatch> Split(string script) => Analyze(script).Batches;

    /// <summary>The batches, and the client commands found (for the linter): DELIMITER changes are handled, not reported.</summary>
    public SplitResult Analyze(string script)
    {
        var text = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        var batches = new List<ScriptBatch>();
        var current = new StringBuilder();
        var hasCode = false;
        var delimiter = ";";
        var line = 1;
        var startLine = 1;
        var i = 0;

        void Flush()
        {
            var statement = current.ToString().Trim();
            if (hasCode && statement.Length > 0)
            {
                batches.Add(new ScriptBatch(statement, startLine));
            }

            current.Clear();
            hasCode = false;
        }

        while (i < text.Length)
        {
            // A DELIMITER line, only at the start of a line between statements.
            if (AtLineStart(text, i) && !hasCode && TryDelimiterLine(text, i, out var next, out var end))
            {
                delimiter = next;
                current.Clear();
                line += Count(text, i, end, '\n');
                i = end;
                startLine = line;
                continue;
            }

            if (!hasCode && current.ToString().Trim().Length == 0)
            {
                startLine = line;
            }

            var c = text[i];
            if (At(text, i, delimiter))
            {
                Flush();
                i += delimiter.Length;
                continue;
            }

            if (c is '\'' or '"' or '`')
            {
                var close = QuoteEnd(text, i, c);
                current.Append(text, i, close - i);
                line += Count(text, i, close, '\n');
                hasCode = true;
                i = close;
                continue;
            }

            // '-- ' starts a comment only when followed by whitespace (or the end): '--1' is minus minus one.
            if (c == '#' || (At(text, i, "--") && (i + 2 == text.Length || char.IsWhiteSpace(text[i + 2]))))
            {
                var eol = text.IndexOf('\n', i);
                eol = eol < 0 ? text.Length : eol;
                current.Append(text, i, eol - i);
                i = eol;
                continue;
            }

            if (At(text, i, "/*"))
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                close = close < 0 ? text.Length : close + 2;
                current.Append(text, i, close - i);
                line += Count(text, i, close, '\n');

                // A versioned comment (/*!80000 ... */) is code: mysql runs it.
                if (At(text, i, "/*!"))
                {
                    hasCode = true;
                }

                i = close;
                continue;
            }

            if (c == '\n')
            {
                line++;
            }
            else if (!char.IsWhiteSpace(c))
            {
                hasCode = true;
            }

            current.Append(c);
            i++;
        }

        Flush();
        return new SplitResult(batches, delimiter);
    }

    private static bool AtLineStart(string text, int i)
    {
        var j = i - 1;
        while (j >= 0 && text[j] is ' ' or '\t')
        {
            j--;
        }

        return j < 0 || text[j] == '\n';
    }

    /// <summary>"DELIMITER //" (any case, leading spaces): the new delimiter and where the line ends.</summary>
    private static bool TryDelimiterLine(string text, int i, out string delimiter, out int end)
    {
        delimiter = string.Empty;
        end = i;
        var j = i;
        while (j < text.Length && text[j] is ' ' or '\t')
        {
            j++;
        }

        if (string.Compare(text, j, "DELIMITER", 0, 9, StringComparison.OrdinalIgnoreCase) != 0 || j + 9 >= text.Length || !char.IsWhiteSpace(text[j + 9]) || text[j + 9] == '\n')
        {
            return false;
        }

        var eol = text.IndexOf('\n', j);
        eol = eol < 0 ? text.Length : eol;
        var value = text[(j + 9)..eol].Trim();
        if (value.Length == 0)
        {
            return false;
        }

        delimiter = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        end = eol;
        return true;
    }

    /// <summary>Index after the closing quote; backslash escapes in strings, doubled quotes everywhere.</summary>
    private static int QuoteEnd(string text, int start, char quote)
    {
        var i = start + 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && quote != '`')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static bool At(string text, int i, string token) => string.CompareOrdinal(text, i, token, 0, token.Length) == 0;

    private static int Count(string text, int from, int to, char c)
    {
        var n = 0;
        for (var i = from; i < to && i < text.Length; i++)
        {
            if (text[i] == c)
            {
                n++;
            }
        }

        return n;
    }
}

/// <param name="Batches">The statements to send.</param>
/// <param name="FinalDelimiter">The delimiter in effect at the end (a script should set it back to ';').</param>
public sealed record SplitResult(IReadOnlyList<ScriptBatch> Batches, string FinalDelimiter);
