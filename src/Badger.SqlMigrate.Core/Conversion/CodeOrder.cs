namespace Badger.SqlMigrate.Core.Conversion;

public static class CodeOrder
{
    /// <summary>
    /// Orders code objects so each comes after the objects it depends on, keeping the original order otherwise.
    /// Cycles (possible with mutually recursive routines) keep their original order.
    /// </summary>
    public static List<PortableCode> Sort(IEnumerable<PortableCode> code)
    {
        var items = code.ToList();
        var byKey = items.GroupBy(c => c.Name.Key).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<PortableCode>(items.Count);
        var state = new Dictionary<PortableCode, int>(ReferenceEqualityComparer.Instance);

        void Visit(PortableCode item)
        {
            if (state.TryGetValue(item, out var s))
            {
                return;
            }

            state[item] = 1;
            foreach (var dependency in item.DependsOn)
            {
                if (byKey.TryGetValue(dependency, out var targets))
                {
                    foreach (var target in targets.Where(t => !ReferenceEquals(t, item) && !state.ContainsKey(t)))
                    {
                        Visit(target);
                    }
                }
            }

            if (item.TriggerFunction is { } function && byKey.TryGetValue(function.Key, out var functions))
            {
                foreach (var target in functions.Where(t => !state.ContainsKey(t)))
                {
                    Visit(target);
                }
            }

            state[item] = 2;
            result.Add(item);
        }

        foreach (var item in items)
        {
            Visit(item);
        }

        return result;
    }
}
