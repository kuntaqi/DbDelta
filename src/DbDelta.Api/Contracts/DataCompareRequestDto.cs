namespace DbDelta.Api.Contracts;

public sealed record DataCompareRequestDto(
    string Table,
    string Mode,
    int TopCount,
    string? Filter);
