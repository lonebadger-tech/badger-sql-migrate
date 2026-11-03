using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Core.Engine;

public enum ChangeState
{
    Pending,
    Applied,

    /// <summary>Applied, but the working copy's scripts no longer hash to what was deployed.</summary>
    HashMismatch,
}

public sealed record ChangeStatus(MigrationChange Change, int PlanIndex, ChangeState State, AppliedChange? Applied);

/// <summary>Pure comparison of a project against the database journal. No I/O.</summary>
public sealed class StatusReport
{
    private StatusReport(
        IReadOnlyList<ChangeStatus> changes,
        IReadOnlyList<AppliedChange> orphans,
        IReadOnlyList<AppliedChange> applied,
        bool certified,
        string? databaseHeadChainId,
        string? planHeadChainId)
    {
        Changes = changes;
        Orphans = orphans;
        Applied = applied;
        Certified = certified;
        DatabaseHeadChainId = databaseHeadChainId;
        PlanHeadChainId = planHeadChainId;
    }

    /// <summary>Every change in the plan with its state, in plan order.</summary>
    public IReadOnlyList<ChangeStatus> Changes { get; }

    /// <summary>Changes applied to the database that are not in the plan (removed or from another branch).</summary>
    public IReadOnlyList<AppliedChange> Orphans { get; }

    /// <summary>The database journal in deploy order.</summary>
    public IReadOnlyList<AppliedChange> Applied { get; }

    /// <summary>
    /// True when the database received exactly a prefix of the plan, in plan order, with identical script hashes.
    /// In that case <see cref="DatabaseHeadChainId"/> equals the plan's chain id at that position.
    /// </summary>
    public bool Certified { get; }

    public string? DatabaseHeadChainId { get; }

    public string? PlanHeadChainId { get; }

    public IEnumerable<ChangeStatus> Pending => Changes.Where(c => c.State == ChangeState.Pending);

    public IEnumerable<ChangeStatus> Mismatched => Changes.Where(c => c.State == ChangeState.HashMismatch);

    public bool UpToDate => !Pending.Any() && !Mismatched.Any() && Orphans.Count == 0;

    public static StatusReport Build(MigrationProject project, IReadOnlyList<AppliedChange> applied)
    {
        var byName = applied.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var statuses = new List<ChangeStatus>(project.Changes.Count);
        for (var i = 0; i < project.Changes.Count; i++)
        {
            var change = project.Changes[i];
            if (byName.TryGetValue(change.Name, out var row))
            {
                var state = string.Equals(row.ScriptHash, change.ScriptHash, StringComparison.Ordinal) ? ChangeState.Applied : ChangeState.HashMismatch;
                statuses.Add(new ChangeStatus(change, i, state, row));
            }
            else
            {
                statuses.Add(new ChangeStatus(change, i, ChangeState.Pending, null));
            }
        }

        var planNames = project.Changes.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var orphans = applied.Where(a => !planNames.Contains(a.Name)).ToList();

        var dbHead = applied.Count == 0 ? null : applied[^1].ChainId;
        var certified = applied.Count == 0
            || (applied.Count <= project.Changes.Count && string.Equals(dbHead, project.Changes[applied.Count - 1].ChainId, StringComparison.Ordinal));

        return new StatusReport(statuses, orphans, applied, certified, dbHead, project.HeadChainId);
    }
}
