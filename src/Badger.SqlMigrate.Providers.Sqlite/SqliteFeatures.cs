using System.Text;
using System.Text.RegularExpressions;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>
/// SQL that older SQLite engines reject, with the version that added it. Checked on one statement with comments
/// removed and string literals and quoted names blanked, so text inside them never matches.
/// </summary>
internal static partial class SqliteFeatures
{
    private static readonly (Version Version, string Feature, Regex Pattern)[] Checks =
    [
        (new(3, 30), "NULLS FIRST / NULLS LAST", Rx(@"\bNULLS\s+(?:FIRST|LAST)\b")),
        (new(3, 30), "An aggregate FILTER (WHERE ...) clause", Rx(@"\bFILTER\s*\(\s*WHERE\b")),
        (new(3, 31), "A generated column", Rx(@"\bGENERATED\s+ALWAYS\b|\A(?:CREATE\s+(?:TEMP\s+|TEMPORARY\s+)?TABLE|ALTER\s+TABLE)\b.*?\bAS\s*\(")),
        (new(3, 32), "iif()", Rx(@"\biif\s*\(")),
        (new(3, 35), "ALTER TABLE ... DROP COLUMN", Rx(@"\AALTER\s+TABLE\b.*\bDROP\b")),
        (new(3, 35), "RETURNING", Rx(@"\bRETURNING\b")),
        (new(3, 35), "MATERIALIZED / NOT MATERIALIZED in a WITH clause", Rx(@"\bAS\s+(?:NOT\s+)?MATERIALIZED\s*\(")),
        (new(3, 37), "A STRICT table", Rx(@"\ACREATE\s+(?:TEMP\s+|TEMPORARY\s+)?TABLE\b.*\)\s*(?:WITHOUT\s+ROWID\s*,\s*)?STRICT\s*(?:,|;|\z)")),
        (new(3, 38), "The -> and ->> operators", Rx(@"->")),
        (new(3, 38), "unixepoch()", Rx(@"\bunixepoch\s*\(")),
        (new(3, 38), "format()", Rx(@"\bformat\s*\(")),
        (new(3, 39), "RIGHT and FULL OUTER JOIN", Rx(@"\b(?:RIGHT|FULL)\s+(?:OUTER\s+)?JOIN\b")),
        (new(3, 39), "IS [NOT] DISTINCT FROM", Rx(@"\bIS\s+(?:NOT\s+)?DISTINCT\s+FROM\b")),
        (new(3, 41), "unhex()", Rx(@"\bunhex\s*\(")),
        (new(3, 43), "timediff() and octet_length()", Rx(@"\b(?:timediff|octet_length)\s*\(")),
        (new(3, 44), "concat(), concat_ws() and string_agg()", Rx(@"\b(?:concat|concat_ws|string_agg)\s*\(")),
        (new(3, 45), "JSONB functions", Rx(@"\bjsonb\w*\s*\(")),
    ];

    /// <summary>Every feature the statement uses that some SQLite 3.26 or later lacks, oldest first.</summary>
    public static IEnumerable<(Version Version, string Feature)> Find(string statement)
    {
        var text = Mask(statement);
        foreach (var (version, feature, pattern) in Checks)
        {
            if (pattern.IsMatch(text))
            {
                yield return (version, feature);
            }
        }

        if (UsesUpdateFrom(text))
        {
            yield return (new(3, 33), "UPDATE ... FROM");
        }
    }

    /// <summary>The statement with comments removed and strings and quoted names replaced by placeholders.</summary>
    private static string Mask(string statement)
    {
        var sb = new StringBuilder();
        foreach (var t in SqliteLexer.Tokenize(statement))
        {
            sb.Append(t.Kind switch
            {
                TokenKind.Comment or TokenKind.Space => " ",
                TokenKind.String => "''",
                TokenKind.QuotedIdentifier => "\"q\"",
                _ => statement.AsSpan(t.Start, t.Length),
            });
        }

        return sb.ToString().Trim();
    }

    /// <summary>UPDATE ... SET ... FROM at the statement's own level (a FROM inside a subquery is older SQL).</summary>
    private static bool UsesUpdateFrom(string text)
    {
        if (!UpdateRegex().IsMatch(text))
        {
            return false;
        }

        var depth = 0;
        var afterSet = false;
        foreach (Match m in WordOrParenRegex().Matches(text))
        {
            switch (m.Value)
            {
                case "(":
                    depth++;
                    break;
                case ")":
                    depth--;
                    break;
                default:
                    if (depth == 0 && m.Value.Equals("SET", StringComparison.OrdinalIgnoreCase))
                    {
                        afterSet = true;
                    }
                    else if (depth == 0 && afterSet && m.Value.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\A(?:WITH\b.*?\)\s*)?UPDATE\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UpdateRegex();

    [GeneratedRegex(@"[A-Za-z_]\w*|[()]")]
    private static partial Regex WordOrParenRegex();
}
