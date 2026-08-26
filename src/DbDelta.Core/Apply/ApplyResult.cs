namespace DbDelta.Core.Apply;

public sealed class ApplyResult
{
    public required ApplyOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public int StepCount { get; init; }

    public long DurationMs { get; init; }

    public string? ServerMessage { get; init; }

    public int? ErrorNumber { get; init; }

    public IReadOnlyList<string> Blockers { get; init; } = [];

    public bool Succeeded => Outcome == ApplyOutcome.Committed;

    public static ApplyResult Blocked(string message, IReadOnlyList<string> blockers) =>
        new() { Outcome = ApplyOutcome.Blocked, Message = message, Blockers = blockers };
}
