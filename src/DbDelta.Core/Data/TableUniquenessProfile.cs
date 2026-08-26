namespace DbDelta.Core.Data;

// One scan answers "which single columns would work as a key", so the user picks from what actually
// works instead of guessing a column at a time.
public sealed class TableUniquenessProfile
{
    public required long RowCount { get; init; }

    public required IReadOnlyList<ColumnUniqueness> Columns { get; init; }

    public bool WasProbed { get; init; } = true;

    public string? Problem { get; init; }

    public IEnumerable<string> UniqueColumns =>
        Columns.Where(c => c.CouldBeKey(RowCount)).Select(c => c.Column);
}
