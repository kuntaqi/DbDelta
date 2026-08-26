using DbDelta.Core.Data;

namespace DbDelta.Core.Tests;

// What makes a column usable as a key when nothing is declared. The rules are narrow on purpose: a
// column that is unique only by coincidence today would silently start merging rows tomorrow, so the
// only thing this can honestly say is "unique across every row that exists right now".
public sealed class ColumnUniquenessTests
{
    [Fact]
    public void A_column_with_one_distinct_value_per_row_can_be_a_key()
    {
        Assert.True(new ColumnUniqueness("Ref", 40, 0).CouldBeKey(40));
    }

    [Fact]
    public void A_repeated_value_cannot_be_a_key()
    {
        Assert.False(new ColumnUniqueness("Area", 39, 0).CouldBeKey(40));
    }

    [Fact]
    public void A_null_disqualifies_a_column_even_when_the_rest_is_distinct()
    {
        // The NULL row is the one that cannot be addressed: it never equals anything, so the merge join
        // would drop it on both sides and report a match that was never checked.
        Assert.False(new ColumnUniqueness("Operator", 40, 1).CouldBeKey(40));
    }

    [Fact]
    public void An_empty_table_offers_no_key()
    {
        // Every column is trivially "distinct per row" at zero rows. Accepting that would hand back a
        // key chosen by an empty table and then apply it to a full one.
        Assert.False(new ColumnUniqueness("Ref", 0, 0).CouldBeKey(0));
    }

    [Fact]
    public void The_profile_names_only_the_columns_that_would_work()
    {
        var profile = new TableUniquenessProfile
        {
            RowCount = 4,
            Columns =
            [
                new ColumnUniqueness("EventId", 4, 0),
                new ColumnUniqueness("Area", 2, 0),
                new ColumnUniqueness("Operator", 4, 1),
                new ColumnUniqueness("Payload", 4, 0)
            ]
        };

        Assert.Equal(["EventId", "Payload"], profile.UniqueColumns);
    }

    [Fact]
    public void A_profile_that_could_not_be_taken_recommends_nothing()
    {
        var profile = new TableUniquenessProfile
        {
            RowCount = 0,
            Columns = [],
            WasProbed = false,
            Problem = "No column in this table can be tested for uniqueness."
        };

        Assert.Empty(profile.UniqueColumns);
    }
}
