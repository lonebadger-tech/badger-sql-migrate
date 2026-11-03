using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Badger.SqlMigrate.Core.Preprocessing;

/// <summary>Where a variable's value comes from, for 'bsm vars' and error messages.</summary>
public enum VariableSource
{
    BuiltIn,
    Config,
    CommandLine,
}

public sealed record VariableInfo(string Name, string Value, VariableSource Source, string? Description = null, bool Volatile = false);

public enum UnknownVariablePolicy
{
    /// <summary>An unknown {name} is an error (default): typos never reach the database.</summary>
    Error,

    /// <summary>An unknown {name} is left as-is (for scripts with many literal braces).</summary>
    Keep,
}

public sealed record PreprocessorOptions
{
    public UnknownVariablePolicy Unknown { get; init; } = UnknownVariablePolicy.Error;

    /// <summary>Also expand sqlcmd-style $(name).</summary>
    public bool SqlCmdSyntax { get; init; } = true;
}

/// <summary>Per-script values for the change.* built-ins.</summary>
public sealed record ScriptContext(string Name, string Kind, string? ScriptHash);

/// <param name="Text">The SQL handed to the provider.</param>
/// <param name="UsesVolatile">True when a per-run value (deploy.utc, deploy.id, ...) was used, so the rendered text is not reproducible.</param>
/// <param name="Variables">Names referenced, after expansion.</param>
public sealed record RenderResult(string Text, bool UsesVolatile, IReadOnlyList<string> Variables);

public sealed record VariableReference(string Name, int Line, string Syntax);

/// <summary>
/// Expands variables in scripts before the provider sees them. Vendor-neutral.
/// <list type="bullet">
/// <item><c>{name}</c> and <c>{group.name}</c>: built-ins and user variables. Only an identifier directly inside the
/// braces is a token, so ODBC escapes (<c>{fn NOW()}</c>, <c>{d '2026-01-01'}</c>), JSON (<c>'{"a":1}'</c>) and
/// format strings (<c>'{0}'</c>) are untouched.</item>
/// <item><c>{{name}}</c> emits a literal <c>{name}</c>.</item>
/// <item><c>$(name)</c>: sqlcmd syntax over the same variables.</item>
/// <item>A script whose first line is <c>-- bsm:preprocess off</c> is passed through unchanged.</item>
/// </list>
/// User variable values may reference other variables (<c>calc_db: calc_{env}</c>); cycles are errors.
/// Raw scripts are what get hashed and certified; rendered text is hashed separately for audit.
/// </summary>
public sealed partial class ScriptPreprocessor
{
    /// <summary>Namespaces reserved for built-ins. User variables may not start with these.</summary>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["env", "db", "change", "bsm", "plan", "deploy"];

    private const string OffDirective = "bsm:preprocess off";

    private readonly Dictionary<string, VariableInfo> variables;
    private readonly PreprocessorOptions options;

    public ScriptPreprocessor(IEnumerable<VariableInfo> builtIns, IEnumerable<VariableInfo> userVariables, PreprocessorOptions? options = null)
    {
        this.options = options ?? new PreprocessorOptions();
        variables = new Dictionary<string, VariableInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtIn in builtIns)
        {
            variables[builtIn.Name] = builtIn;
        }

