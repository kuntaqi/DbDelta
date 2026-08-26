using DbDelta.Core.Model;

namespace DbDelta.Core.Scripting;

public sealed class TableDataChanges
{
    public required TableDefinition Table { get; init; }

    public required IReadOnlyList<string> KeyColumns { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<DataChange> Changes { get; init; }

    // Needed for the blast-radius guard: two deletes out of five rows is a different decision from
    // two out of five million.
    public long TargetRowCount { get; init; }

    public bool HasIdentityInsert =>
        Table.Columns.Any(c => c.Identity is not null && Columns.Concat(KeyColumns)
            .Contains(c.Name, StringComparer.OrdinalIgnoreCase));
}
