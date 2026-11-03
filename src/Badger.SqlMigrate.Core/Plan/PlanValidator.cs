namespace Badger.SqlMigrate.Core.Plan;

/// <summary>Semantic checks on a plan: unique names, resolvable dependencies, no forward references or cycles.</summary>
public static class PlanValidator
{
    public static IReadOnlyList<string> Validate(PlanFile plan)
    {
        var problems = plan.Errors.Select(e => e.ToString()).ToList();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var tags = new HashSet<string>(StringComparer.Ordinal);

        foreach (var change in plan.Changes)
        {
            if (!seen.TryAdd(change.Name, change.LineNumber))
            {
                problems.Add($"line {change.LineNumber}: change '{change.Name}' is already defined on line {seen[change.Name]}.");
                continue;
            }

            foreach (var dep in change.Requires)
            {
                if (string.Equals(dep, change.Name, StringComparison.Ordinal))
                {
                    problems.Add($"line {change.LineNumber}: change '{change.Name}' requires itself.");
                }
                else if (!seen.ContainsKey(dep))
                {
                    var later = plan.IndexOf(dep) >= 0;
                    problems.Add(later
                        ? $"line {change.LineNumber}: change '{change.Name}' requires '{dep}', which appears later in the plan. Move '{dep}' above it."
                        : $"line {change.LineNumber}: change '{change.Name}' requires unknown change '{dep}'.");
                }
            }

            foreach (var tag in change.Tags)
            {
                if (!tags.Add(tag))
                {
                    problems.Add($"tag '@{tag}' is defined more than once.");
                }
            }
        }

        return problems;
    }
}
