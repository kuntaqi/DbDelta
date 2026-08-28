using DbDelta.Core.Scripting;

namespace DbDelta.Core.Tests;

public sealed class PlanSizeEstimatorTests
{
    private const long Inline = 8L * 1024 * 1024;
    private const long Reviewable = 100L * 1024 * 1024;

    private static TableSizeInput Table(
        string name = "dbo.T",
        bool scanned = true,
        bool differs = true,
        long? exact = null,
        long sourceRows = 1000,
        long targetRows = 1000,
        long bytesPerRow = 100) =>
        new(name, scanned, differs, exact, sourceRows, targetRows, bytesPerRow);

    private static PlanSizeEstimate Estimate(params TableSizeInput[] tables) =>
        PlanSizeEstimator.Estimate(tables, schemaBytes: 1000, Inline, Reviewable);

    [Fact]
    public void An_empty_plan_is_the_schema_and_nothing_else()
    {
        var estimate = PlanSizeEstimator.Estimate([], 5000, Inline, Reviewable);

        Assert.Equal(5000, estimate.MinBytes);
        Assert.Equal(5000, estimate.MaxBytes);
        Assert.Equal(0, estimate.MinRows);
        Assert.Equal(0, estimate.MaxRows);
        Assert.True(estimate.RowsAreExact);
        Assert.Equal(ReviewableVerdict.Within, estimate.Verdict);
    }

    // The one cheap certainty: a scanned table that does not differ writes nothing.
    [Fact]
    public void A_scanned_table_that_does_not_differ_contributes_nothing()
    {
        var estimate = Estimate(Table(differs: false));

        Assert.Equal(0, estimate.MinDataBytes);
        Assert.Equal(0, estimate.MaxDataBytes);
        Assert.Equal(0, estimate.MaxRows);
        Assert.True(estimate.RowsAreExact);
    }

    // Narrowed to particular rows, so the row count is the pick rather than a guess. The byte figure is
    // still a range, because knowing how many rows move says nothing about how wide they are as SQL — and
    // claiming otherwise is what made a first version of this report a floor above the truth.
    [Fact]
    public void A_table_narrowed_to_particular_rows_has_an_exact_row_count_and_a_ranged_size()
    {
        var estimate = Estimate(Table(exact: 12));

        Assert.Equal(12, estimate.MinRows);
        Assert.Equal(12, estimate.MaxRows);
        Assert.True(estimate.RowsAreExact);
        Assert.True(estimate.MinBytes < estimate.MaxBytes, "the width of a row is not knowable from its count");
    }

    // Scanned, differs, same row counts on both sides: at least one row moved, at most all of them.
    [Fact]
    public void A_differing_table_is_bounded_by_one_row_and_the_whole_table()
    {
        var estimate = Estimate(Table(sourceRows: 500, targetRows: 500));

        Assert.Equal(1, estimate.MinRows);
        Assert.Equal(500, estimate.MaxRows);
        Assert.False(estimate.RowsAreExact);
    }

    // The row counts disagreeing by 300 means at least 300 rows have to be inserted or deleted before the
    // two sides can hold the same number, so the floor is not 1.
    [Fact]
    public void A_row_count_difference_raises_the_floor()
    {
        var estimate = Estimate(Table(sourceRows: 800, targetRows: 500));

        Assert.Equal(300, estimate.MinRows);
        Assert.Equal(800, estimate.MaxRows);
    }

    // Never scanned means nothing is known, including whether it differs at all — so the floor is zero.
    [Fact]
    public void An_unscanned_table_is_bounded_by_zero_and_the_whole_table()
    {
        var estimate = Estimate(Table(scanned: false, sourceRows: 400, targetRows: 400));

        Assert.Equal(0, estimate.MinRows);
        Assert.Equal(400, estimate.MaxRows);
        Assert.Equal(1, estimate.TablesNotScanned);
        Assert.Contains(estimate.Notes, n => n.Contains("have not been scanned", StringComparison.Ordinal));
    }

