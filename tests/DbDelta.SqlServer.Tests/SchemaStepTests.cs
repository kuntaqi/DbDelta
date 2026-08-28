using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer.Tests;

// Nothing ever asserted on the schema phase, which is the whole reason it was wrong: the emitter added
// every schema in the source database to the ones the plan needed, and since CreateSchemaIfMissing is
// guarded and idempotent, no end-to-end test could tell. A step that does nothing still has to be a step
// the plan asked for.
//
// The fixture has what this needs on both sides: sales.vActiveSegments exists only on the source, so a
// plan containing it must create the schema, while sales.vCompanySegment exists on both, so a plan
// containing that one must not.
[Collection(nameof(LocalDbCollection))]
public sealed class SchemaStepTests
{
    private readonly LocalDbFixture _fixture;

    public SchemaStepTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_plan_of_dbo_objects_alone_creates_no_schema()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target, diff) = await CompareAsync();

        var dbo = diff.Differing
            .Where(o => o.Kind == DiffKind.SourceOnly && o.Identity.Schema == "dbo")
            .Select(o => o.Identity)
            .ToHashSet();

        Assert.NotEmpty(dbo);

        var script = new TSqlEmitter().Emit(source, target, diff, dbo);

        // Before the fix this carried "ensure schema sales" because the source database happens to have a
        // sales schema — nothing in the plan referred to it.
        Assert.Empty(script.InPhase(ScriptPhase.Schemas));
    }

    [SkippableFact]
    public async Task A_plan_that_creates_an_object_in_a_schema_creates_the_schema()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target, diff) = await CompareAsync();

        var view = diff.Differing.Single(o =>
            o.Kind == DiffKind.SourceOnly && o.Identity.Name == "vActiveSegments");

        var script = new TSqlEmitter().Emit(source, target, diff, new HashSet<ObjectIdentity> { view.Identity });

        var step = Assert.Single(script.InPhase(ScriptPhase.Schemas));

        Assert.Contains("sales", step.Description, StringComparison.Ordinal);
        Assert.Contains("SCHEMA_ID", step.Sql, StringComparison.Ordinal);
    }

    // An object that merely differs is already on the target, so its schema is too. Creating one for it
    // would be the plan doing something nobody asked for, even though the statement is a no-op.
    [SkippableFact]
    public async Task A_plan_of_objects_that_only_differ_creates_no_schema()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target, diff) = await CompareAsync();

        var existing = diff.Differing
            .Where(o => o.Kind == DiffKind.Different)
            .Select(o => o.Identity)
            .ToHashSet();

        Assert.NotEmpty(existing);

        var script = new TSqlEmitter().Emit(source, target, diff, existing);

        Assert.Empty(script.InPhase(ScriptPhase.Schemas));
    }

    // The unfiltered script still has to carry it, or the fix would have swapped one wrong answer for
    // another: sales.vActiveSegments is in there, so sales has to be.
    [SkippableFact]
    public async Task The_whole_plan_still_creates_the_schema_its_new_objects_need()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target, diff) = await CompareAsync();
        var script = new TSqlEmitter().Emit(source, target, diff);

        var step = Assert.Single(script.InPhase(ScriptPhase.Schemas));

        Assert.Contains("sales", step.Description, StringComparison.Ordinal);
    }

    // dbo exists in every database, so asking for it is noise whatever the plan holds.
    [SkippableFact]
    public async Task Dbo_is_never_created()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (source, target, diff) = await CompareAsync();
        var sql = new TSqlEmitter().Emit(source, target, diff).ToSql();

        Assert.DoesNotContain("SCHEMA_ID('dbo')", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(DatabaseSchema Source, DatabaseSchema Target, SchemaDiff Diff)> CompareAsync()
    {
        var source = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
        var target = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.TargetDatabase)).ReadAsync();

        return (source, target, new SchemaComparer().Compare(source, target));
    }
}
