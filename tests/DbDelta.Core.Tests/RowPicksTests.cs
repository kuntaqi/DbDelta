using DbDelta.Core.Data;

namespace DbDelta.Core.Tests;

public sealed class RowPicksTests
{
    private static readonly IReadOnlyList<RowDifference> Differences =
    [
        new("1", RowClassification.Insert),
        new("2", RowClassification.Update, "abc"),
        new("3", RowClassification.Delete, "def")
    ];

    private static HashSet<string> Picked(params string[] keys) => new(keys, StringComparer.Ordinal);

    // No picks is the whole table, which is what selecting a table has always meant.
    [Fact]
    public void A_null_pick_set_keeps_everything_and_is_not_a_narrowing()
    {
        var resolution = RowPicks.Apply(Differences, null);

        Assert.Equal(3, resolution.Kept.Count);
        Assert.False(resolution.IsNarrowed);
        Assert.Empty(resolution.Unmatched);
    }

    // And empty is not the same as absent. Unticking the last row leaves a table narrowed to nothing;
    // reading that as "everything" would write the whole table off the back of a box being cleared.
    [Fact]
    public void An_empty_pick_set_keeps_nothing_rather_than_everything()
    {
        var resolution = RowPicks.Apply(Differences, Picked());

        Assert.Empty(resolution.Kept);
        Assert.True(resolution.IsNarrowed);
    }

    [Fact]
    public void Only_the_picked_rows_are_kept()
    {
        var resolution = RowPicks.Apply(Differences, Picked("1", "3"));

        Assert.Equal(["1", "3"], resolution.Kept.Select(d => d.Key));
        Assert.True(resolution.IsNarrowed);
    }

    [Fact]
    public void A_delete_can_be_picked_like_any_other_row()
    {
        var resolution = RowPicks.Apply(Differences, Picked("3"));

        var kept = Assert.Single(resolution.Kept);
        Assert.Equal(RowClassification.Delete, kept.Classification);
    }

    // The case that would otherwise be silent. Picks are made against one comparison and the script is
    // built from another, so a picked row can be gone: fixed, deleted, or out of a limited window.
    [Fact]
    public void A_pick_that_no_longer_matches_anything_is_reported_rather_than_dropped()
    {
        var resolution = RowPicks.Apply(Differences, Picked("2", "999"));

        Assert.Equal(["2"], resolution.Kept.Select(d => d.Key));
        Assert.Equal(["999"], resolution.Unmatched);
    }

    [Fact]
    public void The_available_count_is_what_the_table_offered_not_what_was_kept()
    {
        var resolution = RowPicks.Apply(Differences, Picked("1"));

        Assert.Single(resolution.Kept);
        Assert.Equal(3, resolution.AvailableCount);
    }

    // Keys are compared exactly. They are built by the digest expression, not typed by anyone, and a
    // case-insensitive match here would silently merge two distinct rows.
    [Fact]
    public void Keys_match_ordinally()
    {
        var differences = new List<RowDifference> { new("ABC", RowClassification.Update) };

        var resolution = RowPicks.Apply(differences, Picked("abc"));

        Assert.Empty(resolution.Kept);
        Assert.Equal(["abc"], resolution.Unmatched);
    }

    [Fact]
    public void Unmatched_picks_come_back_in_a_stable_order()
    {
        var resolution = RowPicks.Apply(Differences, Picked("zz", "aa", "mm"));

        Assert.Equal(["aa", "mm", "zz"], resolution.Unmatched);
    }
}
