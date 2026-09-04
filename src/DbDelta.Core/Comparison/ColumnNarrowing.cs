namespace DbDelta.Core.Comparison;

// The verdict on one column change. Reason is written for the person about to approve the apply, so it
// names both types and what is at stake rather than saying "may lose data".
public sealed record ColumnNarrowing(NarrowingKind Kind, string? Reason = null)
{
    public static readonly ColumnNarrowing None = new(NarrowingKind.None);

    public bool LosesData => Kind != NarrowingKind.None;
}
