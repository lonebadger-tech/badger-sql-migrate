namespace Badger.SqlMigrate.Providers.Sqlite;

internal enum TokenKind
{
    Space,
    Comment,

    /// <summary>'text' (and x'blob').</summary>
    String,

    /// <summary>"name", [name] or `name`.</summary>
    QuotedIdentifier,
    Word,
    Number,
    Punctuation,
}

internal readonly record struct Token(TokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;

    public bool IsTrivia => Kind is TokenKind.Space or TokenKind.Comment;

    public bool IsWordLike => Kind is TokenKind.String or TokenKind.QuotedIdentifier or TokenKind.Word or TokenKind.Number;
}

/// <summary>SQLite's tokens, as far as splitting statements and comparing definitions needs them.</summary>
internal static class SqliteLexer
{
    public static Token Next(string s, int i)
    {
        var c = s[i];
        if (char.IsWhiteSpace(c))
        {
            var j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j]))
            {
                j++;
            }

            return new Token(TokenKind.Space, i, j - i);
        }

        if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
        {
            var j = s.IndexOf('\n', i);
            return new Token(TokenKind.Comment, i, (j < 0 ? s.Length : j) - i);
        }

        if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
        {
            var j = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return new Token(TokenKind.Comment, i, (j < 0 ? s.Length : j + 2) - i);
        }

        if (c == '\'')
        {
            return new Token(TokenKind.String, i, Quoted(s, i, '\'') - i);
        }

        if ((c is 'x' or 'X') && i + 1 < s.Length && s[i + 1] == '\'')
        {
            return new Token(TokenKind.String, i, Quoted(s, i + 1, '\'') - i);
        }

        if (c is '"' or '`')
        {
            return new Token(TokenKind.QuotedIdentifier, i, Quoted(s, i, c) - i);
        }

        if (c == '[')
        {
            var j = s.IndexOf(']', i + 1);
            return new Token(TokenKind.QuotedIdentifier, i, (j < 0 ? s.Length : j + 1) - i);
        }

        if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
        {
            var j = i + 1;
            while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] is '.' or '_' || (s[j] is '+' or '-' && s[j - 1] is 'e' or 'E' && !s[i..j].StartsWith("0x", StringComparison.OrdinalIgnoreCase))))
            {
                j++;
            }

            return new Token(TokenKind.Number, i, j - i);
        }

        if (IsWordStart(c))
        {
            var j = i + 1;
            while (j < s.Length && (IsWordStart(s[j]) || char.IsAsciiDigit(s[j]) || s[j] == '$'))
            {
                j++;
            }

            return new Token(TokenKind.Word, i, j - i);
        }

        return new Token(TokenKind.Punctuation, i, 1);
    }

    public static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < s.Length;)
        {
            var t = Next(s, i);
            tokens.Add(t);
            i = t.End;
        }

        return tokens;
    }

    public static bool IsWord(string s, Token t, string word) =>
        t.Kind == TokenKind.Word && t.Length == word.Length && string.Compare(s, t.Start, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>The identifier a word or quoted identifier names (quotes removed).</summary>
    public static string Identifier(string s, Token t) => t.Kind switch
    {
        TokenKind.QuotedIdentifier when s[t.Start] == '[' => s.Substring(t.Start + 1, Math.Max(0, t.Length - 2)),
        TokenKind.QuotedIdentifier => s.Substring(t.Start + 1, Math.Max(0, t.Length - 2)).Replace(new string(s[t.Start], 2), new string(s[t.Start], 1), StringComparison.Ordinal),
        _ => s.Substring(t.Start, t.Length),
    };

    /// <summary>
    /// A definition compared and fingerprinted without layout: comments dropped, a single space only between two
    /// word-like tokens, and simple quoted identifiers unquoted ("Email" and Email are the same column).
    /// </summary>
    public static string Normalize(string s) => Normalize(s, Tokenize(s));

    public static string Normalize(string s, IEnumerable<Token> tokens)
    {
        var sb = new System.Text.StringBuilder();
        Token? previous = null;
        foreach (var t in tokens)
        {
            if (t.IsTrivia)
            {
                continue;
            }

            if (previous is { IsWordLike: true } && t.IsWordLike)
            {
                sb.Append(' ');
            }

            if (t.Kind == TokenKind.QuotedIdentifier)
            {
                var name = Identifier(s, t);
                sb.Append(IsSimple(name) ? name : "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
            }
            else
            {
                sb.Append(s, t.Start, t.Length);
            }

            previous = t;
        }

        return sb.ToString();
    }

    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static bool IsSimple(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static bool IsWordStart(char c) => char.IsAsciiLetter(c) || c == '_' || c > 127;

    private static int Quoted(string s, int i, char quote)
    {
        var j = i + 1;
        while (j < s.Length)
        {
            if (s[j] == quote)
            {
                if (j + 1 < s.Length && s[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }

                return j + 1;
            }

            j++;
        }

        return s.Length;
    }
}
