using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.Sqlite;

/// <summary>
/// Splits a script into statements the way the sqlite3 shell does (the state machine of sqlite3_complete): a
/// semicolon ends a statement unless it is inside a string, a comment or the BEGIN ... END body of CREATE TRIGGER.
/// sqlite3 dot-commands (.read, .mode ...) are not SQL; they are reported, not run.
/// </summary>
public sealed class SqliteStatementSplitter : IScriptParser
{
    // Token classes and transitions from SQLite's complete.c.
    private const int Semi = 0, Ws = 1, Other = 2, Explain = 3, Create = 4, Temp = 5, Trigger = 6, End = 7;

    private static readonly int[,] Transitions =
    {
        /* 0 INVALID */ { 1, 0, 2, 3, 4, 2, 2, 2 },
        /* 1 START   */ { 1, 1, 2, 3, 4, 2, 2, 2 },
        /* 2 NORMAL  */ { 1, 2, 2, 2, 2, 2, 2, 2 },
        /* 3 EXPLAIN */ { 1, 3, 3, 2, 4, 2, 2, 2 },
        /* 4 CREATE  */ { 1, 4, 2, 2, 2, 4, 5, 2 },
        /* 5 TRIGGER */ { 6, 5, 5, 5, 5, 5, 5, 5 },
        /* 6 SEMI    */ { 6, 6, 5, 5, 5, 5, 5, 7 },
        /* 7 END     */ { 1, 7, 5, 5, 5, 5, 5, 5 },
    };

    public IReadOnlyList<ScriptBatch> Split(string script) => Analyze(script).Batches;

    internal sealed record ScanResult(IReadOnlyList<ScriptBatch> Batches, IReadOnlyList<(int Line, string Message)> Problems);

    internal ScanResult Analyze(string script)
    {
        var batches = new List<ScriptBatch>();
        var problems = new List<(int, string)>();
        var lines = LineStarts(script);
        var state = 0;
        var first = -1;
        var i = 0;
        while (i < script.Length)
        {
            // A dot-command is a whole line, and only where a statement could start.
            if (first < 0 && script[i] == '.' && AtLineStart(script, i))
            {
                var eol = script.IndexOf('\n', i);
                eol = eol < 0 ? script.Length : eol;
                var command = script[i..eol].Trim();
                problems.Add((Line(lines, i), $"'{command.Split(' ', 2)[0]}' is a sqlite3 shell command, not SQL; trek cannot run it. Remove it (put the SQL it would run in the script instead)."));
                i = eol;
                continue;
            }

            var t = SqliteLexer.Next(script, i);
            var kind = Classify(script, t);
            state = Transitions[state, kind];
            if (kind != Ws && first < 0)
            {
                first = t.Start;
            }

            if (kind == Semi && state == 1)
            {
                if (first >= 0 && first < t.Start)
                {
                    batches.Add(new ScriptBatch(script[first..t.End], Line(lines, first)));
                }

                first = -1;
            }

            i = t.End;
        }

        if (first >= 0)
        {
            var text = script[first..].TrimEnd();
            if (text.Length > 0 && text != ";")
            {
                batches.Add(new ScriptBatch(text, Line(lines, first)));
            }
        }

        return new ScanResult(batches, problems);
    }

    private static int Classify(string s, Token t)
    {
        if (t.IsTrivia)
        {
            return Ws;
        }

        if (t.Kind == TokenKind.Punctuation && s[t.Start] == ';')
        {
            return Semi;
        }

        if (t.Kind != TokenKind.Word)
        {
            return Other;
        }

        return SqliteLexer.IsWord(s, t, "EXPLAIN") ? Explain
            : SqliteLexer.IsWord(s, t, "CREATE") ? Create
            : SqliteLexer.IsWord(s, t, "TEMP") || SqliteLexer.IsWord(s, t, "TEMPORARY") ? Temp
            : SqliteLexer.IsWord(s, t, "TRIGGER") ? Trigger
            : SqliteLexer.IsWord(s, t, "END") ? End
            : Other;
    }

    private static bool AtLineStart(string s, int i)
    {
        for (var j = i - 1; j >= 0 && s[j] != '\n'; j--)
        {
            if (!char.IsWhiteSpace(s[j]))
            {
                return false;
            }
        }

        return true;
    }

    private static List<int> LineStarts(string s)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int Line(List<int> starts, int position)
    {
        var index = starts.BinarySearch(position);
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}
