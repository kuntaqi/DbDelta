using DbDelta.Api.Contracts;
using DbDelta.Api.Services;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.Api.Tests;

// The cart's rules, exercised without a database: what a tick means under each scope, and what a refusal
// survives. The interesting half is the second one — an exclusion only earns its keep against a selection
// nobody enumerated.
public sealed class SchemaSelectionServiceTests
{
    // Source-only Site (a create), differing Company (an alter), target-only SegmentLegacy (a drop).
    private static CompareSession Session()
    {
        var source = new DatabaseSchema
        {
            DatabaseName = "Src",
            Tables =
            [
                Table("Company", "CompanyName", "NVARCHAR", 200),
                Table("Site"),
                Table("Category")
            ]
        };

        var target = new DatabaseSchema
        {
            DatabaseName = "Tgt",
            Tables =
            [
                Table("Company", "CompanyName", "NVARCHAR", 100),
                Table("SegmentLegacy"),
                Table("Category")
            ]
        };

        return new CompareSession
        {
            Id = "s",
            Source = source,
            Target = target,
            Diff = new SchemaComparer().Compare(source, target),
            SourceServer = "SRC",
            TargetServer = "TGT",
            SourceConnectionString = "src",
            TargetConnectionString = "tgt",
            DurationMs = 1
        };
    }

    private static TableDefinition Table(
        string name,
        string? extraColumn = null,
        string type = "INT",
        int? length = null)
    {
        var columns = new List<ColumnDefinition>
        {
            new() { Name = "Id", DataType = new DataTypeSpec("INT", null), OrdinalPosition = 1 }
        };

        if (extraColumn is not null)
        {
            columns.Add(new ColumnDefinition
            {
                Name = extraColumn,
                DataType = new DataTypeSpec(type, length),
                OrdinalPosition = 2
            });
        }

        return new TableDefinition
        {
            Identity = new ObjectIdentity(ObjectType.Table, "dbo", name),
            Columns = columns
        };
    }

    private static string Id(string name) => $"table:dbo.{name}".ToLowerInvariant();

    [Fact]
    public void Nothing_is_in_the_plan_until_something_is_picked()
    {
        var response = SchemaSelectionService.Describe(Session());

        Assert.Empty(response.Selected);
        Assert.Equal("Picked", response.Scope);
        Assert.Empty(response.Excluded);
    }

    [Fact]
    public void A_tick_under_picked_scope_puts_one_object_in_and_no_more()
    {
        var session = Session();

        var response = SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("Site"), true));

        Assert.Equal([Id("Site")], response.Selected);
        Assert.Empty(response.Excluded);
    }

    [Fact]
    public void Escalating_to_the_whole_database_covers_every_differing_object()
    {
        var session = Session();

        var response = SchemaSelectionService.SetScope(session, "Database");

        Assert.Equal("Database", response.Scope);
        Assert.Equal(
            [Id("Company"), Id("SegmentLegacy"), Id("Site")],
            response.Selected.Order(StringComparer.Ordinal));
    }

    // The point of the whole mechanism. Under a database-wide plan there is no list to take an object off,
    // so declining one has to be recorded as a decision.
    [Fact]
    public void Unticking_under_the_whole_database_records_an_exclusion_rather_than_a_removal()
    {
        var session = Session();
        SchemaSelectionService.SetScope(session, "Database");

        var response = SchemaSelectionService.Select(
            session, new SchemaSelectionRequest(Id("SegmentLegacy"), false));

        Assert.DoesNotContain(Id("SegmentLegacy"), response.Selected);

        var excluded = Assert.Single(response.Excluded);
        Assert.Equal("dbo.SegmentLegacy", excluded.QualifiedName);
        Assert.Equal("DropObject", excluded.Kind);
    }

    [Fact]
    public void An_exclusion_survives_a_second_escalation_of_the_same_scope()
    {
        var session = Session();
        SchemaSelectionService.SetScope(session, "Database");
        SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("SegmentLegacy"), false));

        // Asking for the whole database again is the move that used to reinstate everything.
        var again = SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("Site"), true));

        Assert.DoesNotContain(Id("SegmentLegacy"), again.Selected);
        Assert.Single(again.Excluded);
    }

    [Fact]
    public void Ticking_an_excluded_object_revokes_the_exclusion()
    {
        var session = Session();
        SchemaSelectionService.SetScope(session, "Database");
        SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("SegmentLegacy"), false));

        var response = SchemaSelectionService.Select(
            session, new SchemaSelectionRequest(Id("SegmentLegacy"), true));

        Assert.Contains(Id("SegmentLegacy"), response.Selected);
        Assert.Empty(response.Excluded);
    }

    // Under a list, an object left out is simply not on it — recording a refusal there would be noise.
    [Fact]
    public void Unticking_under_picked_scope_is_a_removal_not_an_exclusion()
    {
        var session = Session();
        SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("Site"), true));

        var response = SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("Site"), false));

        Assert.Empty(response.Selected);
        Assert.Empty(response.Excluded);
    }

    // Narrowing keeps the result and drops the machinery: what was in the plan stays in it as plain picks.
    [Fact]
    public void Narrowing_to_picked_items_keeps_what_was_in_the_plan_and_forgets_the_exclusions()
    {
        var session = Session();
        SchemaSelectionService.SetScope(session, "Database");
        SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("SegmentLegacy"), false));

        var response = SchemaSelectionService.SetScope(session, "Picked");

        Assert.Equal("Picked", response.Scope);
        Assert.Equal([Id("Company"), Id("Site")], response.Selected.Order(StringComparer.Ordinal));
        Assert.Empty(response.Excluded);
    }

    [Fact]
    public void Clearing_empties_both_the_picks_and_the_refusals()
    {
        var session = Session();
        SchemaSelectionService.SetScope(session, "Database");
        SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("SegmentLegacy"), false));

        var response = SchemaSelectionService.Clear(session);

        Assert.Empty(response.Selected);
        Assert.Empty(response.Excluded);
        Assert.Equal("Picked", response.Scope);
    }

    [Fact]
    public void An_object_with_nothing_to_sync_cannot_be_picked()
    {
        var session = Session();

        Assert.Throws<InvalidOperationException>(() =>
            SchemaSelectionService.Select(session, new SchemaSelectionRequest(Id("Category"), true)));
    }

    [Fact]
    public void An_id_from_another_comparison_is_refused_by_name()
    {
        var session = Session();

        var error = Assert.Throws<InvalidOperationException>(() =>
            SchemaSelectionService.Select(session, new SchemaSelectionRequest("table:dbo.nosuch", true)));

        Assert.Contains("not part of this comparison", error.Message);
    }
}
