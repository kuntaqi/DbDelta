using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

// A plan bigger than one fetch. The two-pass design fetches full rows only for what is needed, and the
// cap that makes that true for a screen is wrong for a script: a script needs every row it will write.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollectionC))]
public sealed class LargePlanTests
{
    private readonly LocalDbFixture _fixture;

    public LargePlanTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Every_differing_row_is_fetched_not_the_first_batch_of_them()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var sourceConnection = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
        var table = source.Tables.Single(t => t.Identity.Name == "Metric");

        var request = new DataCompareRequest
        {
            Table = table.Identity,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(table)
        };

        var keys = new List<string>();
        await foreach (var row in new SqlServerRowHashReader(sourceConnection)
            .StreamAsync(table, request, ["Label", "Amount", "At"], RowSetSide.Source))
        {
            keys.Add(row.Key);
        }

        Assert.Equal(1200, keys.Count);

        var clock = System.Diagnostics.Stopwatch.StartNew();

        var rows = await new SqlServerRowDetailReader(sourceConnection).FetchAsync(
            table, request, ["MetricId", "Label", "Amount", "At"], keys);

        clock.Stop();

        Assert.Equal(keys.Count, rows.Count);
        Assert.Equal(keys.Count, rows.Select(r => r.Key).Distinct().Count());

        // A budget, not a benchmark. This fetch used to take 27 seconds, because addressing rows with
        // `WHERE <key expression> IN (@k0…@k499)` re-evaluates that nvarchar(max) expression once per list
        // element — 1200 rows against 500 keys is 600,000 LOB concatenations. Joining to the keys instead
        // made it 91ms. The number here is deliberately far above 91ms and far below 27s: a correctness
        // test cannot see the difference between those two, so nothing else in the suite would notice the
        // shape being changed back.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(8),
            $"fetching {keys.Count} rows took {clock.Elapsed.TotalSeconds:N1}s; the row detail query has "
            + "probably gone back to an IN list over the key expression");
    }
}
