namespace DbDelta.Core.Comparison;

public sealed record PropertyDiff(string Property, string? Source, string? Target)
{
    public override string ToString() => $"{Property}: {Source ?? "<none>"} -> {Target ?? "<none>"}";
}
