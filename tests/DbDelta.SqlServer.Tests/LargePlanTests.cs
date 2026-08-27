using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

// A plan bigger than one fetch. The two-pass design fetches full rows only for what is needed, and the
// cap that makes that true for a screen is wrong for a script: a script needs every row it will write.
[Collection(nameof(LocalDbCollection))]
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

        var rows = await new SqlServerRowDetailReader(sourceConnection).FetchAsync(
            table, request, ["MetricId", "Label", "Amount", "At"], keys);

        Assert.Equal(keys.Count, rows.Count);
        Assert.Equal(keys.Count, rows.Select(r => r.Key).Distinct().Count());
    }
}
