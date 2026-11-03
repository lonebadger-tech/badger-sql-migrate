namespace Badger.SqlMigrate.Core.Providers;

/// <summary>Maps the "provider:" id in badger.yaml to an implementation.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, Func<IDatabaseProvider>> factories = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry Register(string id, Func<IDatabaseProvider> factory)
    {
        factories[id] = factory;
        return this;
    }

    public IEnumerable<string> Ids => factories.Keys.Order(StringComparer.Ordinal);

    public IDatabaseProvider Resolve(string id) =>
        factories.TryGetValue(id, out var factory)
            ? factory()
            : throw new MigrationException($"Unknown provider '{id}'. Available providers: {string.Join(", ", Ids)}.");
}
