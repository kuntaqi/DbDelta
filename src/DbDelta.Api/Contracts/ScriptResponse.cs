namespace DbDelta.Api.Contracts;

public sealed record ScriptResponse(
    string Sql,
    int StepCount,
    int ByteSize,
    bool ExceedsReviewableSize,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<string> DeleteWarnings,
    IReadOnlyList<RequiredObjectDto> Required,
    // What the apply will fail on, and what it will simply not contain. Both used to be one list, under
    // a UI sentence that told the reader the script would fail and roll back — true of a prerequisite the
    // target cannot get, false of an object the emitter declined to write, which is declined precisely so
    // the rest stays applicable.
    IReadOnlyList<string> Unsatisfiable,
    IReadOnlyList<string> NotEmitted,
    IReadOnlyList<RequiredRowsDto> RequiredRows,
    IReadOnlyList<ExcludedObjectDto> Excluded,
    IReadOnlyList<StagedTableDto> Staged,
    IReadOnlyList<string> Narrowings);
