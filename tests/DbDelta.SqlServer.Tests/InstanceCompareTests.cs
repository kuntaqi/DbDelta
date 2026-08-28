using DbDelta.Core.Instances;

namespace DbDelta.SqlServer.Tests;

// Comparing two instances against a real server. There is only one LocalDB instance here, so both sides of
// these are the same server — which still exercises everything that matters, because what is being tested
// is the pairing and what gets read, not that two hostnames differ.
//
// The naming constraint applies with force here: this reads every database name on the instance, so these
// assert containment and never the whole list.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollection))]
public sealed class InstanceCompareTests
{
    private readonly LocalDbFixture _fixture;

    public InstanceCompareTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task An_instance_against_itself_pairs_every_database_by_name()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var databases = await ListAsync();
        var match = InstanceMatcher.Match(databases, databases);

        Assert.Equal(databases.Count, match.OnBothSides);
        Assert.Equal(0, match.SourceOnly);
        Assert.Equal(0, match.TargetOnly);
        Assert.Empty(match.Problems);

        // The fixture's own two are in there, which is what proves the list is the real one.
        Assert.Contains(match.Pairs, p => p.Name == LocalDbFixture.SourceDatabase && p.OnBothSides);
        Assert.Contains(match.Pairs, p => p.Name == LocalDbFixture.TargetDatabase && p.OnBothSides);
    }

    // A database that exists on one side only. Created for this test so the assertion is about a known
    // database rather than about whatever else happens to be on the instance.
    [SkippableFact]
    public async Task A_database_created_on_one_side_only_is_reported_on_that_side()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "InstanceOnly";
        var before = await ListAsync();
        await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var after = await ListAsync();
            var name = $"{LocalDbFixture.TargetDatabase}_{scratch}";

            Assert.Contains(after, d => d.Name == name);

            // "after" as the source, "before" as the target: the new database is on the source only.
            var match = InstanceMatcher.Match(after, before);

            Assert.Contains(match.Pairs, p => p.Kind == PairKind.SourceOnly && p.Name == name);

            // And the other way round it is on the target only, which is the same fact read from the other
            // end rather than a second one.
            var reversed = InstanceMatcher.Match(before, after);
            Assert.Contains(reversed.Pairs, p => p.Kind == PairKind.TargetOnly && p.Name == name);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The case the feature exists for: two databases that hold the same thing under different names. The
    // fixture's source and target are exactly that — deliberately different contents, different names.
    [SkippableFact]
    public async Task Databases_with_different_names_can_be_paired_and_then_described()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var databases = await ListAsync();

        var match = InstanceMatcher.Match(
            databases,
            databases,
            [new DatabasePairing(LocalDbFixture.SourceDatabase, LocalDbFixture.TargetDatabase)]);

        var pair = Assert.Single(match.Pairs, p => p.Kind == PairKind.Declared);

        Assert.Equal(LocalDbFixture.SourceDatabase, pair.Source!.Name);
        Assert.Equal(LocalDbFixture.TargetDatabase, pair.Target!.Name);
        Assert.True(pair.CanBeCompared);
        Assert.Empty(match.Problems);

        // Both names are consumed by the pairing, so neither can also pair with itself by name.
        Assert.DoesNotContain(
            match.Pairs,
            p => p.Kind == PairKind.ByName
                && (p.Name == LocalDbFixture.SourceDatabase || p.Name == LocalDbFixture.TargetDatabase));

        // Reading them confirms the pairing is worth something: these two really do differ, which is what
        // the fixture is built to be.
        var left = await DescribeAsync(LocalDbFixture.SourceDatabase);
        var right = await DescribeAsync(LocalDbFixture.TargetDatabase);

        Assert.True(
            left.Tables != right.Tables || left.Views != right.Views || left.Routines != right.Routines,
            "the fixture's two databases are supposed to differ in what they hold");
    }

    // Object counts are all the cheap pass can offer, and they are not a schema comparison. Proving the gap
    // rather than asserting it: the emitter's own tests show these two databases differ in dozens of ways,
    // and a count comparison of one of those pairs can still come out equal.
    [SkippableFact]
    public async Task Equal_object_counts_do_not_mean_equal_schemas()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "InstanceCounts";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            // A copy of the target, then one column widened. Same number of everything.
            await ExecuteAsync(connectionString, "ALTER TABLE dbo.Category ALTER COLUMN Name NVARCHAR(200) NOT NULL;");

            var target = await DescribeAsync($"{LocalDbFixture.TargetDatabase}_{scratch}");
            var original = await DescribeAsync(LocalDbFixture.TargetDatabase);

            Assert.Equal(original.Tables, target.Tables);
            Assert.Equal(original.Views, target.Views);
            Assert.Equal(original.Routines, target.Routines);

            // And yet they differ, which is the whole point of the caveat the comparison prints.
            var diff = new Core.Comparison.SchemaComparer().Compare(
                await new SqlServerSchemaReader(
                    LocalDbFixture.ConnectionStringFor(LocalDbFixture.TargetDatabase)).ReadAsync(),
                await new SqlServerSchemaReader(connectionString).ReadAsync());

            Assert.NotEmpty(diff.Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private static async Task<IReadOnlyList<Core.Model.DatabaseSummary>> ListAsync() =>
        await new SqlServerProvider().ListDatabasesAsync(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase));

    private static async Task<Core.Model.DatabaseDetail> DescribeAsync(string database) =>
        await new SqlServerProvider().DescribeDatabaseAsync(
            LocalDbFixture.ConnectionStringFor(database));

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
