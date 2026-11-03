using System.Text.Json;
using System.Text.Json.Serialization;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;

namespace Badger.SqlMigrate.Providers.SqlServer;

public enum PartitionBoundaries
{
    /// <summary>Boundary values and the scheme's filegroup per partition are part of the fingerprint.</summary>
    Include,

    /// <summary>
    /// Only the function's type and direction and the scheme's set of filegroups count, for databases whose
    /// partitions are split and merged by a job outside bsm (a sliding window).
    /// </summary>
    Ignore,
}

/// <summary>
/// The environment's <c>schemaFingerprint:</c> block:
/// <code>
/// schemaFingerprint:
///   partitionBoundaries: ignore   # include (default) | ignore
/// </code>
/// </summary>
public sealed class SchemaFingerprintSettings
{
    public const string SectionName = "schemaFingerprint";

    public static readonly SchemaFingerprintSettings Default = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public PartitionBoundaries PartitionBoundaries { get; init; } = PartitionBoundaries.Include;

    public static SchemaFingerprintSettings From(DeploymentEnvironment environment)
    {
        if (!environment.ProviderSettings.TryGetPropertyValue(SectionName, out var node) || node is null)
        {
            return Default;
        }

        try
        {
            return node.Deserialize<SchemaFingerprintSettings>(JsonOptions) ?? Default;
        }
        catch (JsonException ex)
        {
            throw new MigrationException($"environment '{environment.Name}': invalid {SectionName} settings: {ex.Message}", ex);
        }
    }
}
