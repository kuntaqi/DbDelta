namespace DbDelta.Api.Contracts;

public sealed record RequiredRowsDto(
    string Table,
    int RowCount,
    string RequiredBy,
    string ForeignKeyName);
