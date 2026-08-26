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

    public double MaxDeleteShare { get; init; } = 0.05;
}
