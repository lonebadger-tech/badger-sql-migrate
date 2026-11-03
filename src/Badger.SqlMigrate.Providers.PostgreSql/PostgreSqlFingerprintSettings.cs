using System.Text.Json;
using System.Text.Json.Serialization;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Configuration;

namespace Badger.SqlMigrate.Providers.PostgreSql;

public enum PartitionBoundaries
{
    /// <summary>Partitions (child tables) and their columns, constraints and indexes are part of the fingerprint.</summary>
    Include,

    /// <summary>Partitions are left out, for databases where a job (pg_partman, cron) creates and drops them; the partitioned table and its key still count.</summary>
    Ignore,
}

public enum FingerprintPart
{
    Include,
    Ignore,
}

/// <summary>
/// The environment's <c>schemaFingerprint:</c> block for PostgreSQL:
/// <code>
/// schemaFingerprint:
///   partitionBoundaries: ignore   # include (default) | ignore: leave partitions a job maintains out
///   privileges: ignore            # include (default) | ignore: leave GRANTs and default privileges out
/// </code>
/// </summary>
public sealed class PostgreSqlFingerprintSettings
{
    public const string SectionName = "schemaFingerprint";

    public static readonly PostgreSqlFingerprintSettings Default = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public PartitionBoundaries PartitionBoundaries { get; init; } = PartitionBoundaries.Include;

    public FingerprintPart Privileges { get; init; } = FingerprintPart.Include;

    public static PostgreSqlFingerprintSettings From(DeploymentEnvironment environment)
    {
        if (!environment.ProviderSettings.TryGetPropertyValue(SectionName, out var node) || node is null)
        {
            return Default;
        }

        try
        {
            return node.Deserialize<PostgreSqlFingerprintSettings>(JsonOptions) ?? Default;
        }
        catch (JsonException ex)
        {
            throw new MigrationException($"environment '{environment.Name}': invalid {SectionName} settings: {ex.Message}", ex);
        }
    }
}
