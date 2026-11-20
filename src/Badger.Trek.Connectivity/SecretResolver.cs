using System.Text.RegularExpressions;
using Badger.Trek.Core;

namespace Badger.Trek.Connectivity;

/// <summary>
/// Expands secret references in config values so secrets never live in trek.yaml:
/// <c>${env:NAME}</c> reads an environment variable, <c>${file:path}</c> reads a file (e.g. a mounted secret).
/// </summary>
public static partial class SecretResolver
{
    public static string? Resolve(string? value)
    {
        if (value is null || !value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        return ReferenceRegex().Replace(value, m =>
        {
            var kind = m.Groups[1].Value.ToLowerInvariant();
            var arg = m.Groups[2].Value;
            return kind switch
            {
                "env" => Environment.GetEnvironmentVariable(arg)
                    ?? throw new MigrationException($"Environment variable '{arg}' referenced in trek.yaml is not set."),
                "file" => File.Exists(SshTunnel.ExpandHome(arg))
                    ? File.ReadAllText(SshTunnel.ExpandHome(arg)).TrimEnd('\r', '\n')
                    : throw new MigrationException($"Secret file '{arg}' referenced in trek.yaml does not exist."),
                _ => throw new MigrationException($"Unknown secret reference '${{{kind}:...}}'. Use ${{env:NAME}} or ${{file:path}}."),
            };
        });
    }

    [GeneratedRegex(@"\$\{(env|file|[a-z]+):([^}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceRegex();
}
