using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Data;

// Seeding the top 100 of a child table writes rows whose foreign keys point at parents the target may
// not have, and the insert fails on the key. So the rows a plan writes are walked upward: every
// referenced parent row that is missing on the target is fetched from the source and inserted first.
//
// Upward only. Following children transitively is the full database-subsetting problem and would drag
// in most of the database; following parents is bounded by the keys the rows actually carry.
//
// This is not limited to Top N. A row is a row: an insert under All rows can reference a parent the
// target lacks just as easily, and the constraint does not care which mode produced it.
public sealed class ParentClosure
{
    private readonly IRowByValueReader _source;
    private readonly IRowByValueReader _target;
    private readonly int _maxRows;

    public ParentClosure(IRowByValueReader source, IRowByValueReader target, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);

        _source = source;
        _target = target;
        _maxRows = maxRows;
    }

    public async Task<ParentClosureResult> ExpandAsync(
        DatabaseSchema sourceSchema,
        DatabaseSchema targetSchema,
        IReadOnlyList<TableDataChanges> planned,
        Func<TableDefinition, IReadOnlyList<string>> keyFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSchema);
        ArgumentNullException.ThrowIfNull(targetSchema);
        ArgumentNullException.ThrowIfNull(planned);
        ArgumentNullException.ThrowIfNull(keyFor);

        var state = planned.ToDictionary(p => p.Table.Identity, TableState.From);
        var added = new List<RequiredRows>();
        var warnings = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new Dictionary<ObjectIdentity, HashSet<string>>();

        var queue = new Queue<(ObjectIdentity Table, IReadOnlyList<DataChange> Rows)>(
            planned.Select(p => (p.Table.Identity, p.Changes)));

        var pulled = 0;
        var capped = false;

        while (queue.Count > 0 && !capped)
        {
            var (identity, rows) = queue.Dequeue();

            if (!state.TryGetValue(identity, out var child))
            {
                continue;
            }

            foreach (var name in ParentReferenceCollector.Unreadable(child.Table, child.AllColumns))
            {
                Warn(warnings, reported,
                    $"{identity.QualifiedName}: {name} could not be checked because its columns are not part of "
                    + "this comparison, so the rows it points at were not verified against the target.");
            }

            foreach (var reference in ParentReferenceCollector.From(child.Table, rows, child.AllColumns))
            {
                var parentSource = sourceSchema.Tables.FirstOrDefault(t => t.Identity == reference.Parent);
                if (parentSource is null)
                {
                    Warn(warnings, reported,
                        $"{identity.QualifiedName}: {reference.ForeignKeyName} points at "
                        + $"{reference.Parent.QualifiedName}, which is not in this comparison.");
                    continue;
                }

                var parent = Ensure(state, targetSchema, parentSource, reference, keyFor);
                if (parent is null)
                {
                    Warn(warnings, reported,
                        $"{reference.Parent.QualifiedName} is already in the plan without the columns "
                        + $"{reference.ForeignKeyName} points at, so its rows could not be matched. Compare "
                        + $"{reference.Parent.QualifiedName} directly to include them.");
                    continue;
                }

                var known = Known(resolved, reference.Parent);
                var wanted = reference.Keys
                    .Where(k => !known.Contains(k.Canonical) && !parent.Holds(reference.ParentColumns, k))
                    .ToList();

                if (wanted.Count == 0)
                {
                    continue;
                }

                var missing = await MissingAsync(
                    targetSchema, reference, wanted, cancellationToken).ConfigureAwait(false);

                foreach (var key in wanted)
                {
                    known.Add(key.Canonical);
                }

                if (missing.Count == 0)
                {
                    continue;
                }

                // A silent cap would read as "closure found nothing more", which is the opposite of what
                // an over-large expansion means.
                if (pulled + missing.Count > _maxRows)
                {
                    warnings.Add(
                        $"Parent closure stopped at {_maxRows} row(s). {reference.Parent.QualifiedName} still "
                        + $"needs {missing.Count} more for {reference.ForeignKeyName}. Compare that table "
                        + "directly instead of seeding into it.");
                    capped = true;
                    break;
                }

                var fetched = await _source.FetchAsync(
                    parentSource, reference.ParentColumns, missing, parent.AllColumns, cancellationToken)
                    .ConfigureAwait(false);

                var found = fetched.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

                foreach (var key in missing.Where(k => !found.Contains(k.Canonical)))
                {
                    Warn(warnings, reported,
                        $"{identity.QualifiedName}: {reference.ForeignKeyName} references "
                        + $"{reference.Parent.QualifiedName} ({key.Display}), which is on neither side. The "
                        + "source itself is missing it, so nothing can be written to satisfy the key.");
                }

                var inserts = fetched.Select(parent.Insert).ToList();
                if (inserts.Count == 0)
                {
                    continue;
                }

                parent.Add(inserts);
                pulled += inserts.Count;
                added.Add(new RequiredRows(
                    reference.Parent, inserts.Count, identity, reference.ForeignKeyName));

                // The parents may have parents. Only the new rows are re-examined; the ones already in
                // the plan were walked when they went in.
                queue.Enqueue((reference.Parent, inserts));
            }
        }

        return new ParentClosureResult
        {
            Tables = state.Values.Select(s => s.Build()).ToList(),
            Added = added,
            Warnings = warnings
        };
    }

    private async Task<IReadOnlyList<ParentKey>> MissingAsync(
        DatabaseSchema targetSchema,
        ParentReference reference,
        IReadOnlyList<ParentKey> wanted,
        CancellationToken cancellationToken)
    {
        var parentTarget = targetSchema.Tables.FirstOrDefault(t => t.Identity == reference.Parent);

        // No table on the target means every parent row is missing, and querying it would fail. Schema
        // closure is what puts the table itself in the plan; this puts the rows in it.
        if (parentTarget is null)
        {
            return wanted;
        }

        var present = await _target.FetchAsync(
            parentTarget, reference.ParentColumns, wanted, reference.ParentColumns, cancellationToken)
            .ConfigureAwait(false);

        var here = present.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

        return wanted.Where(k => !here.Contains(k.Canonical)).ToList();
    }

    // A parent already in the plan is used as it stands, so the rows added to it are written with the
    // same column list as the rest. One that is not in the plan gets an entry built for it here.
    private static TableState? Ensure(
        Dictionary<ObjectIdentity, TableState> state,
        DatabaseSchema targetSchema,
        TableDefinition parentSource,
        ParentReference reference,
        Func<TableDefinition, IReadOnlyList<string>> keyFor)
    {
        if (state.TryGetValue(reference.Parent, out var existing))
        {
            // Without the referenced columns there is no way to tell an already-planned parent row from
            // one that still has to be pulled in, and guessing would emit the same row twice.
            return reference.ParentColumns.All(c =>
                existing.AllColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                ? existing
                : null;
        }

        var keyColumns = keyFor(parentSource);
        if (keyColumns.Count == 0)
        {
            keyColumns = reference.ParentColumns;
        }

        var request = new DataCompareRequest { Table = reference.Parent, KeyColumns = keyColumns };
        var parentTarget = targetSchema.Tables.FirstOrDefault(t => t.Identity == reference.Parent);

        // Resolving the source against itself when the target has no such table yet: the table will be
        // created from the source, so every source column will be there — but computed and rowversion
        // columns still have to be dropped, and the resolver is what knows which those are.
        var columns = ColumnSetResolver.Resolve(parentSource, parentTarget ?? parentSource, request);

        var created = new TableState(parentSource, keyColumns, columns.ComparedColumns, []);
        state[reference.Parent] = created;

        return created;
    }

    private static HashSet<string> Known(
        Dictionary<ObjectIdentity, HashSet<string>> resolved,
        ObjectIdentity table)
    {
        if (!resolved.TryGetValue(table, out var known))
        {
            known = new HashSet<string>(StringComparer.Ordinal);
            resolved[table] = known;
        }

        return known;
    }

    private static void Warn(List<string> warnings, HashSet<string> reported, string message)
    {
        if (reported.Add(message))
        {
            warnings.Add(message);
        }
    }
}
