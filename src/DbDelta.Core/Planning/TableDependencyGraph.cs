using DbDelta.Core.Model;

namespace DbDelta.Core.Planning;

public sealed class TableDependencyGraph
{
    private readonly Dictionary<ObjectIdentity, List<ObjectIdentity>> _parents;
    private readonly Dictionary<ObjectIdentity, List<ObjectIdentity>> _children;

    private TableDependencyGraph(
        Dictionary<ObjectIdentity, List<ObjectIdentity>> parents,
        Dictionary<ObjectIdentity, List<ObjectIdentity>> children)
    {
        _parents = parents;
        _children = children;
    }

    public static TableDependencyGraph Build(IReadOnlyList<TableDefinition> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var known = tables.Select(t => t.Identity).ToHashSet();
        var parents = new Dictionary<ObjectIdentity, List<ObjectIdentity>>();
        var children = new Dictionary<ObjectIdentity, List<ObjectIdentity>>();

        foreach (var table in tables)
        {
            parents[table.Identity] = [];
            children[table.Identity] = [];
        }

        foreach (var table in tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!known.Contains(fk.ReferencedTable) || fk.ReferencedTable == table.Identity)
                {
                    continue;
                }

                if (!parents[table.Identity].Contains(fk.ReferencedTable))
                {
                    parents[table.Identity].Add(fk.ReferencedTable);
                    children[fk.ReferencedTable].Add(table.Identity);
                }
            }
        }

        return new TableDependencyGraph(parents, children);
    }

    public IReadOnlyList<ObjectIdentity> ParentsOf(ObjectIdentity table) =>
        _parents.TryGetValue(table, out var p) ? p : [];

    public IReadOnlyList<ObjectIdentity> ChildrenOf(ObjectIdentity table) =>
        _children.TryGetValue(table, out var c) ? c : [];

    public IReadOnlyCollection<ObjectIdentity> Tables => _parents.Keys;

    // Data must land parents-first: a child row cannot reference a parent row that is not there yet.
    public TopologicalSortResult<ObjectIdentity> OrderForData() =>
        TopologicalSorter.Sort(_parents.Keys.ToList(), ParentsOf);

    // Every table reachable upward from the seeds, seeds included. Used to pull the parent rows a
    // seeded table needs; deliberately upward only, since following children transitively is the
    // full subsetting problem and would drag in most of the database.
    public IReadOnlyList<ObjectIdentity> ParentClosure(IEnumerable<ObjectIdentity> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        var visited = new HashSet<ObjectIdentity>();
        var queue = new Queue<ObjectIdentity>(seeds);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var parent in ParentsOf(current))
            {
                if (!visited.Contains(parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return visited.ToList();
    }
}
