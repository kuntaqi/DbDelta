namespace DbDelta.Api.Contracts;

public sealed record ScriptResponse(
    string Sql,
    int StepCount,
    int ByteSize,
    bool ExceedsReviewableSize,
    IReadOnlyList<StepDto> Steps);
