namespace DbDelta.Api.Services;

public sealed class SafetyOptions
{
    public const string SectionName = "Safety";

    // Servers here can be compared and scripted but never applied to. The guard is a hard block, not
    // a warning, because the cost of a mistaken target is not symmetric.
    public IReadOnlyList<string> ReadOnlyServers { get; init; } = [];

    public IReadOnlyDictionary<string, string[]> Environments { get; init; } =
        new Dictionary<string, string[]>();

    public int MaxReviewableScriptBytes { get; init; } = 100 * 1024 * 1024;

    // Tables larger than this are skipped by a full-database scan and said to be skipped. Scanning a
    // 21 GB table to learn whether it differs costs minutes; the user can raise this deliberately.
    public long MaxScanTableBytes { get; init; } = 200L * 1024 * 1024;

    public double MaxDeleteShare { get; init; } = 0.05;

    // Parent closure follows keys, and a seed into a table with a wide fan-in can follow a lot of them.
    // Past this it stops and says what it stopped short of, because a plan that quietly grew by 80,000
    // rows is not a plan anybody reviewed.
    public int MaxClosureRows { get; init; } = 5000;

    // Where one table stops being written as literal SQL. Past this its rows go over the wire into a
    // staging table instead, which keeps the script the same size whether the table has 800 rows or
    // 800,000 — the script is the reviewable artifact, so its size is the thing being protected.
    public long MaxInlineTableBytes { get; init; } = 8L * 1024 * 1024;
}
