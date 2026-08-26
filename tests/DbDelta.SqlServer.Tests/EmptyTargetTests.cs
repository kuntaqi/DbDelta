using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

// An empty target is the case that stresses everything else: every object is a create, so ordering
// stops being a handful of foreign keys and becomes the whole dependency graph at once.
[Collection(nameof(LocalDbCollection))]
public sealed class EmptyTargetTests
{
    private readonly LocalDbFixture _fixture;

    public EmptyTargetTests(LocalDbFixture fixture) => _fixture = fixture;

    private static Task<DatabaseSchema> SourceAsync() =>
        new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();

    [SkippableFact]
    public async Task A_database_with_no_objects_reports_itself_empty()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "EmptyProbe";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var schema = await new SqlServerSchemaReader(connectionString).ReadAsync();

            Assert.True(schema.IsEmpty);
            Assert.Equal(0, schema.ObjectCount);
            Assert.Empty(schema.Tables);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    [SkippableFact]
    public async Task Every_source_object_shows_as_source_only_against_an_empty_target()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "EmptyDiff";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var diff = new SchemaComparer().Compare(source, target);

            Assert.Equal(source.ObjectCount, diff.Count(DiffKind.SourceOnly));
            Assert.Equal(0, diff.Count(DiffKind.TargetOnly));
            Assert.Equal(0, diff.Count(DiffKind.Same));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The one that matters. Provisioning a whole schema from nothing exercises schema creation, bare
    // tables then foreign keys, and dependency order between programmables all at once.
    [SkippableFact]
    public async Task Provisioning_an_empty_database_reproduces_the_source_exactly()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "EmptyProvision";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();

            var comparer = new SchemaComparer();
            var script = new TSqlEmitter().Emit(source, target, comparer.Compare(source, target));

            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script.ToSql(), script.Count);

            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            Assert.Empty(comparer.Compare(source, after).Differing.Select(o => o.ToString()));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A view built on another view cannot be created before the view it selects from, and that order
    // does not come from foreign keys — it comes from the SQL bodies.
    [SkippableFact]
    public async Task A_view_built_on_another_view_is_created_after_it()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "EmptyViewOrder";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var source = await SourceAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));

            var sql = script.ToSql();
            var basePosition = sql.IndexOf("vCompanySegment", StringComparison.Ordinal);
            var dependentPosition = sql.IndexOf("vActiveSegments", StringComparison.Ordinal);

            Assert.True(basePosition >= 0, "the base view should be in the script");
            Assert.True(dependentPosition >= 0, "the dependent view should be in the script");
            Assert.True(
                basePosition < dependentPosition,
                "sales.vCompanySegment must be created before sales.vActiveSegments, which selects from it");
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }
}
