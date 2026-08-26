namespace DbDelta.Api.Contracts;

public sealed record RowDiffDto(
    string Key,
    string Display,
    string Classification,
    IReadOnlyList<CellDiff> Changes,
    int UnchangedColumns);
