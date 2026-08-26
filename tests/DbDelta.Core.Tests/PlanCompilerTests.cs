using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.Core.Tests;

public sealed class PlanCompilerTests
{
    private static readonly ObjectIdentity Company = Build.TableId("Company");
    private static readonly ObjectIdentity Category = Build.TableId("Category");

    private static ChangeUnit Row(ObjectIdentity table, string key, ChangeUnitKind kind = ChangeUnitKind.InsertRow) =>
        new()
        {
            Id = new ChangeUnitId(table, kind, key),
            Description = $"{kind} {table.QualifiedName}[{key}]"
        };

    private static ChangeUnit Schema(ObjectIdentity table, ChangeUnitKind kind = ChangeUnitKind.AlterObject) =>
        new()
        {
            Id = new ChangeUnitId(table, kind),
            Description = $"{kind} {table.QualifiedName}"
        };

    [Fact]
    public void Row_selections_collapse_under_a_table_selection()
    {
        var selections = new[]
        {
            PlanSelection.Row(Company, "1"),
            PlanSelection.Row(Company, "2"),
            PlanSelection.Table(Company)
        };

        var normalized = PlanNormalizer.Normalize(selections);

        var only = Assert.Single(normalized);
        Assert.Equal(SelectionScope.Table, only.Scope);
    }

    [Fact]
    public void Everything_collapses_under_a_database_selection()
    {
        var selections = new[]
        {
            PlanSelection.Row(Company, "1"),
            PlanSelection.Table(Category),
            PlanSelection.Database()
        };

        var normalized = PlanNormalizer.Normalize(selections);

        var only = Assert.Single(normalized);
        Assert.Equal(SelectionScope.Database, only.Scope);
    }

    [Fact]
    public void Row_selections_on_other_tables_survive_a_table_selection()
    {
        var selections = new[]
        {
            PlanSelection.Row(Category, "9"),
            PlanSelection.Table(Company)
        };

        var normalized = PlanNormalizer.Normalize(selections);

        Assert.Equal(2, normalized.Count);
    }

    [Fact]
    public void Overlapping_scopes_emit_each_change_exactly_once()
    {
        var available = new[]
        {
            Row(Company, "1"),
            Row(Company, "2"),
            Schema(Company)
        };

        // The same work picked three different ways: one row, the whole table, the whole database.
        var plan = PlanCompiler.Compile(
            [PlanSelection.Row(Company, "1"), PlanSelection.Table(Company), PlanSelection.Database()],
            available);

        Assert.Equal(3, plan.Count);
        Assert.Equal(3, plan.Units.Select(u => u.Id).Distinct().Count());
    }

    [Fact]
    public void A_repeated_change_unit_in_the_source_list_is_deduplicated()
    {
        var duplicate = Row(Company, "1");
        var plan = PlanCompiler.Compile([PlanSelection.Table(Company)], [duplicate, duplicate]);

        Assert.Equal(1, plan.Count);
    }

    [Fact]
    public void Only_covered_units_are_compiled()
    {
        var available = new[] { Row(Company, "1"), Row(Category, "9") };

        var plan = PlanCompiler.Compile([PlanSelection.Table(Company)], available);

        var unit = Assert.Single(plan.Units);
        Assert.Equal(Company, unit.Id.Object);
    }

    [Fact]
    public void A_row_selection_covers_only_that_row()
    {
        var available = new[] { Row(Company, "1"), Row(Company, "2") };

        var plan = PlanCompiler.Compile([PlanSelection.Row(Company, "2")], available);

        var unit = Assert.Single(plan.Units);
        Assert.Equal("2", unit.Id.RowKey);
    }

    [Fact]
    public void Exclusions_survive_escalation_to_the_whole_database()
    {
        var available = new[] { Row(Company, "1"), Row(Company, "2"), Schema(Company, ChangeUnitKind.DropObject) };
        var exclusions = new[]
        {
            new Exclusion(new ChangeUnitId(Company, ChangeUnitKind.DropObject), "left out deliberately")
        };

        var plan = PlanCompiler.Compile([PlanSelection.Database()], available, exclusions);

        Assert.Equal(2, plan.Count);
        Assert.DoesNotContain(plan.Units, u => u.Id.Kind == ChangeUnitKind.DropObject);
        Assert.Single(plan.AppliedExclusions);
    }

    [Fact]
    public void An_exclusion_that_never_matched_is_not_reported_as_applied()
    {
        var available = new[] { Row(Company, "1") };
        var exclusions = new[] { new Exclusion(new ChangeUnitId(Category, ChangeUnitKind.DropObject), "stale") };

        var plan = PlanCompiler.Compile([PlanSelection.Database()], available, exclusions);

        Assert.Equal(1, plan.Count);
        Assert.Empty(plan.AppliedExclusions);
    }

    [Fact]
    public void An_exclusion_is_bound_to_a_change_not_to_the_object()
    {
        // Excluding the DROP must not also suppress a later ALTER of the same table.
        var available = new[] { Schema(Company, ChangeUnitKind.DropObject), Schema(Company, ChangeUnitKind.AlterObject) };
        var exclusions = new[] { new Exclusion(new ChangeUnitId(Company, ChangeUnitKind.DropObject), "no drops") };

        var plan = PlanCompiler.Compile([PlanSelection.Database()], available, exclusions);

        var unit = Assert.Single(plan.Units);
        Assert.Equal(ChangeUnitKind.AlterObject, unit.Id.Kind);
    }

    [Fact]
    public void Schema_and_row_changes_are_separable_on_the_compiled_plan()
    {
        var available = new[] { Schema(Company), Row(Company, "1"), Row(Company, "2") };

        var plan = PlanCompiler.Compile([PlanSelection.Database()], available);

        Assert.Single(plan.SchemaChanges);
        Assert.Equal(2, plan.RowChanges.Count());
    }
}