    // Staging is what keeps the upper bound worth printing: past the threshold a table's rows leave the
    // script, so it cannot contribute more than a fixed amount however many rows it holds.
    [Fact]
    public void A_table_certain_to_stage_contributes_a_fixed_amount_not_its_rows()
    {
        // 10 million rows at 100 bytes is far past the 8 MB threshold even at the floor.
        var estimate = Estimate(Table(sourceRows: 10_000_000, targetRows: 0));

        Assert.True(estimate.MaxDataBytes < 64 * 1024, $"staged table contributed {estimate.MaxDataBytes} bytes");
        Assert.Contains(estimate.Notes, n => n.Contains("certain to take the staged path", StringComparison.Ordinal));

        // The rows are still counted, because the work of moving them is real even when the script is small.
        Assert.Equal(10_000_000, estimate.MaxRows);
    }

    // Straddling the threshold: it might stay inline and it might stage, so the upper bound is the
    // threshold rather than the unbounded row estimate.
    [Fact]
    public void A_table_that_might_stage_is_capped_at_the_threshold()
    {
        // 1 row floor, 200k rows ceiling at 200 bytes = 40 MB ceiling, well past the threshold.
        var estimate = Estimate(Table(sourceRows: 200_000, targetRows: 200_000, bytesPerRow: 200));

        Assert.Equal(Inline, estimate.MaxDataBytes);
        Assert.Contains(estimate.Notes, n => n.Contains("may take the staged path", StringComparison.Ordinal));
    }

    [Fact]
    public void The_schema_half_is_added_to_both_ends()
    {
        var estimate = PlanSizeEstimator.Estimate([Table(differs: false)], 4096, Inline, Reviewable);

        Assert.Equal(4096, estimate.SchemaBytes);
        Assert.Equal(4096, estimate.MinBytes);
        Assert.Equal(4096, estimate.MaxBytes);
        Assert.True(estimate.RowsAreExact);
    }

    [Fact]
    public void Within_the_limit_at_both_ends_reads_as_within()
    {
        Assert.Equal(ReviewableVerdict.Within, Estimate(Table(sourceRows: 10, targetRows: 10)).Verdict);
    }

    // Twenty tables that each might reach the staging threshold add to 160 MB of possible script, which is
    // past a 100 MB limit — but each might also be tiny, so this is "possibly", not "exceeds".
    [Fact]
    public void Many_tables_each_near_the_threshold_can_exceed_the_reviewable_limit()
    {
        var tables = Enumerable.Range(0, 20)
            .Select(i => Table($"dbo.T{i}", sourceRows: 200_000, targetRows: 200_000, bytesPerRow: 200))
            .ToArray();

        var estimate = Estimate(tables);

        Assert.Equal(ReviewableVerdict.Possibly, estimate.Verdict);
        Assert.True(estimate.MaxBytes > Reviewable);
        Assert.True(estimate.MinBytes < Reviewable);
    }

    // A floor past the limit is not a maybe. The floor takes the narrow end of the width band, so this needs
    // to be past the limit even after that halving.
    [Fact]
    public void A_floor_past_the_limit_reads_as_exceeds()
    {
        var estimate = PlanSizeEstimator.Estimate(
            [Table(exact: 20_000_000, bytesPerRow: 100)],
            schemaBytes: 0,
            maxInlineTableBytes: long.MaxValue,
            maxReviewableBytes: Reviewable);

        Assert.Equal(ReviewableVerdict.Exceeds, estimate.Verdict);
    }

    [Fact]
    public void A_zero_row_width_does_not_collapse_the_estimate_to_nothing()
    {
        var estimate = Estimate(Table(exact: 100, bytesPerRow: 0));

        Assert.True(estimate.MinDataBytes > 0, "a row cannot cost nothing to write");
    }

    [Fact]
    public void Every_estimate_says_what_is_measured_and_what_is_not()
    {
        Assert.Contains(
            Estimate(Table()).Notes,
            n => n.Contains("schema half is measured, not estimated", StringComparison.Ordinal));
    }

    [Fact]
    public void Tables_are_counted()
    {
        var estimate = Estimate(Table("dbo.A"), Table("dbo.B"), Table("dbo.C", scanned: false));

        Assert.Equal(3, estimate.TablesCounted);
        Assert.Equal(1, estimate.TablesNotScanned);
    }
}
