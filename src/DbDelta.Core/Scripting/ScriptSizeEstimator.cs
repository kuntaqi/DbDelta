namespace DbDelta.Core.Scripting;

using DbDelta.Core.Data;

// How large a table's DML would be as literal SQL, measured before any of it is built. The old check
// assembled the whole script and then counted it, which meant an oversized plan had already paid for the
// string it was about to warn about. Deciding first is what lets a table take the bulk path instead.
//
// Deliberately an estimate. The exact number needs the emitted text, and the only decision it feeds is
// "inline or staged", which does not turn on a few hundred bytes.
public static class ScriptSizeEstimator
{
    // A literal costs its own characters plus quoting, a separator, and the newline every few columns.
    private const int PerValueOverhead = 4;

    // INSERT INTO [schema].[table] (…) VALUES, the statement's own weight per row.
    private const int PerRowOverhead = 8;

    public static long EstimateBytes(TableDataChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var columnNames = changes.Columns.Sum(c => c.Length + 4);
        var keyNames = changes.KeyColumns.Sum(c => c.Length + 4);
        var total = 0L;

        foreach (var change in changes.Changes)
        {
            total += change.Classification switch
            {
                // An update repeats every column name as an assignment, so its per-row cost carries the
                // names as well as the values.
                RowClassification.Update => Values(change, changes) + columnNames + keyNames + PerRowOverhead,

                // A delete writes only its key, which is why deletes are left inline even when the rest
                // of the table is staged.
                RowClassification.Delete => Key(change, changes) + keyNames + PerRowOverhead,

                _ => Values(change, changes) + PerRowOverhead
            };
        }

        return total + columnNames + 64;
    }

    private static long Values(DataChange change, TableDataChanges changes) =>
        changes.KeyColumns.Concat(changes.Columns)
            .Sum(c => (change.SourceValues.GetValueOrDefault(c)
                ?? change.KeyValues.GetValueOrDefault(c))?.Length + PerValueOverhead ?? PerValueOverhead);

    private static long Key(DataChange change, TableDataChanges changes) =>
        changes.KeyColumns.Sum(c => change.KeyValues.GetValueOrDefault(c)?.Length + PerValueOverhead ?? PerValueOverhead);
}
