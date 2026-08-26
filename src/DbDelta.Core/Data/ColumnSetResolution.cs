namespace DbDelta.Core.Data;

public sealed class ColumnSetResolution
{
    public required IReadOnlyList<string> ComparedColumns { get; init; }

    public required IReadOnlyList<ColumnExclusion> ExcludedColumns { get; init; }

    public required int TotalSourceColumns { get; init; }

    public bool CanCompare => ComparedColumns.Count > 0;

    public string Summary => $"{ComparedColumns.Count} of {TotalSourceColumns} columns";
}
