using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Api.Tests;

// dbo.Contact -> dbo.Company -> dbo.Category, so focusing on Company at depth 1 has exactly one parent
// and exactly one child. That is the smallest shape where a direction filter means anything.
public sealed class FkMapServiceTests
{
    private static CompareSession Session()
    {
        var tables = new List<TableDefinition>
        {
            Table("Category"),
            Table("Company", ("FK_Company_Category", "CategoryId", "Category")),
            Table("Contact", ("FK_Contact_Company", "CompanyId", "Company"))
        };

        var schema = new DatabaseSchema { DatabaseName = "Src", Tables = tables };
        var target = new DatabaseSchema { DatabaseName = "Tgt", Tables = tables };

        return new CompareSession
        {
            Id = "s",
            Source = schema,
            Target = target,
            Diff = new SchemaComparer().Compare(schema, target),
            SourceServer = "SRC",
            TargetServer = "TGT",
            SourceConnectionString = "src",
            TargetConnectionString = "tgt",
            DurationMs = 1
        };
    }

    private static TableDefinition Table(string name, params (string Name, string Column, string Parent)[] keys) =>
        new()
        {
            Identity = new ObjectIdentity(ObjectType.Table, "dbo", name),
            Columns =
            [
                new ColumnDefinition { Name = "Id", DataType = new DataTypeSpec("INT"), OrdinalPosition = 1 },
                new ColumnDefinition { Name = "CategoryId", DataType = new DataTypeSpec("INT"), OrdinalPosition = 2 },
                new ColumnDefinition { Name = "CompanyId", DataType = new DataTypeSpec("INT"), OrdinalPosition = 3 }
            ],
            ForeignKeys = keys.Select(k => new ForeignKeyDefinition
            {
                Name = k.Name,
                Columns = [k.Column],
                ReferencedTable = new ObjectIdentity(ObjectType.Table, "dbo", k.Parent),
                ReferencedColumns = ["Id"]
            }).ToList()
        };

    private static FkMapResponse Map(FkDirection direction) =>
        new FkMapService().Build(Session(), "dbo.Company", 1, direction);

    [Fact]
    public void Both_directions_draw_the_parent_and_the_child()
    {
        var map = Map(FkDirection.Both);

        Assert.Equal("Both", map.Direction);
        Assert.Equal(
            ["dbo.Category", "dbo.Company", "dbo.Contact"],
            map.Nodes.Select(n => n.QualifiedName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Parents_only_leaves_the_child_out_of_the_drawing()
    {
        var map = Map(FkDirection.Parents);

        Assert.Equal(
            ["dbo.Category", "dbo.Company"],
            map.Nodes.Select(n => n.QualifiedName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(map.Edges, e => e.From == "dbo.Contact");
    }

    [Fact]
    public void Children_only_leaves_the_parent_out_of_the_drawing()
    {
        var map = Map(FkDirection.Children);

        Assert.Equal(
            ["dbo.Company", "dbo.Contact"],
            map.Nodes.Select(n => n.QualifiedName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(map.Edges, e => e.To == "dbo.Category");
    }

    // The filter is about what fits on screen. What the plan has to survive is not a viewing preference, so
    // hiding a direction must not also hide the count of what is over there.
    [Fact]
    public void What_is_hidden_is_still_counted_in_the_notes()
    {
        Assert.Contains(
            Map(FkDirection.Parents).Notes,
            n => n.Contains("1 child table(s)") && n.Contains("hidden by the direction filter"));

        Assert.Contains(
            Map(FkDirection.Children).Notes,
            n => n.Contains("1 parent table(s)") && n.Contains("hidden by the direction filter"));
    }

    [Fact]
    public void With_both_directions_shown_there_is_nothing_to_report_as_hidden()
    {
        Assert.DoesNotContain(Map(FkDirection.Both).Notes, n => n.Contains("hidden by the direction filter"));
    }

    // The child count note is plan information, not a description of the picture, so it survives a filter
    // that stops the child being drawn.
    [Fact]
    public void The_seeding_warning_survives_hiding_the_children_it_is_about()
    {
        Assert.Contains(
            Map(FkDirection.Parents).Notes,
            n => n.Contains("1 table(s) reference Company"));
    }
}
