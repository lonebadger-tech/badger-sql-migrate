using System.Text.Json.Nodes;

namespace Badger.SqlMigrate.Core.Providers;

/// <summary>Maps the "provider:" id in bsm.yaml to an implementation.</summary>
public sealed class ProviderRegistry
{
    private readonly Dictionary<string, Func<JsonObject?, IDatabaseProvider>> factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> sections = new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry Register(string id, Func<IDatabaseProvider> factory)
    {
        factories[id] = _ => factory();
        return this;
    }

    /// <summary>A provider with project-level settings: the bsm.yaml top-level block named <paramref name="settingsSection"/>.</summary>
    public ProviderRegistry Register(string id, string settingsSection, Func<JsonObject?, IDatabaseProvider> factory)
    {
        factories[id] = factory;
        sections[id] = settingsSection;
        return this;
    }

    public IEnumerable<string> Ids => factories.Keys.Order(StringComparer.Ordinal);

    /// <summary>Every bsm.yaml top-level key that holds a provider's project settings.</summary>
    public IEnumerable<string> SettingsSections => sections.Values.Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>The bsm.yaml top-level key with this provider's project settings, if it takes any.</summary>
    public string? SettingsSection(string id) => sections.GetValueOrDefault(id);

    public IDatabaseProvider Resolve(string id, JsonObject? settings = null) =>
        factories.TryGetValue(id, out var factory)
            ? factory(settings)
            : throw new MigrationException($"Unknown provider '{id}'. Available providers: {string.Join(", ", Ids)}.");
}
