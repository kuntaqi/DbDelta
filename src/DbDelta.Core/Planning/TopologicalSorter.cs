namespace DbDelta.Core.Planning;

public static class TopologicalSorter
{
    // Kahn's algorithm, always taking the earliest ready node in input order so the output is stable
    // run to run. Nodes left over when nothing is ready are the ones caught in a cycle; they are
    // reported rather than thrown, because the caller has better fallbacks than failing.
    public static TopologicalSortResult<T> Sort<T>(
        IEnumerable<T> nodes,
        Func<T, IEnumerable<T>> dependencies,
        IEqualityComparer<T>? comparer = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(dependencies);

        comparer ??= EqualityComparer<T>.Default;

        var all = nodes.Distinct(comparer).ToList();
        var index = new Dictionary<T, int>(comparer);
        for (var i = 0; i < all.Count; i++)
        {
            index[all[i]] = i;
        }

        var dependents = new Dictionary<T, List<T>>(comparer);
        var remaining = new Dictionary<T, int>(comparer);

        foreach (var node in all)
        {
            remaining[node] = 0;
        }

        foreach (var node in all)
        {
            foreach (var dependency in dependencies(node).Distinct(comparer))
            {
                if (comparer.Equals(dependency, node) || !index.ContainsKey(dependency))
                {
                    continue;
                }

                if (!dependents.TryGetValue(dependency, out var list))
                {
                    list = [];
                    dependents[dependency] = list;
                }

                list.Add(node);
                remaining[node]++;
            }
        }

        var ready = new List<T>(all.Where(n => remaining[n] == 0));
        var ordered = new List<T>(all.Count);

        while (ready.Count > 0)
        {
            var pick = 0;
            for (var i = 1; i < ready.Count; i++)
            {
                if (index[ready[i]] < index[ready[pick]])
                {
                    pick = i;
                }
            }

            var node = ready[pick];
            ready.RemoveAt(pick);
            ordered.Add(node);

            if (!dependents.TryGetValue(node, out var next))
            {
                continue;
            }

            foreach (var dependent in next)
            {
                if (--remaining[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        var ordering = new HashSet<T>(ordered, comparer);

        return new TopologicalSortResult<T>
        {
            Ordered = ordered,
            Cyclic = all.Where(n => !ordering.Contains(n)).ToList()
        };
    }
}
