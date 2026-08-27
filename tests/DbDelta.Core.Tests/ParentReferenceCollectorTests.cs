using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Tests;

public sealed class ParentReferenceCollectorTests
{
    private static readonly TableDefinition Site = Build.Table(
        "Site",
        columns: [Build.Column("SiteId", "INT"), Build.Column("CompanyId", "INT", nullable: true)],
        foreignKeys: [Build.ForeignKey("FK_Site_Company", "CompanyId", "Company")]);

    private static readonly string[] Fetched = ["SiteId", "CompanyId"];

    [Fact]
    public void Rows_being_written_ask_for_the_parents_their_key_points_at()
    {
        var references = ParentReferenceCollector.From(
            Site,
            [Row("1", "10", RowClassification.Insert), Row("2", "11", RowClassification.Insert)],
            Fetched);

        var reference = Assert.Single(references);

        Assert.Equal("FK_Site_Company", reference.ForeignKeyName);
        Assert.Equal("dbo.Company", reference.Parent.QualifiedName);
        Assert.Equal(["Id"], reference.ParentColumns);
        Assert.Equal(["10", "11"], reference.Keys.Select(k => k.Display));
    }

    // SQL Server does not enforce a foreign key when one of its columns is NULL, so a row with a NULL
    // there references nothing and pulling a parent in for it would be inventing a row.
    [Fact]
    public void A_null_foreign_key_value_asks_for_nothing()
    {
        var references = ParentReferenceCollector.From(
            Site,
            [Row("1", null, RowClassification.Insert)],
            Fetched);

        Assert.Empty(references);
    }

    [Fact]
    public void The_same_parent_is_asked_for_once_however_many_children_point_at_it()
    {
        var references = ParentReferenceCollector.From(
            Site,
            [
                Row("1", "10", RowClassification.Insert),
                Row("2", "10", RowClassification.Insert),
                Row("3", "10", RowClassification.Insert)
            ],
            Fetched);

        Assert.Equal(["10"], Assert.Single(references).Keys.Select(k => k.Display));
    }

    // An update that moves a foreign key onto a value the target does not have fails exactly like an
    // insert does, so it is not only the new rows that need their parents present.
    [Fact]
    public void An_update_asks_for_its_parent_too()
    {
        var references = ParentReferenceCollector.From(
            Site,
            [Row("1", "10", RowClassification.Update)],
            Fetched);

        Assert.Equal(["10"], Assert.Single(references).Keys.Select(k => k.Display));
    }

    [Fact]
    public void A_delete_asks_for_nothing_because_it_cannot_break_an_outbound_key()
    {
        var references = ParentReferenceCollector.From(
            Site,
            [Row("1", "10", RowClassification.Delete)],
            Fetched);

        Assert.Empty(references);
    }

    // The plan would otherwise look complete while one constraint went unexamined.
    [Fact]
    public void A_key_whose_columns_were_never_fetched_is_named_rather_than_skipped_quietly()
    {
        Assert.Equal(
            ["FK_Site_Company"],
            ParentReferenceCollector.Unreadable(Site, ["SiteId"]));

        Assert.Empty(ParentReferenceCollector.From(Site, [Row("1", "10", RowClassification.Insert)], ["SiteId"]));
        Assert.Empty(ParentReferenceCollector.Unreadable(Site, Fetched));
    }

    [Fact]
    public void A_composite_key_is_matched_value_by_value()
    {
        var table = Build.Table(
            "Allocation",
            columns: [Build.Column("Year", "INT"), Build.Column("Region", "NVARCHAR", 10)],
            foreignKeys:
            [
                new ForeignKeyDefinition
                {
                    Name = "FK_Allocation_Period",
                    Columns = ["Year", "Region"],
                    ReferencedTable = Build.TableId("Period"),
                    ReferencedColumns = ["Year", "Region"]
                }
            ]);

        var change = new DataChange(
            "k",
            "k",
            RowClassification.Insert,
            new Dictionary<string, string?> { ["Year"] = "2026", ["Region"] = "North" },
            new Dictionary<string, string?>());

        var reference = Assert.Single(
            ParentReferenceCollector.From(table, [change], ["Year", "Region"]));

        Assert.Equal("2026, North", Assert.Single(reference.Keys).Display);
    }

    // Length-prefixed, so two tuples that concatenate to the same text stay apart.
    [Fact]
    public void Two_different_composite_keys_do_not_collapse_into_one()
    {
        var left = new ParentKey(["a", "bc"]);
        var right = new ParentKey(["ab", "c"]);

        Assert.NotEqual(left.Canonical, right.Canonical);
    }

    private static DataChange Row(string id, string? companyId, RowClassification classification) =>
        new(id,
            id,
            classification,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["SiteId"] = id,
                ["CompanyId"] = companyId
            },
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["SiteId"] = id });
}
