namespace Badger.SqlMigrate.Core.Plan;

/// <summary>Flags that can be attached to a change in the plan with a leading '!'.</summary>
[Flags]
public enum ChangeFlags
{
    None = 0,

    /// <summary>The change cannot be reverted; revert stops with an explicit error.</summary>
    Irreversible = 1,

    /// <summary>The change runs outside a transaction (e.g. ALTER DATABASE, full-text DDL).</summary>
    NoTransaction = 2,
}

/// <summary>One change line in bsm.plan.</summary>
public sealed record PlanChange(
    string Name,
    IReadOnlyList<string> Requires,
    ChangeFlags Flags,
    DateTimeOffset? PlannedAt,
    string? Author,
    string? Note,
    int LineNumber)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A tag line ("@release-1.4") that marks the change immediately above it.</summary>
public sealed record PlanTag(string Name, string? Note, int LineNumber);
