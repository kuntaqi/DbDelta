namespace DbDelta.Api.Contracts;

public sealed record TableScanResponse(
    long DurationMs,
    int Scanned,
    int Differing,
    int NotComparable,
    int Skipped,
    long MaxTableBytes,
    IReadOnlyList<TableScanRow> Tables);
