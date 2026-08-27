using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Data;

// The target-side state of every row a plan touches, at the moment the plan was built. Two snapshots
// taken at different times answer the question a schema-only drift check cannot: did the rows this plan
// is about to write move under it since it was reviewed.
//
// Only affected rows are recorded, and that is the point. A row the plan does not touch can change all
// it likes — refusing to apply because an unrelated row moved would make the check unusable on any
// database in real use. The rows that matter are the ones about to be overwritten, deleted, or inserted
// on top of.
public sealed class RowStateSnapshot
{
    private readonly Dictionary<ObjectIdentity, Dictionary<string, string?>> _tables;

    private RowStateSnapshot(Dictionary<ObjectIdentity, Dictionary<string, string?>> tables) =>
        _tables = tables;

    public int TableCount => _tables.Count;

    public int RowCount => _tables.Values.Sum(t => t.Count);

    public static RowStateSnapshot From(IEnumerable<TableDataChanges> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var captured = new Dictionary<ObjectIdentity, Dictionary<string, string?>>();

        foreach (var table in tables)
        {
            var rows = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var change in table.Changes)
            {
                // An insert records null: what has to still be true is that the target does not have it.
                rows[change.Key] = change.Classification == RowClassification.Insert ? null : change.TargetHash;
            }

            captured[table.Table.Identity] = rows;
        }

        return new RowStateSnapshot(captured);
    }

    // Every way the two can disagree is drift, and each says something different about what happened.
    public IReadOnlyList<string> DriftAgainst(RowStateSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var drift = new List<string>();

        foreach (var (table, reviewed) in _tables.OrderBy(t => t.Key.QualifiedName, StringComparer.OrdinalIgnoreCase))
        {
            if (!current._tables.TryGetValue(table, out var now))
            {
                drift.Add($"{table.QualifiedName} is no longer in the plan.");
                continue;
            }

            var moved = reviewed.Count(r =>
                now.TryGetValue(r.Key, out var hash) && !string.Equals(hash, r.Value, StringComparison.Ordinal));
            var settled = reviewed.Keys.Count(k => !now.ContainsKey(k));
            var appeared = now.Keys.Count(k => !reviewed.ContainsKey(k));

            if (moved > 0)
            {
                drift.Add($"{table.QualifiedName}: {moved} row(s) the plan would write have changed on the "
                    + "target since you reviewed it.");
            }

            if (settled > 0)
            {
                drift.Add($"{table.QualifiedName}: {settled} row(s) no longer differ — the target already "
                    + "matches the source there.");
            }

            if (appeared > 0)
            {
                drift.Add($"{table.QualifiedName}: {appeared} row(s) now differ that did not when you "
                    + "reviewed the plan.");
            }
        }

        foreach (var (table, _) in current._tables.Where(t => !_tables.ContainsKey(t.Key)))
        {
            drift.Add($"{table.QualifiedName} has entered the plan since you reviewed it.");
        }

        return drift;
    }
}
