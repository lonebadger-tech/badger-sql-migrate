using Badger.SqlMigrate.Core.Hashing;
using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Core.Project;

/// <summary>A planned change with its scripts loaded from changes/&lt;name&gt;/.</summary>
public sealed class MigrationChange
{
    public MigrationChange(PlanChange plan, string deployScript, string? revertScript, string? verifyScript)
    {
        Plan = plan;
        DeployScript = ContentHasher.Normalize(deployScript);
        RevertScript = revertScript is null ? null : ContentHasher.Normalize(revertScript);
        VerifyScript = verifyScript is null ? null : ContentHasher.Normalize(verifyScript);
        ScriptHash = ComputeScriptHash(plan, DeployScript, RevertScript, VerifyScript);
    }

    public PlanChange Plan { get; }

    public string Name => Plan.Name;

    public string DeployScript { get; }

    public string? RevertScript { get; }

    public string? VerifyScript { get; }

    /// <summary>SHA-256 over the three scripts plus the semantic plan metadata (name, requires, flags).</summary>
    public string ScriptHash { get; }

    /// <summary>
    /// Merkle-chain id of this change at its position in the plan: SHA-256(provider, name, scriptHash, parentId).
    /// Set by <see cref="MigrationProject"/>.
    /// </summary>
    public string ChainId { get; internal set; } = string.Empty;

    public static string ComputeScriptHash(PlanChange plan, string deploy, string? revert, string? verify) =>
        ContentHasher.HashParts(
            ("name", plan.Name),
            ("requires", string.Join(',', plan.Requires)),
            ("flags", ((int)plan.Flags).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("deploy", deploy),
            ("revert", revert),
            ("verify", verify));

    public static string ComputeChainId(string providerId, string name, string scriptHash, string? parentChainId) =>
        ContentHasher.HashParts(
            ("provider", providerId),
            ("name", name),
            ("script", scriptHash),
            ("parent", parentChainId));
}
