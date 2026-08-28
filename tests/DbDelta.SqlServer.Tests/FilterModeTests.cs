using DbDelta.Core.Data;

namespace DbDelta.SqlServer.Tests;

// Filter mode was reachable from the backend long before it had a control, so what these check is that it
// does what the mode claims: narrows both sides to the same predicate, and never reports a delete.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollectionB))]
public sealed class FilterModeTests
{
    private readonly LocalDbFixture _fixture;

    public FilterModeTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_predicate_narrows_the_rows_that_are_compared()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var all = await StreamAsync(null);
        var filtered = await StreamAsync("MetricId <= 10");

        Assert.Equal(1200, all.Count);
        Assert.Equal(10, filtered.Count);
    }

    // Both sides, not just the source. Filtering one would compare a window against a whole table and call
    // every row outside the window a difference.
    [SkippableFact]
    public async Task The_predicate_applies_to_the_target_as_well()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "FilterBothSides";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var table = target.Tables.Single(t => t.Identity.Name == "Category");

            var request = new DataCompareRequest
            {
                Table = table.Identity,
                KeyColumns = ["CategoryId"],
                Mode = TableDataMode.Filter,
                FilterPredicate = "CategoryId <= 2"
            };

            var rows = new List<KeyHashRow>();
            await foreach (var row in new SqlServerRowHashReader(connectionString)
                .StreamAsync(table, request, ["Name"], RowSetSide.Target))
            {
                rows.Add(row);
            }

            // The target holds categories 1, 2 and 4. Unfiltered that is three rows; the predicate has to
            // narrow the target stream exactly as it narrows the source.
            Assert.Equal(2, rows.Count);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The correctness rule the mode exists under: absence from a limited row set means "outside the
    // filter", never "deleted". Honouring deletes here would empty the target while claiming to seed it.
    [SkippableFact]
    public async Task Deletes_are_suppressed_because_absence_only_means_outside_the_filter()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "FilterDeletes";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var source = await new SqlServerSchemaReader(
                LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();

            var sourceTable = source.Tables.Single(t => t.Identity.Name == "Category");
            var targetTable = target.Tables.Single(t => t.Identity.Name == "Category");

            var request = new DataCompareRequest
            {
                Table = sourceTable.Identity,
                KeyColumns = ["CategoryId"],
                Mode = TableDataMode.Filter,
                FilterPredicate = "CategoryId <= 4"
            };

            var columns = ColumnSetResolver.Resolve(sourceTable, targetTable, request);

            var result = await DataComparer.CompareAsync(
                new SqlServerRowHashReader(LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase))
                    .StreamAsync(sourceTable, request, columns.ComparedColumns, RowSetSide.Source),
                new SqlServerRowHashReader(connectionString)
                    .StreamAsync(targetTable, request, columns.ComparedColumns, RowSetSide.Target),
                new DataCompareSettings { Mode = TableDataMode.Filter, ComparedColumns = columns.ComparedColumns });

            // Category 4 is on the target and not the source, and it is inside the filter — so this is the
            // case where a delete would otherwise be reported.
            Assert.True(result.DeletesSuppressed);
            Assert.Equal(0, result.DeleteCount);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private async Task<List<KeyHashRow>> StreamAsync(string? predicate)
    {
        var connectionString = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        var table = await TableAsync(connectionString);

        var request = new DataCompareRequest
        {
            Table = table.Identity,
            KeyColumns = ["MetricId"],
            Mode = predicate is null ? TableDataMode.AllRows : TableDataMode.Filter,
            FilterPredicate = predicate
        };

        var rows = new List<KeyHashRow>();
        await foreach (var row in new SqlServerRowHashReader(connectionString)
            .StreamAsync(table, request, ["Label", "Amount", "At"], RowSetSide.Source))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static async Task<Core.Model.TableDefinition> TableAsync(string connectionString)
    {
        var schema = await new SqlServerSchemaReader(connectionString).ReadAsync();
        return schema.Tables.Single(t => t.Identity.Name == "Metric");
    }
}
