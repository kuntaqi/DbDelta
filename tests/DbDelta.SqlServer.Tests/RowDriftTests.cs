using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// The drift check is only worth anything if the hash it compares actually comes from the row. These take
// two snapshots either side of a real UPDATE on the target and check that the second one notices.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollection))]
public sealed class RowDriftTests
{
    private readonly LocalDbFixture _fixture;

    public RowDriftTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_row_the_plan_would_overwrite_changing_on_the_target_is_drift()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "DriftMoved";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            // dbo.Category 2 differs by name on both sides, so the plan would update it.
            var reviewed = await SnapshotAsync(connectionString);

            await ExecuteAsync(connectionString, "UPDATE dbo.Category SET Name = N'Changed by someone else' WHERE CategoryId = 2;");

            var now = await SnapshotAsync(connectionString);
            var drift = reviewed.DriftAgainst(now);

            Assert.Contains(drift, d => d.Contains("dbo.Category") && d.Contains("have changed on the target"));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    [SkippableFact]
    public async Task An_untouched_target_reads_as_no_drift()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "DriftQuiet";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var reviewed = await SnapshotAsync(connectionString);
            var now = await SnapshotAsync(connectionString);

            Assert.Empty(reviewed.DriftAgainst(now));
            Assert.True(reviewed.RowCount > 0, "the fixture should give this plan rows to check");
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A row that was identical at review time and differs by apply time is the plan growing after it was
    // read. The reviewed rows are all still untouched, so only checking those would call this clean.
    [SkippableFact]
    public async Task A_row_that_starts_differing_after_the_review_is_drift()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "DriftGrew";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var reviewed = await SnapshotAsync(connectionString);

            // Category 1 matches on both sides, so it is in nobody's plan until this.
            await ExecuteAsync(connectionString, "UPDATE dbo.Category SET Name = N'Drifted' WHERE CategoryId = 1;");

            var now = await SnapshotAsync(connectionString);

            Assert.Contains(reviewed.DriftAgainst(now), d => d.Contains("now differ that did not"));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The same two-pass compare the script path runs for a picked table, reduced to what these need.
    private static async Task<RowStateSnapshot> SnapshotAsync(string targetConnection)
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

        var changes = result.Differences
            .Select(d => new DataChange(
                d.Key,
                d.Key,
                d.Classification,
                new Dictionary<string, string?>(),
                new Dictionary<string, string?>(),
                d.TargetHash))
            .ToList();

        return RowStateSnapshot.From(
        [
            new TableDataChanges
            {
                Table = sourceTable,
                KeyColumns = request.KeyColumns,
                Columns = columns.ComparedColumns,
                Changes = changes
            }
        ]);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
