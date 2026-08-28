namespace DbDelta.Api.Contracts;

public sealed record InstanceComparisonResponse(
    string SourceServer,
    string TargetServer,
    EnvironmentClass SourceEnvironment,
    EnvironmentClass TargetEnvironment,
    bool TargetReadOnly,
    long DurationMs,
    int OnBothSides,
    int SourceOnly,
    int TargetOnly,
    bool Described,
    IReadOnlyList<DatabasePairDto> Pairs,
    IReadOnlyList<string> Warnings);
