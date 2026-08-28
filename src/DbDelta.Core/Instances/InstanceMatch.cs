namespace DbDelta.Core.Instances;

public sealed class InstanceMatch
{
    public required IReadOnlyList<DatabasePair> Pairs { get; init; }

    // Declared pairings that could not be honoured, and why. Reported rather than dropped: a pairing that
    // names a database nobody has is a typo, and silently ignoring it makes the database look absent.
    public required IReadOnlyList<string> Problems { get; init; }

    public int OnBothSides => Pairs.Count(p => p.OnBothSides);

    public int SourceOnly => Pairs.Count(p => p.Kind == PairKind.SourceOnly);

    public int TargetOnly => Pairs.Count(p => p.Kind == PairKind.TargetOnly);
}
