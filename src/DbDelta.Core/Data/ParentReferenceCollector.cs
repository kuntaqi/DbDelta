using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Data;

// Reads the parent demand out of rows that are about to be written. Pure: it decides what to look for,
// never looks. Inserts and updates both count — an update that moves a foreign key column onto a value
// the target does not have fails exactly like an insert does. Deletes cannot break an outbound key.
public static class ParentReferenceCollector
{
    public static IReadOnlyList<ParentReference> From(
        TableDefinition table,
        IEnumerable<DataChange> changes,
        IReadOnlyList<string> availableColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(availableColumns);

        var available = new HashSet<string>(availableColumns, StringComparer.OrdinalIgnoreCase);
        var written = changes
            .Where(c => c.Classification is RowClassification.Insert or RowClassification.Update)
            .ToList();

        if (written.Count == 0)
        {
            return [];
        }

        var references = new List<ParentReference>();

        foreach (var fk in Checkable(table, available))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keys = new List<ParentKey>();

            foreach (var change in written)
            {
                var values = fk.Columns
                    .Select(c => change.SourceValues.GetValueOrDefault(c))
                    .ToList();

                var key = new ParentKey(values);

                // SQL Server does not enforce a foreign key when any of its columns is NULL, so a row
                // with a NULL in the key references nothing and demands nothing.
                if (key.HasNull || !seen.Add(key.Canonical))
                {
                    continue;
                }

                keys.Add(key);
            }

            if (keys.Count > 0)
            {
                references.Add(new ParentReference(
                    table.Identity, fk.Name, fk.ReferencedTable, fk.ReferencedColumns, keys));
            }
        }

        return references;
    }

    // A key whose columns were not fetched cannot be read out of the rows, so it is named rather than
    // skipped in silence: the plan would otherwise look complete while one constraint went unexamined.
    public static IReadOnlyList<string> Unreadable(
        TableDefinition table,
        IReadOnlyList<string> availableColumns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(availableColumns);

        var available = new HashSet<string>(availableColumns, StringComparer.OrdinalIgnoreCase);

        return table.ForeignKeys
            .Where(fk => !fk.Columns.All(available.Contains))
            .Select(fk => fk.Name)
            .ToList();
    }

    private static IEnumerable<ForeignKeyDefinition> Checkable(
        TableDefinition table,
        HashSet<string> available) =>
        table.ForeignKeys.Where(fk =>
            fk.Columns.Count > 0
            && fk.Columns.Count == fk.ReferencedColumns.Count
            && fk.Columns.All(available.Contains));
}
