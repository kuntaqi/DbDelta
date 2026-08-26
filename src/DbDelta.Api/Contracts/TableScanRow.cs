namespace DbDelta.Api.Contracts;

// The scan screens, it does not classify. It can say a table differs and by how many rows overall; it
// cannot say which rows, and it deliberately does not pretend to.
public sealed record TableScanRow(
    string Table,
    bool Comparable,
    string? Reason,
    bool Differs,
    long SourceRows,
    long TargetRows)
{
    public long RowDelta => SourceRows - TargetRows;
}
