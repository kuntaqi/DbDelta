namespace DbDelta.Core.Data;

public sealed class DataCompareSettings
{
    public required TableDataMode Mode { get; init; }

    public required IReadOnlyList<string> ComparedColumns { get; init; }

    public IReadOnlyList<ColumnExclusion> ExcludedColumns { get; init; } = [];

    // Not a preference. Under Top N or a filter the row set cannot testify that an absent row was
    // deleted, only that it fell outside the limit.
    public bool SuppressDeletes => Mode is TableDataMode.TopN or TableDataMode.Filter;
}
