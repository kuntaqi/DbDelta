using DbDelta.Api.Contracts;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.Api.Services;

// A whole-schema ER diagram of 142 tables is unreadable spaghetti, which is how most database diagram
// tools become decoration. The unit here is a neighbourhood: one table, a bounded number of hops, with
// what the sync plan intends to do overlaid on it.
public sealed class FkMapService
{
    public FkMapResponse Build(CompareSession session, string table, int depth, FkDirection direction)
    {
        ArgumentNullException.ThrowIfNull(session);

        var focus = session.Source.Tables.FirstOrDefault(t =>
            string.Equals(t.Identity.QualifiedName, table, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{table} is not a table on the source.");

        depth = Math.Clamp(depth, 1, 3);

        var graph = TableDependencyGraph.Build(session.Source.Tables);

        // Walked twice over: once for what is drawn, once for the whole neighbourhood. The filter is
        // about what fits on screen, and the notes are about what the plan has to survive — hiding a
        // direction must not hide the warning that something is over there.
        var shown = new Dictionary<ObjectIdentity, int> { [focus.Identity] = 0 };
        var full = new Dictionary<ObjectIdentity, int> { [focus.Identity] = 0 };

        Walk(graph, focus.Identity, depth, -1, full);
        Walk(graph, focus.Identity, depth, 1, full);

        if (direction != FkDirection.Children)
        {
            Walk(graph, focus.Identity, depth, -1, shown);
        }

        if (direction != FkDirection.Parents)
        {
            Walk(graph, focus.Identity, depth, 1, shown);
        }

        var edges = BuildEdges(session, shown.Keys.ToHashSet(), graph);

        return new FkMapResponse(
            focus.Identity.QualifiedName,
            depth,
            direction.ToString(),
            shown.Select(pair => Node(session, pair.Key, pair.Value)).OrderBy(n => n.Band).ToList(),
            edges,
            graph.OrderForData().Cyclic.Select(c => c.QualifiedName).ToList(),
            Notes(session, focus, full, BuildEdges(session, full.Keys.ToHashSet(), graph), direction));
    }

    private static void Walk(
        TableDependencyGraph graph,
        ObjectIdentity from,
        int depth,
        int direction,
        Dictionary<ObjectIdentity, int> bands)
    {
        var frontier = new List<ObjectIdentity> { from };

        for (var hop = 1; hop <= depth; hop++)
        {
            var next = new List<ObjectIdentity>();

            foreach (var current in frontier)
            {
                var neighbours = direction < 0 ? graph.ParentsOf(current) : graph.ChildrenOf(current);

                foreach (var neighbour in neighbours.Where(n => !bands.ContainsKey(n)))
                {
                    bands[neighbour] = hop * direction;
                    next.Add(neighbour);
                }
            }

            frontier = next;
        }
    }

    private static List<FkEdge> BuildEdges(
        CompareSession session,
        HashSet<ObjectIdentity> included,
        TableDependencyGraph graph)
    {
        var cyclic = graph.OrderForData().Cyclic.ToHashSet();
        var edges = new List<FkEdge>();

        foreach (var table in session.Source.Tables.Where(t => included.Contains(t.Identity)))
        {
            foreach (var fk in table.ForeignKeys.Where(f => included.Contains(f.ReferencedTable)))
            {
                // Whether an orphan is tolerable or a hard failure comes down to nullability, and no
                // generic ER view shows that next to a sync plan.
                var nullable = fk.Columns.All(column =>
                    table.Columns.Any(c =>
                        string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase) && c.IsNullable));

                edges.Add(new FkEdge(
                    table.Identity.QualifiedName,
                    fk.ReferencedTable.QualifiedName,
                    fk.Name,
                    string.Join(", ", fk.Columns),
                    nullable,
                    cyclic.Contains(table.Identity) && cyclic.Contains(fk.ReferencedTable)));
            }
        }

        return edges;
    }

    private static FkNode Node(CompareSession session, ObjectIdentity identity, int band)
    {
        var diff = session.Diff.Find(identity);

        var (state, note) = band == 0
            ? ("Focus", Describe(diff))
            : diff?.Kind switch
            {
                DiffKind.SourceOnly => ("InPlan", "will be created"),
                DiffKind.Different => ("InPlan", "will be altered"),
                DiffKind.TargetOnly => ("Excluded", "only on target"),
                _ => ("Untouched", "identical, nothing to do")
            };

        return new FkNode(identity.QualifiedName, band, state, note, 0, 0);
    }

    private static string Describe(ObjectDiff? diff) => diff?.Kind switch
    {
        DiffKind.SourceOnly => "will be created",
        DiffKind.Different => "will be altered",
        DiffKind.TargetOnly => "only on target",
        _ => "identical"
    };

    // Computed over the whole neighbourhood, not the filtered one. What is drawn is a viewing choice;
    // what a plan has to survive is not, and a note that disappears when a direction is hidden would
    // make the filter a way of not being told.
    private static List<string> Notes(
        CompareSession session,
        TableDefinition focus,
        Dictionary<ObjectIdentity, int> bands,
        IReadOnlyList<FkEdge> edges,
        FkDirection direction)
    {
        var notes = new List<string>();

        var hidden = direction switch
        {
            FkDirection.Parents => bands.Count(b => b.Value > 0),
            FkDirection.Children => bands.Count(b => b.Value < 0),
            _ => 0
        };

        if (hidden > 0)
        {
            var kind = direction == FkDirection.Parents ? "child" : "parent";
            notes.Add($"{hidden} {kind} table(s) are in this neighbourhood but hidden by the direction filter. "
                + "They still matter to the plan; they are just not drawn.");
        }

        var requiredParents = edges
            .Where(e => e.From == focus.Identity.QualifiedName && !e.Nullable)
            .Select(e => e.To)
            .ToList();

        if (requiredParents.Count > 0)
        {
            notes.Add($"{requiredParents.Count} parent(s) are NOT NULL and must exist before {focus.Identity.Name}: "
                + string.Join(", ", requiredParents));
        }

        var optional = edges.Count(e => e.From == focus.Identity.QualifiedName && e.Nullable);
        if (optional > 0)
        {
            notes.Add($"{optional} parent reference(s) are nullable, so leaving them out only orphans the column.");
        }

        var children = bands.Count(b => b.Value > 0);
        if (children > 0)
        {
            notes.Add($"{children} table(s) reference {focus.Identity.Name}; seeding it with a limited row set "
                + "would leave their rows pointing at rows that were never created.");
        }

        if (session.Target.Tables.All(t => t.Identity != focus.Identity))
        {
            notes.Add($"{focus.Identity.Name} does not exist on the target yet, so everything shown will be created.");
        }

        return notes;
    }
}
