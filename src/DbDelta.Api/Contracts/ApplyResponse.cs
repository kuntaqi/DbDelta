namespace DbDelta.Api.Contracts;

public sealed record ApplyResponse(
    string Outcome,
    string Message,
    int StepCount,
    long DurationMs,
    string? ServerMessage,
    int? ErrorNumber,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> DestructiveSteps);
