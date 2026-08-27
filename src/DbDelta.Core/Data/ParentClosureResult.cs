using DbDelta.Core.Scripting;

namespace DbDelta.Core.Data;

public sealed class ParentClosureResult
{
    // The plan after expansion: the tables that were picked, with any parent rows merged in, plus any
    // parent table that was not picked at all.
    public required IReadOnlyList<TableDataChanges> Tables { get; init; }

    public required IReadOnlyList<RequiredRows> Added { get; init; }

    // Foreign keys that could not be followed, or were followed to a row the source does not hold.
    // Each one is a statement the apply may fail on, so none of them is dropped quietly.
    public required IReadOnlyList<string> Warnings { get; init; }
}
