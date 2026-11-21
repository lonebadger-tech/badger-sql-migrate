using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Oracle;

/// <summary>What one piece of a script is, as SQL*Plus reads it.</summary>
public enum OracleItemKind
{
    /// <summary>A SQL statement (sent without its closing ';').</summary>
    Sql,

    /// <summary>A PL/SQL block or a stored program (CREATE PROCEDURE ...), ended by a '/' line and sent with its END;.</summary>
    PlSql,

    /// <summary>A SQL*Plus command that changes only SQL*Plus's own display or settings (PROMPT, SET ...): not sent.</summary>
    Skipped,
}

/// <param name="Kind">What it is.</param>
/// <param name="Text">The text to send (or the skipped command).</param>
/// <param name="Line">1-based line where it starts.</param>
/// <param name="Terminated">False for a PL/SQL block the script ended without a '/' line.</param>
public sealed record OracleScriptItem(OracleItemKind Kind, string Text, int Line, bool Terminated = true);

/// <summary>
/// Splits scripts the way SQL*Plus and SQLcl do. A SQL statement ends at ';' (outside strings, q'[...]' literals, quoted
/// names and comments) or at a line holding only '/'. A PL/SQL block (DECLARE, BEGIN) or stored program (CREATE
/// PROCEDURE, FUNCTION, PACKAGE [BODY], TRIGGER, TYPE [BODY] ...) holds ';' inside it and ends only at a line holding
/// only '/'. SQL*Plus commands that affect only SQL*Plus (PROMPT, REM, SET, SPOOL, WHENEVER, SHOW, COLUMN ...) are not
/// sent; EXEC[UTE] proc becomes BEGIN proc; END;. Everything else, including @file and CONNECT, is sent (the linter
/// reports those, and the server would reject them).
/// </summary>
public sealed partial class OracleStatementSplitter : IScriptParser
{
    private static readonly HashSet<string> SkippedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROMPT", "PRO", "REM", "REMARK", "SET", "SPOOL", "SPO", "WHENEVER", "SHOW", "SHO", "COLUMN", "COL", "TTITLE", "BTITLE",
        "BREAK", "COMPUTE", "CLEAR", "PAUSE", "TIMING", "REPHEADER", "REPFOOTER", "ARCHIVE",
    };

    public IReadOnlyList<ScriptBatch> Split(string script) =>
        Analyze(script).Where(i => i.Kind != OracleItemKind.Skipped).Select(i => new ScriptBatch(i.Text, i.Line)).ToList();

    public IReadOnlyList<OracleScriptItem> Analyze(string script)
    {
        var items = new List<OracleScriptItem>();
        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var buffer = new StringBuilder();
        var start = 0;
        bool? plsql = null;
        var scan = new ScanState();

        void Emit(bool terminated = true)
        {
            var text = buffer.ToString().Trim();
            if (plsql != true)
            {
                text = text.TrimEnd(';').TrimEnd();
            }

            if (Meaningful(text))
            {
                items.Add(new OracleScriptItem(plsql == true ? OracleItemKind.PlSql : OracleItemKind.Sql, text, start, terminated));
            }

            buffer.Clear();
            plsql = null;
            scan = new ScanState();
        }

        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            var empty = !Meaningful(buffer.ToString());

            // A line holding only '/' runs the buffer: it ends a PL/SQL block or a SQL statement without ';'.
            if (!scan.InsideText && SlashRegex().IsMatch(line))
            {
                if (!empty)
                {
                    Emit();
                }

                continue;
            }

            if (empty && !scan.InsideText)
            {
                var trimmed = line.Trim();
                var word = FirstWordRegex().Match(trimmed).Value;
                if (SkippedCommands.Contains(word))
                {
                    items.Add(new OracleScriptItem(OracleItemKind.Skipped, trimmed, n + 1));
                    buffer.Clear();
                    continue;
                }

                // Other SQL*Plus commands also end at the end of their line. They are sent as they are (the server rejects
                // them, and lint reports them first), but never swallow the statements after them.
                if (LineCommandRegex().IsMatch(trimmed))
                {
                    items.Add(new OracleScriptItem(OracleItemKind.Sql, trimmed.TrimEnd(';').TrimEnd(), n + 1));
                    buffer.Clear();
                    continue;
                }

                if (ExecRegex().Match(trimmed) is { Success: true } exec)
                {
                    items.Add(new OracleScriptItem(OracleItemKind.PlSql, $"BEGIN {exec.Groups[1].Value.TrimEnd().TrimEnd(';').TrimEnd()}; END;", n + 1));
                    buffer.Clear();
                    continue;
                }
            }

            if (empty)
            {
                buffer.Clear();
                start = n + 1;
            }

            // Scan character by character for ';' at the end of a SQL statement.
            var segmentStart = 0;
            for (var i = 0; i < line.Length; i++)
            {
                if (scan.Step(line, ref i))
                {
                    continue;
                }

                if (line[i] != ';')
                {
                    continue;
                }

                plsql ??= Classify(StripComments(buffer.ToString() + line[segmentStart..i]).TrimStart()) ?? false;
                if (plsql == false)
                {
                    buffer.Append(line, segmentStart, i - segmentStart + 1);
                    Emit();
                    segmentStart = i + 1;
                    start = n + 1;
                }
            }

            buffer.Append(line, segmentStart, line.Length - segmentStart).Append('\n');
            scan.EndOfLine();
            if (plsql is null && Meaningful(buffer.ToString()))
            {
                plsql = Classify(StripComments(buffer.ToString()).TrimStart());
            }
        }

        if (Meaningful(buffer.ToString()))
        {
            Emit(terminated: plsql != true);
        }

        return items;
    }

    /// <summary>True for a PL/SQL block or stored program, false for plain SQL, null while too little has been read.</summary>
    private static bool? Classify(string text)
    {
        if (PlSqlRegex().IsMatch(text))
        {
            return true;
        }

        // Enough words to know it is not one: CREATE OR REPLACE EDITIONABLE PACKAGE BODY is the longest prefix.
        return WordsRegex().Matches(text).Count >= 6 || NotPlSqlStartRegex().IsMatch(text) ? false : null;
    }

    private static bool Meaningful(string text) => StripComments(text).Trim().Length > 0;

    internal static string StripComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var scan = new ScanState();
        var lines = sql.Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            var line = lines[l];
            for (var i = 0; i < line.Length; i++)
            {
                var before = i;
                var inComment = scan.InComment;
                if (scan.Step(line, ref i))
                {
                    // Keep strings and quoted names; drop comments.
                    if (!inComment && !scan.InComment && !scan.LineCommentStarted)
                    {
                        sb.Append(line, before, i - before + 1);
                    }
                    else
                    {
                        sb.Append(' ');
                    }

                    scan.LineCommentStarted = false;
                    continue;
                }

                sb.Append(line[i]);
            }

            scan.EndOfLine();
            if (l < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*/\s*$")]
    private static partial Regex SlashRegex();

    [GeneratedRegex(@"^[A-Za-z]+")]
    private static partial Regex FirstWordRegex();

    [GeneratedRegex(@"^(?:@@?\S|(?:START|CONNECT|CONN|DEFINE|DEF|UNDEFINE|UNDEF|ACCEPT|ACC|VARIABLE|VAR|PRINT|EXIT|QUIT|HOST|DESCRIBE|DESC|SAVE|GET|EDIT|ED|PASSWORD|STORE|ATTRIBUTE)(?:\s|$)|!)", RegexOptions.IgnoreCase)]
    private static partial Regex LineCommandRegex();

    [GeneratedRegex(@"^EXEC(?:UTE)?\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ExecRegex();

    [GeneratedRegex(@"\A(?:DECLARE|BEGIN|<<|CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?(?:PROCEDURE|FUNCTION|PACKAGE|TRIGGER|TYPE|LIBRARY|JAVA|AND\s+(?:RESOLVE|COMPILE)\s+JAVA)\b|WITH\s+(?:FUNCTION|PROCEDURE)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex PlSqlRegex();

    [GeneratedRegex(@"\A(?!CREATE\b|WITH\b|DECLARE\b|BEGIN\b)[A-Za-z]+\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotPlSqlStartRegex();

    [GeneratedRegex(@"[A-Za-z_$#]+")]
    private static partial Regex WordsRegex();

    /// <summary>Where the scanner is: inside a block comment, a '...' or q'[...]' literal, or a "quoted name".</summary>
    private sealed class ScanState
    {
        private char? quote;
        private char? qClose;

        public bool InComment { get; private set; }

        public bool LineCommentStarted { get; set; }

        public bool InsideText => InComment || quote is not null || qClose is not null;

        /// <summary>Advances over a comment, literal or quoted name starting at or continuing through i; true when it consumed i.</summary>
        public bool Step(string line, ref int i)
        {
            if (InComment)
            {
                var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0)
                {
                    i = line.Length - 1;
                    return true;
                }

                i = end + 1;
                InComment = false;
                return true;
            }

            if (qClose is { } close)
            {
                var end = line.IndexOf(close + "'", i, StringComparison.Ordinal);
                if (end < 0)
                {
                    i = line.Length - 1;
                    return true;
                }

                i = end + 1;
                qClose = null;
                return true;
            }

            if (quote is { } q)
            {
                for (; i < line.Length; i++)
                {
                    if (line[i] != q)
                    {
                        continue;
                    }

                    if (q == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    quote = null;
                    return true;
                }

                i = line.Length - 1;
                return true;
            }

            var c = line[i];
            if (c == '-' && i + 1 < line.Length && line[i + 1] == '-')
            {
                LineCommentStarted = true;
                i = line.Length - 1;
                return true;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                InComment = true;
                i += 2;
                if (i >= line.Length)
                {
                    i = line.Length - 1;
                    return true;
                }

                return Step(line, ref i);
            }

            // q'[...]' (also with N or Q prefix): the closing is the matching bracket or the same character, then '.
            if ((c is 'q' or 'Q') && i + 2 < line.Length && line[i + 1] == '\'' && (i == 0 || !IsWordChar(line[i - 1]) || line[i - 1] is 'n' or 'N'))
            {
                var open = line[i + 2];
                qClose = open switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => open };
                i += 3;
                if (i >= line.Length)
                {
                    i = line.Length - 1;
                    return true;
                }

                return Step(line, ref i);
            }

            if (c is '\'' or '"')
            {
                quote = c;
                i++;
                if (i >= line.Length)
                {
                    i = line.Length - 1;
                    return true;
                }

                return Step(line, ref i);
            }

            return false;
        }

        public void EndOfLine() => LineCommentStarted = false;

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';
    }
}
