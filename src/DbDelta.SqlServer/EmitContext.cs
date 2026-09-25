using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer;

// What one table's alterations need to know about the rest of the plan. A foreign key sits between two
// tables, so whether one table's emission may drop and re-add it depends on what the plan is doing to the
// other: dropping that table, rebuilding it, or changing the key itself. Deciding it per table in
// isolation is how the same constraint gets dropped twice, or re-added beside a copy that already exists.
internal sealed class EmitContext
{
    private readonly HashSet<ObjectIdentity> _droppedTables;
    private readonly Dictionary<ObjectIdentity, ObjectDiff> _changes;
    private readonly HashSet<(ObjectIdentity Table, string Name)> _droppedKeys = [];

    public EmitContext(
        DatabaseSchema source,
        DatabaseSchema target,
        IReadOnlyList<ObjectDiff> changes,
        HashSet<ObjectIdentity> deferred,
        List<ScriptStep> steps,
        List<string> refusals)
    {
        Source = source;
        Target = target;
        Deferred = deferred;
        Steps = steps;
        Refusals = refusals;
        _changes = changes.ToDictionary(c => c.Identity);
        _droppedTables = changes
            .Where(c => c.Kind == DiffKind.TargetOnly && c.Identity.Type == ObjectType.Table)
            .Select(c => c.Identity)
            .ToHashSet();
    }

    public DatabaseSchema Source { get; }

    public DatabaseSchema Target { get; }

    public string? Collation => Target.Collation;

    public HashSet<ObjectIdentity> Deferred { get; }

    public List<ScriptStep> Steps { get; }

    public List<string> Refusals { get; }

    public HashSet<ObjectIdentity> Rebuilt { get; } = [];

    // Programmable steps a rebuild owes the target — its triggers back, its dependent views refreshed. They
    // are appended after the plan's own programmables, so a refresh sees any view the plan has altered.
    public List<ScriptStep> Trailing { get; } = [];

    public bool InPlan(ObjectIdentity identity) => _changes.ContainsKey(identity);

    public bool Defers(ObjectIdentity table) => Deferred.Contains(table);

    // The plan already drops this key, or the table it lives on, before anything here would run.
    public bool PlanDrops(ObjectIdentity table, string key) =>
        _droppedTables.Contains(table)
        || (_changes.TryGetValue(table, out var change)
            && change.Kind == DiffKind.Different
            && change.DifferingChildren.Any(c =>
                c.Identity.Type == ObjectType.ForeignKey
                && string.Equals(c.Identity.Name, key, StringComparison.OrdinalIgnoreCase)));

    // Drops a foreign key on another table for the length of a change to the one it references, and puts
    // it back as the target had it — unless the table it lives on is rebuilt, which re-adds that table's
    // keys from the source by itself.
    public void BracketInbound(InboundForeignKey inbound, string reason) =>
        BracketForeignKey(
            inbound.Table,
            inbound.Key.Name,
            Rebuilt.Contains(inbound.Table) ? null : inbound.Key,
            reason);

    // One key between two changing tables is reached twice — as the child's outbound key and as the
    // parent's inbound one — so both go through here and only the first drops it and puts it back. The
    // two would restore the same definition: a key the plan changes is the plan's to drop, not this.
    public void BracketForeignKey(ObjectIdentity table, string name, ForeignKeyDefinition? restore, string reason)
    {
        if (PlanDrops(table, name) || !_droppedKeys.Add((table, name.ToUpperInvariant())))
        {
            return;
        }

        Steps.Add(new ScriptStep(
            ScriptPhase.DropForeignKeys,
            $"drop foreign key {name} on {table.QualifiedName} — {reason}",
            TSqlWriter.DropConstraint(table, name)));

        if (restore is not null)
        {
            Steps.Add(new ScriptStep(
                ScriptPhase.AddForeignKeys,
                $"restore foreign key {name} on {table.QualifiedName}",
                Batch(TSqlWriter.AddForeignKey(table, restore), Defers(table))));
        }
    }

    public static string Batch(string sql, bool defer) =>
        defer ? TSqlWriter.ExecuteAsBatch(sql) : sql;
}
