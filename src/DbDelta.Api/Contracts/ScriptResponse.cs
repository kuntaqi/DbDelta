namespace DbDelta.Api.Contracts;

public sealed record ScriptResponse(
    string Sql,
    int StepCount,
    int ByteSize,
    bool ExceedsReviewableSize,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<string> DeleteWarnings,
    IReadOnlyList<RequiredObjectDto> Required,
    IReadOnlyList<string> Unsatisfiable,
    IReadOnlyList<RequiredRowsDto> RequiredRows,
    IReadOnlyList<ExcludedObjectDto> Excluded);
