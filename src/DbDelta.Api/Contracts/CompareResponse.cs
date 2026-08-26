namespace DbDelta.Api.Contracts;

public sealed record CompareResponse(
    string Id,
    string SourceDatabase,
    string TargetDatabase,
    EnvironmentClass SourceEnvironment,
    EnvironmentClass TargetEnvironment,
    bool TargetReadOnly,
    long DurationMs,
    bool TargetIsEmpty,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<TypeCount> Counts,
    IReadOnlyList<ObjectSummary> Objects);
