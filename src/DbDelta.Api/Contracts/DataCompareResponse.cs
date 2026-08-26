namespace DbDelta.Api.Contracts;

public sealed record DataCompareResponse(
    string Table,
    string Mode,
    bool DeletesSuppressed,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> ComparedColumns,
    IReadOnlyList<CellDiff> ExcludedColumns,
    int InsertCount,
    int UpdateCount,
    int DeleteCount,
    int SameCount,
    long TransferBytesEstimate,
    long FootprintBytes,
    IReadOnlyList<RowDiffDto> Rows,
    string? Warning);
