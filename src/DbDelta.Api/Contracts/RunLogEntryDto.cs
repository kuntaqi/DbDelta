namespace DbDelta.Api.Contracts;

public sealed record RunLogEntryDto(
    string Id,
    DateTimeOffset At,
    string Action,
    string Route,
    string Outcome,
    int StepCount,
    long DurationMs,
    string? ServerMessage);
