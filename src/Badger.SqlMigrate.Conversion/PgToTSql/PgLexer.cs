using System.Text;

namespace Badger.SqlMigrate.Conversion.PgToTSql;

internal enum TokenKind
{
    Word,
    QuotedIdent,
    String,
    Number,
    Param,
    Operator,
    Punct,
    End,
}

/// <param name="Value">Words: lowercase. Quoted identifiers and strings: the unescaped content. Others: the text.</param>
internal sealed record Token(TokenKind Kind, string Text, string Value, int Start, int End)
{
    public bool Is(string word) => Kind == TokenKind.Word && Value == word;

    public bool IsPunct(string p) => (Kind is TokenKind.Punct or TokenKind.Operator) && Text == p;

    public override string ToString() => Text;
}

/// <summary>Splits PostgreSQL / PL/pgSQL text into tokens (comments and whitespace dropped).</summary>
internal static class PgLexer
{
    private static readonly string[] Operators =
    [
        "==", "->>", "#>>", "!~~*", "~~*", "!~~", "!~*", "||/", "::", ":=", "..", "=>", "<>", "!=", ">=", "<=", "||", "->", "#>", "@>", "<@", "~~", "~*", "!~", "&&",
        "<<", ">>", "?|", "?&", "|/", "@@", "-|-",
    ];

    /// <param name="sqlite">Also read SQLite's [name] and `name` identifiers.</param>
    public static List<Token> Tokenize(string text, bool sqlite = false)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && At(text, i + 1) == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && At(text, i + 1) == '*')
            {
                var depth = 0;
                do
                {
                    if (text[i] == '/' && At(text, i + 1) == '*')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (text[i] == '*' && At(text, i + 1) == '/')
                    {
                        depth--;
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                while (depth > 0 && i < text.Length);
                continue;
            }

            var start = i;
            if ((c is 'E' or 'e') && At(text, i + 1) == '\'')
            {
                i++;
                var value = ReadQuoted(text, ref i, '\'', escapes: true);
                tokens.Add(new Token(TokenKind.String, text[start..i], value, start, i));
                continue;
            }

            if (c == '\'')
            {
                var value = ReadQuoted(text, ref i, '\'', escapes: false);
                tokens.Add(new Token(TokenKind.String, text[start..i], value, start, i));
                continue;
            }

            if (sqlite && c == '`')
            {
                var value = ReadQuoted(text, ref i, '`', escapes: false);
                tokens.Add(new Token(TokenKind.QuotedIdent, text[start..i], value, start, i));
                continue;
            }

            if (sqlite && c == '[')
            {
                var close = text.IndexOf(']', i + 1);
                var end = close < 0 ? text.Length : close + 1;
                tokens.Add(new Token(TokenKind.QuotedIdent, text[start..end], text[(i + 1)..(close < 0 ? text.Length : close)], start, end));
                i = end;
                continue;
            }

            if (c == '"')
            {
                var value = ReadQuoted(text, ref i, '"', escapes: false);
                tokens.Add(new Token(TokenKind.QuotedIdent, text[start..i], value, start, i));
                continue;
            }

            if (c == '$' && DollarTag(text, i) is { } tag)
            {
                var close = text.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                var end = close < 0 ? text.Length : close + tag.Length;
                var value = text[(i + tag.Length)..(close < 0 ? text.Length : close)];
                tokens.Add(new Token(TokenKind.String, text[start..end], value, start, end));
                i = end;
                continue;
            }

            if (c == '$' && char.IsDigit(At(text, i + 1)))
            {
                i++;
                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Param, text[start..i], text[start..i], start, i));
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && char.IsDigit(At(text, i + 1))))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || (text[i] == '.' && At(text, i + 1) != '.')))
                {
                    i++;
                }

                if (i < text.Length && (text[i] is 'e' or 'E') && (char.IsDigit(At(text, i + 1)) || (At(text, i + 1) is '+' or '-' && char.IsDigit(At(text, i + 2)))))
                {
                    i += 2;
                    while (i < text.Length && char.IsDigit(text[i]))
                    {
                        i++;
                    }
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i], text[start..i], start, i));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c > 127)
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$' || text[i] > 127))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Word, text[start..i], text[start..i].ToLowerInvariant(), start, i));
                continue;
            }

            var op = Operators.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
            if (op is not null)
            {
                i += op.Length;
                tokens.Add(new Token(TokenKind.Operator, op, op, start, i));
                continue;
            }

            i++;
            var kind = c is '(' or ')' or '[' or ']' or ',' or ';' or '.' or ':' ? TokenKind.Punct : TokenKind.Operator;
            tokens.Add(new Token(kind, c.ToString(), c.ToString(), start, i));
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, string.Empty, text.Length, text.Length));
        return tokens;
    }

    private static char At(string text, int i) => i < text.Length ? text[i] : '\0';

    private static string? DollarTag(string text, int i)
    {
        var j = i + 1;
        while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
        {
            j++;
        }

        return j < text.Length && text[j] == '$' && (j == i + 1 || !char.IsDigit(text[i + 1])) ? text[i..(j + 1)] : null;
    }

    private static string ReadQuoted(string text, ref int i, char quote, bool escapes)
    {
        var sb = new StringBuilder();
        i++;
        while (i < text.Length)
        {
            var c = text[i];
            if (escapes && c == '\\' && i + 1 < text.Length)
            {
                var n = text[i + 1];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => n });
                i += 2;
                continue;
            }

            if (c == quote)
            {
                if (At(text, i + 1) == quote)
                {
                    sb.Append(quote);
                    i += 2;
                    continue;
                }

                i++;
                return sb.ToString();
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
