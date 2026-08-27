namespace DbDelta.Api.Contracts;

public sealed record StagedTableDto(
    string Table,
    int RowCount,
    string DataFileName);
