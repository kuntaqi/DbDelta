namespace DbDelta.Api.Services;

public sealed class RunLogEntry
{
    public required string Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string Action { get; init; }

    public required string SourceDatabase { get; init; }

    public required string TargetServer { get; init; }

    public required string TargetDatabase { get; init; }

    public required string Outcome { get; init; }

    public required int StepCount { get; init; }

    public long DurationMs { get; init; }

    public string? ServerMessage { get; init; }

    // The script is kept with the run so a failure can be read, corrected and re-run without
    // repeating the comparison that produced it.
    public required string Sql { get; init; }

    public string Route => $"{SourceDatabase} -> {TargetDatabase}";
}
