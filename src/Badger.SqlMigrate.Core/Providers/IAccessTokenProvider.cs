using Badger.SqlMigrate.Core.Configuration;

namespace Badger.SqlMigrate.Core.Providers;

/// <summary>Supplies OAuth access tokens (Entra ID) to providers without them referencing an identity library.</summary>
public interface IAccessTokenProvider
{
    /// <summary>Scopes requested when the driver does not say which resource it needs.</summary>
    string DefaultScope { get; }

    Task<AccessTokenResult> GetTokenAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken);
}

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresOn);

/// <summary>What the host (CLI or library user) gives a provider when opening a session.</summary>
public sealed record SessionContext
{
    public static readonly SessionContext Empty = new();

    /// <summary>Set when an SSH tunnel is in use: connect here instead of the configured host.</summary>
    public Endpoint? EndpointOverride { get; init; }

    /// <summary>Entra token source for the environment's <see cref="AuthenticationKind"/>, if it uses one.</summary>
    public IAccessTokenProvider? Tokens { get; init; }

    public Action<string>? Warn { get; init; }

    /// <summary>Project directory, for resolving relative paths in provider settings (e.g. custom key store assemblies).</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>
    /// Creates additional token sources, e.g. a separate identity for Azure Key Vault (Always Encrypted keys).
    /// Returns null for kinds that are not token based.
    /// </summary>
    public Func<AuthenticationKind, EntraOptions, IAccessTokenProvider?>? CreateTokenProvider { get; init; }
}
