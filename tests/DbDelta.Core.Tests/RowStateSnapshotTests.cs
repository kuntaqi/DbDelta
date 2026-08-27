using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Tests;

public sealed class RowStateSnapshotTests
{
    [Fact]
    public void An_unchanged_target_is_not_drift()
    {
        var reviewed = Snapshot(Update("1", "H1"), Update("2", "H2"));
        var now = Snapshot(Update("1", "H1"), Update("2", "H2"));

        Assert.Empty(reviewed.DriftAgainst(now));
    }

    // The case a schema-only check cannot see: the row is still in the plan and still being written, but
    // what is about to be overwritten is no longer what was reviewed.
    [Fact]
    public void A_row_the_plan_would_overwrite_having_moved_is_drift()
    {
        var reviewed = Snapshot(Update("1", "H1"));
        var now = Snapshot(Update("1", "H9"));

        var drift = Assert.Single(reviewed.DriftAgainst(now));

        Assert.Contains("dbo.Company", drift);
        Assert.Contains("1 row(s) the plan would write have changed", drift);
    }

    [Fact]
    public void A_row_that_stopped_differing_is_drift()
    {
        var reviewed = Snapshot(Update("1", "H1"), Update("2", "H2"));
        var now = Snapshot(Update("1", "H1"));

        Assert.Contains(reviewed.DriftAgainst(now), d => d.Contains("no longer differ"));
    }

    // The plan grew after it was read. Nothing about the reviewed rows changed, so only comparing the
    // rows already in it would call this clean — which is exactly how a plan applies more than was read.
    [Fact]
    public void A_row_that_started_differing_after_the_review_is_drift()
    {
        var reviewed = Snapshot(Update("1", "H1"));
        var now = Snapshot(Update("1", "H1"), Update("2", "H2"));

        Assert.Contains(reviewed.DriftAgainst(now), d => d.Contains("now differ that did not"));
    }

    // An insert records null: what has to stay true is that the target does not have the row. Someone
    // inserting it meanwhile means the plan's INSERT would land on top of a row nobody reviewed.
    [Fact]
    public void A_planned_insert_whose_row_appeared_on_the_target_is_drift()
    {
        var reviewed = Snapshot(Insert("7"));
        var now = Snapshot(Update("7", "H7"));

        Assert.Contains(reviewed.DriftAgainst(now), d => d.Contains("have changed on the target"));
    }

    [Fact]
    public void A_table_entering_or_leaving_the_plan_is_drift()
    {
        var company = Snapshot(Update("1", "H1"));
        var withCategory = Snapshot(
            [Table("Company", Update("1", "H1")), Table("Category", Update("9", "H9"))]);

        Assert.Contains(company.DriftAgainst(withCategory), d => d.Contains("has entered the plan"));
        Assert.Contains(withCategory.DriftAgainst(company), d => d.Contains("no longer in the plan"));
    }

    // Rows the plan does not touch are not recorded at all, so a busy table does not make the check
    // useless — only the rows about to be written are held to account.
    [Fact]
    public void Only_the_rows_the_plan_touches_are_recorded()
    {
        var snapshot = Snapshot(Insert("1"), Update("2", "H2"), Delete("3", "H3"));

        Assert.Equal(1, snapshot.TableCount);
        Assert.Equal(3, snapshot.RowCount);
    }

    private static DataChange Insert(string key) =>
        new(key, key, RowClassification.Insert, Empty, Empty);

    private static DataChange Update(string key, string hash) =>
        new(key, key, RowClassification.Update, Empty, Empty, hash);

    private static DataChange Delete(string key, string hash) =>
        new(key, key, RowClassification.Delete, Empty, Empty, hash);

    private static IReadOnlyDictionary<string, string?> Empty => new Dictionary<string, string?>();

    private static RowStateSnapshot Snapshot(params DataChange[] changes) =>
        Snapshot([Table("Company", changes)]);

    private static RowStateSnapshot Snapshot(IReadOnlyList<TableDataChanges> tables) =>
        RowStateSnapshot.From(tables);

    private static TableDataChanges Table(string name, params DataChange[] changes) =>
        new()
        {
            Table = Build.Table(name),
            KeyColumns = ["Id"],
            Columns = ["Name"],
            Changes = changes
        };
}
