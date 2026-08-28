using DbDelta.Core.Model;

namespace DbDelta.Core.Instances;

public sealed record DatabasePair(DatabaseSummary? Source, DatabaseSummary? Target, PairKind Kind)
{
    // One of the two is always there, so this never returns null in practice — a pair with neither side
    // is not something the matcher can produce.
    public string Name => Source?.Name ?? Target?.Name ?? string.Empty;

    public bool OnBothSides => Source is not null && Target is not null;

    // Both sides have to be reachable before anything can be read from them, and a database that is
    // offline or closed to this login is neither a difference nor a match — it is unknown.
    public bool CanBeCompared =>
        OnBothSides
        && Source!.Accessible && Target!.Accessible
        && string.Equals(Source.State, "ONLINE", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Target.State, "ONLINE", StringComparison.OrdinalIgnoreCase);
}
