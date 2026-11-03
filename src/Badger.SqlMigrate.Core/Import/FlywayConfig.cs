using System.Globalization;
using System.Text;

namespace Badger.SqlMigrate.Core.Import;

/// <summary>
/// Settings from flyway.conf (key=value) or flyway.toml (Flyway 10+), flattened to dotted keys:
/// "flyway.locations", "flyway.placeholders.x", "environments.dev.url". Lists are stored comma-joined.
/// Only what the importer needs is interpreted; unknown keys are ignored.
/// </summary>
public sealed class FlywayConfig
{
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    public static FlywayConfig Empty { get; } = new();

    public string? this[string key] => values.TryGetValue(key, out var v) ? v : null;

    public IEnumerable<KeyValuePair<string, string>> WithPrefix(string prefix) =>
        values.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(kv => KeyValuePair.Create(kv.Key[prefix.Length..], kv.Value));

    public string Get(string key, string fallback) => this[key] is { Length: > 0 } v ? v : fallback;

    public bool GetBool(string key, bool fallback) => this[key] is { } v ? string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase) : fallback;

    public IReadOnlyList<string> GetList(string key) =>
        this[key] is { } v ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];

    /// <summary>Environment names defined in flyway.toml ([environments.name]).</summary>
    public IReadOnlyList<string> EnvironmentNames =>
        values.Keys.Where(k => k.StartsWith("environments.", StringComparison.OrdinalIgnoreCase))
            .Select(k => k.Split('.')[1]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

    public static FlywayConfig Load(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ? ParseToml(text, path) : ParseConf(text);
    }

    public static FlywayConfig ParseConf(string text)
    {
        var config = new FlywayConfig();
        string? pending = null;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = pending is null ? raw.Trim() : pending + raw.Trim();
            pending = null;
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!'))
            {
                continue;
            }

            if (line.EndsWith('\\'))
            {
                pending = line[..^1];
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                config.values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return config;
    }

    /// <summary>The subset of TOML Flyway files use: tables, dotted keys, strings, numbers, booleans and arrays.</summary>
    public static FlywayConfig ParseToml(string text, string source = "flyway.toml")
    {
        var config = new FlywayConfig();
        var table = string.Empty;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = StripComment(lines[i]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                table = string.Join('.', SplitKey(line.Trim('[', ']').Trim()));
                continue;
            }

            var eq = IndexOutsideQuotes(line, '=');
            if (eq <= 0)
            {
                throw new MigrationException($"{source} line {i + 1}: expected 'key = value'.");
            }

            var key = string.Join('.', SplitKey(line[..eq].Trim()));
            var value = line[(eq + 1)..].Trim();
            while (value.StartsWith('[') && !BalancedArray(value) && i + 1 < lines.Length)
            {
                value += " " + StripComment(lines[++i]).Trim();
            }

            config.values[table.Length == 0 ? key : $"{table}.{key}"] = ParseValue(value);
        }

        return config;
    }

    private static string ParseValue(string value)
    {
        if (value.StartsWith('['))
        {
            var items = new List<string>();
            var inner = value.Trim()[1..^1];
            var current = new StringBuilder();
            char? quote = null;
            foreach (var ch in inner)
            {
                if (quote is null && ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (quote == ch)
                {
                    quote = null;
                }
                else if (quote is null && ch == ',')
                {
                    items.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(ch);
            }

            items.Add(current.ToString().Trim());
            return string.Join(',', items.Where(s => s.Length > 0).Select(ParseValue));
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return Unescape(value[1..^1]);
        }

        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' ? value[1..^1] : value;
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length)
            {
                sb.Append(s[i]);
                continue;
            }

            var c = s[++i];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'u' when i + 4 < s.Length:
                    sb.Append((char)int.Parse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> SplitKey(string key) =>
        key.Split('.').Select(p => p.Trim().Trim('"', '\''));

    private static string StripComment(string line)
    {
        var hash = IndexOutsideQuotes(line, '#');
        return hash < 0 ? line : line[..hash];
    }

    private static int IndexOutsideQuotes(string line, char target)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote is null && ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (quote == ch && (i == 0 || line[i - 1] != '\\'))
            {
                quote = null;
            }
            else if (quote is null && ch == target)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool BalancedArray(string value) => value.Count(c => c == '[') == value.Count(c => c == ']');
}
