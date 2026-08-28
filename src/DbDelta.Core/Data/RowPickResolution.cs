namespace DbDelta.Core.Data;

public sealed class RowPickResolution
{
    public required IReadOnlyList<RowDifference> Kept { get; init; }

    // Picked rows the comparison no longer offers. Reported rather than dropped: between picking a row
    // and building a script the row can be fixed, deleted, or fall outside a limited window, and a plan
    // that quietly shrinks is a plan nobody reviewed.
    public required IReadOnlyList<string> Unmatched { get; init; }

    // What the table had to offer, so the plan can say "3 of 340" rather than "3".
    public required int AvailableCount { get; init; }

    public required bool IsNarrowed { get; init; }
}
