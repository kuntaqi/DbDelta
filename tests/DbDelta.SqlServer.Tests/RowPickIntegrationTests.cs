using DbDelta.Core.Apply;
using DbDelta.Core.Data;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// dbo.Category differs by exactly one of each kind between the fixture's two sides — CategoryId 2 is an
// update, 3 an insert, 4 a delete — so narrowing to one row and checking the other two are untouched is a
// real test of the narrowing rather than of a filter that happens to keep everything.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollection))]
public sealed class RowPickIntegrationTests
{
    private readonly LocalDbFixture _fixture;

    public RowPickIntegrationTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Only_the_picked_row_is_written_and_the_others_are_left_differing()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "RowPick";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var (differences, build) = await CompareAsync(connectionString);

            Assert.Equal(3, differences.Count);

            // Picked by what it is rather than by a key spelled out here: the key is the digest
            // expression's own text, and writing one into a test would be asserting the digest's format.
            var update = differences.Single(d => d.Classification == RowClassification.Update);
            var picks = RowPicks.Apply(differences, new HashSet<string>([update.Key], StringComparer.Ordinal));

            Assert.Single(picks.Kept);
            Assert.Equal(3, picks.AvailableCount);
            Assert.Empty(picks.Unmatched);

            var script = new SyncScript
            {
                Header = "-- narrowed",
                Steps = new TSqlDataEmitter().Emit(await build(picks.Kept), 1_000_000).ToList()
            };

            var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            // The picked row moved.
            Assert.Equal("Wholesale", await TextAsync(connectionString, "SELECT Name FROM dbo.Category WHERE CategoryId = 2"));

            // And the two that were not picked are exactly as they were: the insert did not happen and
            // the delete did not happen. A narrowing that quietly took the whole table would fail here.
            Assert.Equal(0, await CountAsync(connectionString, "SELECT COUNT(*) FROM dbo.Category WHERE CategoryId = 3"));
            Assert.Equal(1, await CountAsync(connectionString, "SELECT COUNT(*) FROM dbo.Category WHERE CategoryId = 4"));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // Picks are made against one comparison and spent against another. A row that stopped differing in
    // between has to be reported, because the alternative is a script quietly smaller than its review.
    [SkippableFact]
    public async Task A_pick_whose_row_stopped_differing_comes_back_as_unmatched()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "RowPickStale";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var (before, _) = await CompareAsync(connectionString);
            var update = before.Single(d => d.Classification == RowClassification.Update);

            // Someone else fixes the very row that was picked.
            await ExecuteAsync(connectionString, "UPDATE dbo.Category SET Name = N'Wholesale' WHERE CategoryId = 2;");

            var (after, _) = await CompareAsync(connectionString);
            var picks = RowPicks.Apply(after, new HashSet<string>([update.Key], StringComparer.Ordinal));

            Assert.Empty(picks.Kept);
            Assert.Equal([update.Key], picks.Unmatched);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The two-pass compare for one table, plus a way to turn a chosen subset into the rows the emitter
    // wants. Row values are fetched for the kept keys only, which is the point: narrowing happens before
    // anything is read, not after.
    private static async Task<(IReadOnlyList<RowDifference> Differences,
        Func<IReadOnlyList<RowDifference>, Task<TableDataChanges>> Build)> CompareAsync(string targetConnection)
    {
        var sourceConnection = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
        var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();

        var sourceTable = source.Tables.Single(t => t.Identity.Name == "Category");
        var targetTable = target.Tables.Single(t => t.Identity.Name == "Category");

        var request = new DataCompareRequest
        {
            Table = sourceTable.Identity,
            KeyColumns = ColumnSetResolver.DefaultKeyFor(sourceTable)
        };

        var columns = ColumnSetResolver.Resolve(sourceTable, targetTable, request);

        var result = await DataComparer.CompareAsync(
            new SqlServerRowHashReader(sourceConnection)
                .StreamAsync(sourceTable, request, columns.ComparedColumns, RowSetSide.Source),
            new SqlServerRowHashReader(targetConnection)
                .StreamAsync(targetTable, request, columns.ComparedColumns, RowSetSide.Target),
            new DataCompareSettings { Mode = TableDataMode.AllRows, ComparedColumns = columns.ComparedColumns });

        async Task<TableDataChanges> Build(IReadOnlyList<RowDifference> kept)
        {
            var fetched = columns.ComparedColumns.Concat(request.KeyColumns)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var sourceRows = (await new SqlServerRowDetailReader(sourceConnection).FetchAsync(
                    sourceTable, request, fetched,
                    kept.Where(d => d.Classification != RowClassification.Delete).Select(d => d.Key).ToList()))
                .ToDictionary(r => r.Key, StringComparer.Ordinal);

            var targetRows = (await new SqlServerRowDetailReader(targetConnection).FetchAsync(
                    targetTable, request, fetched,
                    kept.Where(d => d.Classification == RowClassification.Delete).Select(d => d.Key).ToList()))
                .ToDictionary(r => r.Key, StringComparer.Ordinal);

            var changes = kept
                .Select(d =>
                {
                    var row = d.Classification == RowClassification.Delete
                        ? targetRows.GetValueOrDefault(d.Key)
                        : sourceRows.GetValueOrDefault(d.Key);

                    if (row is null)
                    {
                        return null;
                    }

                    var keys = request.KeyColumns.ToDictionary(
                        c => c, c => row.Values.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase);

                    return new DataChange(
                        d.Key,
                        string.Join(", ", keys.Values.Select(v => v ?? "NULL")),
                        d.Classification,
                        row.Values,
                        keys,
                        d.TargetHash);
                })
                .OfType<DataChange>()
                .ToList();

            return new TableDataChanges
            {
                Table = sourceTable,
                KeyColumns = request.KeyColumns,
                Columns = columns.ComparedColumns,
                Changes = changes
            };
        }

        return (result.Differences, Build);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(string connectionString, string sql) =>
        Convert.ToInt32(await ScalarAsync(connectionString, sql));

    private static async Task<string?> TextAsync(string connectionString, string sql) =>
        (await ScalarAsync(connectionString, sql))?.ToString();

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
