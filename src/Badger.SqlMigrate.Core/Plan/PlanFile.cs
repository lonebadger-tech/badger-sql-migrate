using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Badger.SqlMigrate.Core.Plan;

/// <summary>
/// The parsed bsm.plan file. The format is deliberately one change per line so that git's
/// union merge driver can combine two developers' appended lines without a conflict.
/// <code>
/// # comment
/// add-customer-table                        @2026-11-03T14:10Z brock  # note
/// add-customer-email [add-customer-table]   @2026-11-04T09:02Z alice
/// add-order-index !notx                     @2026-11-04T09:15Z bob
/// @release-1.4
/// </code>
/// </summary>
public sealed partial class PlanFile
{
    public const string FileName = "bsm.plan";

    public PlanFile(IReadOnlyList<PlanChange> changes, IReadOnlyList<PlanParseError> errors)
    {
        Changes = changes;
        Errors = errors;
    }

    public IReadOnlyList<PlanChange> Changes { get; }

    /// <summary>Syntax errors found while parsing; <see cref="PlanValidator"/> adds semantic checks.</summary>
    public IReadOnlyList<PlanParseError> Errors { get; }

    public int IndexOf(string name)
    {
        for (var i = 0; i < Changes.Count; i++)
        {
            if (string.Equals(Changes[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Resolves a change name or "@tag" to an index in <see cref="Changes"/>, or -1.</summary>
    public int ResolveTarget(string target)
    {
        if (target.StartsWith('@'))
        {
            var tag = target[1..];
            for (var i = 0; i < Changes.Count; i++)
            {
                if (Changes[i].Tags.Contains(tag, StringComparer.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        return IndexOf(target);
    }

    public static PlanFile Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static PlanFile Parse(string text)
    {
        var changes = new List<PlanChange>();
        var errors = new List<PlanParseError>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var raw = lines[i];
            var (body, note) = SplitNote(raw);
            body = body.Trim();
            if (body.Length == 0 || body.StartsWith('%'))
            {
                continue;
            }

            if (body.StartsWith('@'))
            {
                var tagName = body[1..].Trim();
                if (!NameRegex().IsMatch(tagName))
                {
                    errors.Add(new(lineNumber, $"Invalid tag name '{tagName}'."));
                    continue;
                }

                if (changes.Count == 0)
                {
                    errors.Add(new(lineNumber, $"Tag '@{tagName}' must follow a change."));
                    continue;
                }

                var last = changes[^1];
                changes[^1] = last with { Tags = [.. last.Tags, tagName] };
                continue;
            }

            if (TryParseChange(body, note, lineNumber, out var change, out var error))
            {
                changes.Add(change);
            }
            else
            {
                errors.Add(new(lineNumber, error));
            }
        }

        return new PlanFile(changes, errors);
    }

    /// <summary>Formats a single change line in canonical form.</summary>
    public static string FormatChange(PlanChange change)
    {
        var sb = new StringBuilder(change.Name);
        if (change.Requires.Count > 0)
        {
            sb.Append(" [").Append(string.Join(' ', change.Requires)).Append(']');
        }

        if (change.Flags.HasFlag(ChangeFlags.Irreversible))
        {
            sb.Append(" !irreversible");
        }

        if (change.Flags.HasFlag(ChangeFlags.NoTransaction))
        {
            sb.Append(" !notx");
        }

        if (change.PlannedAt is { } at)
        {
            sb.Append(" @").Append(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(change.Author))
            {
                sb.Append(' ').Append(change.Author);
            }
        }

        if (!string.IsNullOrWhiteSpace(change.Note))
        {
            sb.Append("  # ").Append(change.Note);
        }

        return sb.ToString();
    }

    public static bool IsValidName(string name) => NameRegex().IsMatch(name);

    private static bool TryParseChange(string body, string? note, int lineNumber, out PlanChange change, out string error)
    {
        change = null!;
        error = string.Empty;

        var requires = new List<string>();
        var open = body.IndexOf('[', StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = body.IndexOf(']', open);
            if (close < 0)
            {
                error = "Unclosed '[' in dependency list.";
                return false;
            }

            requires.AddRange(body[(open + 1)..close].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries));
            body = body[..open] + " " + body[(close + 1)..];
        }

        var tokens = body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var name = tokens[0];
        if (!NameRegex().IsMatch(name))
        {
            error = $"Invalid change name '{name}'. Use letters, digits, '-', '_' or '.', starting with a letter or digit.";
            return false;
        }

        var flags = ChangeFlags.None;
        DateTimeOffset? plannedAt = null;
        string? author = null;
        for (var t = 1; t < tokens.Length; t++)
        {
            var token = tokens[t];
            switch (token)
            {
                case "!irreversible":
                    flags |= ChangeFlags.Irreversible;
                    break;
                case "!notx":
                    flags |= ChangeFlags.NoTransaction;
                    break;
                default:
                    if (token.StartsWith('@'))
                    {
                        if (!DateTimeOffset.TryParse(token[1..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
                        {
                            error = $"Invalid timestamp '{token}'.";
                            return false;
                        }

                        plannedAt = at;
                        if (t + 1 < tokens.Length && !tokens[t + 1].StartsWith('!'))
                        {
                            author = tokens[++t];
                        }
                    }
                    else
                    {
                        error = $"Unexpected token '{token}'.";
                        return false;
                    }

                    break;
            }
        }

        foreach (var dep in requires)
        {
            if (!NameRegex().IsMatch(dep))
            {
                error = $"Invalid dependency name '{dep}'.";
                return false;
            }
        }

        change = new PlanChange(name, requires, flags, plannedAt, author, note, lineNumber);
        return true;
    }

    private static (string Body, string? Note) SplitNote(string line)
    {
        var hash = line.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return (line, null);
        }

        var note = line[(hash + 1)..].Trim();
        return (line[..hash], note.Length == 0 ? null : note);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$")]
    private static partial Regex NameRegex();
}

public sealed record PlanParseError(int LineNumber, string Message)
{
    public override string ToString() => $"line {LineNumber}: {Message}";
}
