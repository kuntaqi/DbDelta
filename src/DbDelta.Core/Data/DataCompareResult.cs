namespace DbDelta.Core.Data;

public sealed class DataCompareResult
{
    public required IReadOnlyList<RowDifference> Differences { get; init; }

    public required int SameCount { get; init; }

    public required bool DeletesSuppressed { get; init; }

    public required IReadOnlyList<ColumnExclusion> ExcludedColumns { get; init; }

    public required IReadOnlyList<string> ComparedColumns { get; init; }

    public int InsertCount => Count(RowClassification.Insert);

    public int UpdateCount => Count(RowClassification.Update);

    public int DeleteCount => Count(RowClassification.Delete);

    public int ChangedCount => Differences.Count;

    public bool HasChanges => Differences.Count > 0;

    public IEnumerable<RowDifference> Of(RowClassification classification) =>
        Differences.Where(d => d.Classification == classification);

    private int Count(RowClassification classification) =>
        Differences.Count(d => d.Classification == classification);
}