        foreach (var user in userVariables)
        {
            var name = user.Name.StartsWith("var.", StringComparison.OrdinalIgnoreCase) ? user.Name[4..] : user.Name;
            ValidateUserName(name);
            if (variables.TryGetValue(name, out var existing) && existing.Source == VariableSource.BuiltIn)
            {
                throw new MigrationException($"Variable '{name}' is built in and cannot be redefined.");
            }

            variables[name] = user with { Name = name };
        }
    }

    /// <summary>All variables with change.* shown as placeholders, values unexpanded.</summary>
    public IReadOnlyList<VariableInfo> Describe() => [.. variables.Values.OrderBy(v => v.Source).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The fully expanded value of a variable.</summary>
    public string Resolve(string name, ScriptContext? context = null)
    {
        var used = new List<string>();
        var volatileUsed = false;
        return Lookup(name, context, [], used, ref volatileUsed, 0)
            ?? throw new MigrationException($"Unknown variable '{name}'.");
    }

    public RenderResult Render(string script, ScriptContext context)
    {
        if (IsDisabled(script))
        {
            return new RenderResult(script, false, []);
        }

        var used = new List<string>();
        var volatileUsed = false;
        var errors = new List<string>();
        var text = Expand(script, context, [], used, ref volatileUsed, errors, 0);
        if (errors.Count > 0)
        {
            throw new MigrationException($"{context.Name} ({context.Kind}): {string.Join("; ", errors.Distinct())}");
        }

        return new RenderResult(text, volatileUsed, [.. used.Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>Every variable a script references, with line numbers (for offline checks).</summary>
    public static IReadOnlyList<VariableReference> FindReferences(string script, bool sqlCmdSyntax = true)
    {
        if (IsDisabled(script))
        {
            return [];
        }

        var refs = new List<VariableReference>();
        foreach (Match m in TokenRegex().Matches(script))
        {
            if (!m.Groups["escaped"].Success)
            {
                refs.Add(new VariableReference(m.Groups["brace"].Success ? m.Groups["brace"].Value : m.Groups["sqlcmd"].Value, LineOf(script, m.Index), m.Groups["brace"].Success ? "{}" : "$()"));
            }
        }

        return [.. refs.Where(r => sqlCmdSyntax || r.Syntax == "{}")];
    }

    /// <summary>
    /// Makes literal SQL (generated from a database, or taken from another tool) survive rendering unchanged:
    /// {name} becomes {{name}}. When it also contains $(name) text and sqlcmd syntax is on, it is returned
    /// unescaped with a "-- bsm:preprocess off" first line instead.
    /// </summary>
    public static string EscapeLiteral(string text, bool sqlCmdSyntax)
    {
        if (sqlCmdSyntax && SqlCmdTokenRegex().IsMatch(text))
        {
            return $"-- {OffDirective}\n" + text;
        }

        return LiteralBraceRegex().Replace(text, m => "{" + m.Value + "}");
    }

    [GeneratedRegex(@"(?<!\{)\{[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\}(?!\})")]
    private static partial Regex LiteralBraceRegex();

    [GeneratedRegex(@"\$\([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*\)")]
    private static partial Regex SqlCmdTokenRegex();

    /// <summary>True when the name is a built-in (including change.* which only exist while rendering).</summary>
    public static bool IsBuiltInName(string name) => ReservedPrefixes.Any(p => name.Equals(p, StringComparison.OrdinalIgnoreCase) || name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>Built-ins for a run. Values that change every run are marked volatile.</summary>
    public static IReadOnlyList<VariableInfo> BuiltIns(
        string environment,
        string? environmentDescription,
        string? database,
        string? host,
        int? port,
        string provider,
        string toolVersion,
        string stateSchema,
        string? planHead,
        string user,
        string machine,
        DateTimeOffset runStartedUtc,
        string runId)
    {
        var list = new List<VariableInfo>
        {
            new("env", environment, VariableSource.BuiltIn, "environment name (same as env.name)"),
            new("env.name", environment, VariableSource.BuiltIn, "environment name"),
            new("env.description", environmentDescription ?? string.Empty, VariableSource.BuiltIn, "environment description"),
            new("db", database ?? string.Empty, VariableSource.BuiltIn, "database name (same as db.name)"),
            new("db.name", database ?? string.Empty, VariableSource.BuiltIn, "database name"),
            new("db.host", host ?? string.Empty, VariableSource.BuiltIn, "server host"),
            new("db.port", port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, VariableSource.BuiltIn, "server port"),
            new("bsm.provider", provider, VariableSource.BuiltIn, "provider id"),
            new("bsm.version", toolVersion, VariableSource.BuiltIn, "Badger SQL Migrate version"),
            new("bsm.schema", stateSchema, VariableSource.BuiltIn, "schema holding the bsm journal"),
            new("plan.head", planHead ?? string.Empty, VariableSource.BuiltIn, "chain id of the last change in the plan"),
            new("deploy.user", user, VariableSource.BuiltIn, "OS user running bsm", Volatile: true),
            new("deploy.host", machine, VariableSource.BuiltIn, "machine running bsm", Volatile: true),
            new("deploy.utc", runStartedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), VariableSource.BuiltIn, "run start time, ISO 8601 UTC", Volatile: true),
            new("deploy.date", runStartedUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), VariableSource.BuiltIn, "run start date, UTC", Volatile: true),
            new("deploy.id", runId, VariableSource.BuiltIn, "unique id of this run", Volatile: true),
            new("change", "<change name>", VariableSource.BuiltIn, "current change or repeatable name (same as change.name)"),
            new("change.name", "<change name>", VariableSource.BuiltIn, "current change or repeatable name"),
            new("change.kind", "<deploy|revert|verify|repeatable>", VariableSource.BuiltIn, "which script is running"),
            new("change.hash", "<script hash>", VariableSource.BuiltIn, "the change's script hash"),
        };
        return list;
    }

    private static void ValidateUserName(string name)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new MigrationException($"Invalid variable name '{name}'. Use letters, digits and '_', with '.' between groups.");
        }

        if (IsBuiltInName(name))
        {
            throw new MigrationException($"Variable '{name}' uses a reserved name; {string.Join(", ", ReservedPrefixes.Select(p => p + ".*"))} are built-ins.");
        }
    }

    private string Expand(string text, ScriptContext? context, HashSet<string> stack, List<string> used, ref bool volatileUsed, List<string> errors, int line)
    {
        var sb = new StringBuilder(text.Length);
        var last = 0;
        foreach (Match m in TokenRegex().Matches(text))
        {
            sb.Append(text, last, m.Index - last);
            last = m.Index + m.Length;

            if (m.Groups["escaped"].Success)
            {
                sb.Append('{').Append(m.Groups["escaped"].Value).Append('}');
                continue;
            }

            var isBrace = m.Groups["brace"].Success;
            if (!isBrace && !options.SqlCmdSyntax)
            {
                sb.Append(m.Value);
                continue;
            }

            var name = isBrace ? m.Groups["brace"].Value : m.Groups["sqlcmd"].Value;
            var value = Lookup(name, context, stack, used, ref volatileUsed, line == 0 ? LineOf(text, m.Index) : line, errors);
            if (value is not null)
            {
                sb.Append(value);
            }
            else if (isBrace && options.Unknown == UnknownVariablePolicy.Keep && !IsBuiltInName(name) && !name.StartsWith("var.", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(m.Value);
            }
            else
            {
                var at = line == 0 ? LineOf(text, m.Index) : line;
                errors.Add($"line {at}: unknown variable {m.Value}. Define it under 'variables:' in bsm.yaml or pass --var {name}=value; write {{{{{name}}}}} for a literal");
                sb.Append(m.Value);
            }
        }

        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    private string? Lookup(string name, ScriptContext? context, HashSet<string> stack, List<string> used, ref bool volatileUsed, int line, List<string>? errors = null)
    {
        var key = name.StartsWith("var.", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        switch (key.ToLowerInvariant())
        {
            case "change" or "change.name" when context is not null:
                used.Add(key);
                return context.Name;
            case "change.kind" when context is not null:
                used.Add(key);
                return context.Kind;
            case "change.hash" when context is not null:
                used.Add(key);
                return context.ScriptHash ?? string.Empty;
        }

        if (!variables.TryGetValue(key, out var info) || (key.StartsWith("change", StringComparison.OrdinalIgnoreCase) && context is null))
        {
            return null;
        }

        used.Add(info.Name);
        volatileUsed |= info.Volatile;
        if (info.Source == VariableSource.BuiltIn || !info.Value.Contains('{') && !info.Value.Contains("$(", StringComparison.Ordinal))
        {
            return info.Value;
        }

        if (!stack.Add(info.Name))
        {
            throw new MigrationException($"Variable cycle: {string.Join(" -> ", stack)} -> {info.Name}.");
        }

        var nested = new List<string>();
        var expanded = Expand(info.Value, context, stack, used, ref volatileUsed, nested, line);
        stack.Remove(info.Name);
        if (nested.Count > 0)
        {
            var message = $"variable '{info.Name}' references an unknown variable ({string.Join("; ", nested)})";
            if (errors is null)
            {
                throw new MigrationException(message);
            }

            errors.Add(message);
        }

        return expanded;
    }

    private static bool IsDisabled(string script)
    {
        var firstLine = script.AsSpan().TrimStart();
        var newline = firstLine.IndexOf('\n');
        var line = (newline < 0 ? firstLine : firstLine[..newline]).Trim();
        return line.StartsWith("--", StringComparison.Ordinal) && line[2..].Trim().Equals(OffDirective, StringComparison.OrdinalIgnoreCase);
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    [GeneratedRegex(@"\{\{(?<escaped>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\}\}|\{(?<brace>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\}|\$\((?<sqlcmd>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\)")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex NameRegex();
}
