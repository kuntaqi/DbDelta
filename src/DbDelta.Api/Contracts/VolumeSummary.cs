namespace DbDelta.Api.Contracts;

public sealed record VolumeSummary(
    long SourceDataBytes,
    long SourceLogBytes,
    long SourceRows,
    long TargetDataBytes,
    long TargetLogBytes,
    long TargetRows,
    IReadOnlyList<TableRow> Tables);
