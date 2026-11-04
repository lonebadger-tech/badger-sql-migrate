using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Project;

namespace Badger.SqlMigrate.Core.Import;

/// <summary>
/// Which repeatables a database already has exactly as written, so 'bsm baseline' records those as applied and the
/// first deploy re-applies only the ones that differ. A repeatable matches when its rendered script, without
/// bsm:requires headers, is the definition SQL Server stores for a module (view, procedure, function, trigger),
/// optionally followed by more batches (a GRANT after the procedure). Whitespace, line endings and CREATE
/// versus CREATE OR ALTER do not matter; case does, apart from the CREATE keyword.
/// </summary>
public static partial class RepeatableDefinitions
{
    /// <summary>The names of the repeatables (and environment scripts) whose rendered text matches one of <paramref name="definitions"/>.</summary>
    public static IReadOnlyList<string> Matching(MigrationProject project, IReadOnlyList<string> definitions, Func<RepeatableScript, string> render)
    {
        var live = definitions.Select(Normalize).Where(d => d.Length > 0).ToHashSet(StringComparer.Ordinal);
        return project.Repeatables.Where(r => Matches(Normalize(RequiresLineRegex().Replace(render(r), string.Empty)), live))
            .Select(r => r.Name)
            .ToList();
    }

    private static bool Matches(string script, HashSet<string> live)
    {
        if (live.Contains(script))
        {
            return true;
        }

        // The module is the first batch; what follows GO (grants, properties) is not part of its definition.
        foreach (Match go in GoRegex().Matches(script))
        {
            if (live.Contains(script[..go.Index].TrimEnd().TrimEnd(';').TrimEnd()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whitespace collapsed and the leading CREATE [OR ALTER] keyword made uniform. Everything else keeps its case:
    /// an edit that only changes a string literal's case ('active' to 'Active') is still a difference.
    /// </summary>
    private static string Normalize(string sql)
    {
        var text = WhitespaceRegex().Replace(sql.Replace("\r\n", "\n", StringComparison.Ordinal), " ").Trim().TrimEnd(';').Trim();
        return CreateRegex().Replace(text, "CREATE", 1);
    }

    [GeneratedRegex(@"^\s*--\s*bsm:requires.*$\n?(?:\s*GO\s*\n)?", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RequiresLineRegex();

    /// <summary>The first CREATE or CREATE OR ALTER, after any leading comments.</summary>
    [GeneratedRegex(@"\bCREATE(?:\s+OR\s+ALTER)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreateRegex();

    [GeneratedRegex(@" GO(?= |$)")]
    private static partial Regex GoRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
