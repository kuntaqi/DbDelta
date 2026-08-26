using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.SqlServer.Tests;

// The full loop: read two real databases from LocalDB, diff them, and compile a plan. Everything
// above this is unit-tested in isolation; this is the only place the pieces meet a real server.
[Collection(nameof(LocalDbCollection))]
public sealed class EndToEndCompareTests
{
    private readonly LocalDbFixture _fixture;

    public EndToEndCompareTests(LocalDbFixture fixture) => _fixture = fixture;

    private async Task<SchemaDiff> CompareAsync()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var source = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
        var target = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.TargetDatabase)).ReadAsync();

        return new SchemaComparer().Compare(source, target);
    }

    [SkippableFact]
    public async Task The_widened_column_is_detected_across_two_real_databases()
    {
        var diff = await CompareAsync();

        var company = diff.Find(new ObjectIdentity(ObjectType.Table, "dbo", "Company"));
        Assert.NotNull(company);
        Assert.Equal(DiffKind.Different, company.Kind);

        var segment = company.DifferingChildren.Single(c => c.Identity.Name == "Segment");
        var dataType = segment.Properties.Single(p => p.Property == "DataType");
        Assert.Equal("NVARCHAR(40)", dataType.Source);
        Assert.Equal("NVARCHAR(20)", dataType.Target);
    }

    [SkippableFact]
    public async Task The_missing_column_and_index_are_reported_as_source_only()
    {
        var diff = await CompareAsync();

        var company = diff.Find(new ObjectIdentity(ObjectType.Table, "dbo", "Company"));
        Assert.NotNull(company);

        var rating = company.DifferingChildren.Single(c => c.Identity.Name == "RatingBand");
        Assert.Equal(DiffKind.SourceOnly, rating.Kind);
        Assert.Equal(ObjectType.Column, rating.Identity.Type);

        var index = company.DifferingChildren.Single(c => c.Identity.Name == "IX_Company_Rating");
        Assert.Equal(DiffKind.SourceOnly, index.Kind);
        Assert.Equal(ObjectType.Index, index.Identity.Type);
    }

    [SkippableFact]
    public async Task The_check_constraint_missing_on_target_is_reported()
    {
        var diff = await CompareAsync();

        var company = diff.Find(new ObjectIdentity(ObjectType.Table, "dbo", "Company"));
        Assert.NotNull(company);

        var check = company.DifferingChildren.Single(c => c.Identity.Type == ObjectType.CheckConstraint);
        Assert.Equal(DiffKind.SourceOnly, check.Kind);
    }

    [SkippableFact]
    public async Task The_table_only_on_target_is_reported_as_target_only()
    {
        var diff = await CompareAsync();

        var legacy = diff.Find(new ObjectIdentity(ObjectType.Table, "dbo", "SegmentLegacy"));
        Assert.NotNull(legacy);
        Assert.Equal(DiffKind.TargetOnly, legacy.Kind);
    }

    [SkippableFact]
    public async Task The_changed_view_body_is_reported_but_the_identical_procedure_is_not()
    {
        var diff = await CompareAsync();

        var view = diff.Find(new ObjectIdentity(ObjectType.View, "sales", "vCompanySegment"));
        Assert.NotNull(view);
        Assert.Equal(DiffKind.Different, view.Kind);

        var routine = diff.Find(new ObjectIdentity(ObjectType.Routine, "dbo", "usp_GetCompany"));
        Assert.NotNull(routine);
        Assert.Equal(DiffKind.Same, routine.Kind);
    }

    [SkippableFact]
    public async Task The_unchanged_table_is_reported_as_same()
    {
        var diff = await CompareAsync();

        var category = diff.Find(new ObjectIdentity(ObjectType.Table, "dbo", "Category"));
        Assert.NotNull(category);
        Assert.Equal(DiffKind.Same, category.Kind);
    }

    [SkippableFact]
    public async Task Dependency_order_from_a_real_schema_follows_the_foreign_key_chain()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var source = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();

        var order = TableDependencyGraph.Build(source.Tables).OrderForData();

        Assert.False(order.HasCycles);
        Assert.Equal(
            ["dbo.Category", "dbo.Company", "dbo.Contact", "dbo.Digest"],
            order.Ordered.Select(t => t.QualifiedName));
    }

    [SkippableFact]
    public async Task A_plan_over_the_real_diff_emits_each_object_once()
    {
        var diff = await CompareAsync();

        var units = diff.Differing
            .Select(o => new ChangeUnit
            {
                Id = new ChangeUnitId(
                    o.Identity,
                    o.Kind == DiffKind.SourceOnly ? ChangeUnitKind.CreateObject : ChangeUnitKind.AlterObject),
                Description = o.ToString()
            })
            .ToList();

        var plan = PlanCompiler.Compile(
            [PlanSelection.Database(), .. units.Select(u => PlanSelection.Table(u.Id.Object))],
            units);

        Assert.Equal(units.Count, plan.Count);
        Assert.Equal(plan.Count, plan.Units.Select(u => u.Id).Distinct().Count());
    }
}
