using DbDelta.Core.Apply;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// The staged path has to land the same data as the literal one — that is the whole bar. These run both
// against a real server and compare the target afterwards, because the staging table converts text back
// into types and that is where a datetime or a decimal would quietly come out wrong.
[Trait("Speed", "Slow")]
[Collection(nameof(LocalDbCollectionC))]
public sealed class StagedBulkTests
{
    private readonly LocalDbFixture _fixture;

    public StagedBulkTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Staged_rows_land_exactly_as_the_literal_path_would_have_written_them()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string inline = "StagedOffCmp";
        const string staged = "StagedOnCmp";

        var inlineConnection = await _fixture.CreateScratchTargetAsync(inline);
        var stagedConnection = await _fixture.CreateScratchTargetAsync(staged);

        try
        {
            // Same 1200 rows, written both ways. Anything the staging conversion gets wrong shows up as a
            // difference between the two targets.
            await RunAsync(inlineConnection, long.MaxValue);
            await RunAsync(stagedConnection, 1);

            Assert.Equal(1200, await ScalarAsync(inlineConnection, "SELECT COUNT(*) FROM dbo.Metric"));
            Assert.Equal(1200, await ScalarAsync(stagedConnection, "SELECT COUNT(*) FROM dbo.Metric"));

            // Compared column by column rather than by row count: a count would pass with every value
            // mangled the same way.
            Assert.Equal(0, await DifferenceCountAsync(inlineConnection, stagedConnection));
        }
        finally
        {
            await _fixture.DropScratchAsync(inline);
            await _fixture.DropScratchAsync(staged);
        }
    }

    [SkippableFact]
    public async Task A_staged_plan_keeps_the_script_small_however_many_rows_it_moves()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "StagedSize";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var changes = await ChangesAsync(connectionString);

            var literal = new SyncScript { Header = "--", Steps = new TSqlDataEmitter().Emit(changes, long.MaxValue) };
            var staged = new SyncScript { Header = "--", Steps = new TSqlDataEmitter().Emit(changes, 1) };

            Assert.True(
                staged.ToSql().Length * 20 < literal.ToSql().Length,
                $"staged {staged.ToSql().Length} vs literal {literal.ToSql().Length}");

            var load = Assert.Single(staged.Loads);
            Assert.Equal(1200, load.RowCount);
            Assert.Equal("dbo.Metric", load.Table);

            // The rows are not in the script, so the script alone cannot be the whole download.
            Assert.DoesNotContain("row 900", staged.ToSql(), StringComparison.Ordinal);
            Assert.Contains("row 900", ScriptPackagerCsv(load), StringComparison.Ordinal);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A staged load and the statements around it are one unit or they are nothing: the staging table is
    // temporary and the load is not SQL, so if they did not share a transaction a failure would leave the
    // target half-written.
    [SkippableFact]
    public async Task A_failure_after_the_load_rolls_the_staged_rows_back_too()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "StagedRollback";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var changes = await ChangesAsync(connectionString);
            var steps = new TSqlDataEmitter().Emit(changes, 1).ToList();

            steps.Add(new ScriptStep(
                ScriptPhase.DataUpserts,
                "deliberate failure after the load",
                "INSERT INTO dbo.NoSuchTable (X) VALUES (1);"));

            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, new SyncScript { Header = "--", Steps = steps });

            Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
            Assert.Equal(0, await ScalarAsync(connectionString, "SELECT COUNT(*) FROM dbo.Metric"));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private static string ScriptPackagerCsv(BulkLoad load) =>
        string.Join("\n", load.Rows.Select(r => string.Join(",", r.Select(v => v ?? string.Empty))));

    private async Task RunAsync(string connectionString, long maxInlineBytes)
    {
        var changes = await ChangesAsync(connectionString);
        var script = new SyncScript { Header = "--", Steps = new TSqlDataEmitter().Emit(changes, maxInlineBytes) };

        var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);

        Assert.Equal(ApplyOutcome.Committed, result.Outcome);
    }

    private static async Task<TableDataChanges> ChangesAsync(string targetConnection)
    {
        var sourceConnection = LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase);
        var source = await new SqlServerSchemaReader(sourceConnection).ReadAsync();
        var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();

        var sourceTable = source.Tables.Single(t => t.Identity.Name == "Metric");
        var targetTable = target.Tables.Single(t => t.Identity.Name == "Metric");

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

        var fetched = columns.ComparedColumns.Concat(request.KeyColumns)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var rows = (await new SqlServerRowDetailReader(sourceConnection).FetchAsync(
                sourceTable, request, fetched,
                result.Differences.Where(d => d.Classification != RowClassification.Delete)
                    .Select(d => d.Key).ToList()))
            .ToDictionary(r => r.Key, StringComparer.Ordinal);

        var changes = result.Differences
            .Where(d => rows.ContainsKey(d.Key))
            .Select(d =>
            {
                var row = rows[d.Key];
                var keys = request.KeyColumns.ToDictionary(
                    c => c, c => row.Values.GetValueOrDefault(c), StringComparer.OrdinalIgnoreCase);

                return new DataChange(d.Key, d.Key, d.Classification, row.Values, keys, d.TargetHash);
            })
            .ToList();

        return new TableDataChanges
        {
            Table = sourceTable,
            KeyColumns = request.KeyColumns,
            Columns = columns.ComparedColumns,
            Changes = changes
        };
    }

    // EXCEPT both ways, so a row present in one target and absent from the other counts either way.
    private static async Task<int> DifferenceCountAsync(string left, string right)
    {
        var rows = new List<string>();

        foreach (var connection in new[] { left, right })
        {
            await using var open = new SqlConnection(connection);
            await open.OpenAsync();

            await using var command = new SqlCommand(
                "SELECT CONVERT(nvarchar(max), MetricId) + N'|' + Label + N'|' + CONVERT(nvarchar(max), Amount) "
                + "+ N'|' + COALESCE(CONVERT(nvarchar(max), At, 126), N'-') FROM dbo.Metric ORDER BY MetricId;",
                open);

            await using var reader = await command.ExecuteReaderAsync();
            var side = new List<string>();
            while (await reader.ReadAsync())
            {
                side.Add(reader.GetString(0));
            }

            rows.Add(string.Join("\n", side));
        }

        return string.Equals(rows[0], rows[1], StringComparison.Ordinal) ? 0 : 1;
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
