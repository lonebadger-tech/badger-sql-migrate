using System.Text;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>
/// Splits a PostgreSQL script into statements at semicolons, the way psql does: semicolons inside
/// 'strings' (including E'escapes'), "identifiers", -- and nested /* */ comments, and $tag$ dollar-quoted
/// bodies (functions, DO blocks) do not end a statement. Statements made only of comments are dropped.
/// Each statement runs on its own, so statements that refuse a transaction block still work with !notx.
/// </summary>
public sealed class PostgreSqlStatementSplitter : IScriptParser
{
    public IReadOnlyList<ScriptBatch> Split(string script)
    {
        var batches = new List<ScriptBatch>();
        var current = new StringBuilder();
        var line = 1;
        var startLine = 0;
        var hasCode = false;
        var i = 0;

        void Flush()
        {
            if (hasCode)
            {
                batches.Add(new ScriptBatch(current.ToString().Trim(), startLine));
            }

            current.Clear();
            hasCode = false;
            startLine = 0;
        }

        void Code()
        {
            if (!hasCode)
            {
                hasCode = true;
                startLine = line;
            }
        }

        void Take(int count)
        {
            for (var k = 0; k < count && i < script.Length; k++, i++)
            {
                if (script[i] == '\n')
                {
                    line++;
                }

                current.Append(script[i]);
            }
        }

        while (i < script.Length)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                var end = script.IndexOf('\n', i);
                Take((end < 0 ? script.Length : end) - i);
                continue;
            }

            if (c == '/' && next == '*')
            {
                var depth = 0;
                do
                {
                    if (script[i] == '/' && i + 1 < script.Length && script[i + 1] == '*')
                    {
                        depth++;
                        Take(2);
                    }
                    else if (script[i] == '*' && i + 1 < script.Length && script[i + 1] == '/')
                    {
                        depth--;
                        Take(2);
                    }
                    else
                    {
                        Take(1);
                    }
                }
                while (depth > 0 && i < script.Length);
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
                Flush();
                continue;
            }

            Code();
            if (c == '\'')
            {
                var escapes = i > 0 && (script[i - 1] is 'E' or 'e') && (i < 2 || !IsIdentifierChar(script[i - 2]));
                Take(1);
                while (i < script.Length)
                {
                    if (escapes && script[i] == '\\')
                    {
                        Take(2);
                    }
                    else if (script[i] == '\'' && i + 1 < script.Length && script[i + 1] == '\'')
                    {
                        Take(2);
                    }
                    else if (script[i] == '\'')
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
                while (i < script.Length)
                {
                    if (script[i] == '"' && i + 1 < script.Length && script[i + 1] == '"')
                    {
                        Take(2);
                    }
                    else if (script[i] == '"')
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

            if (c == '$' && (i == 0 || !IsIdentifierChar(script[i - 1])) && DollarTag(script, i) is { } tag)
            {
                var close = script.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                Take((close < 0 ? script.Length : close + tag.Length) - i);
                continue;
            }

            Take(1);
        }

        Flush();
        return batches;
    }

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
}
