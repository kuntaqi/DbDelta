using DbDelta.Core.Apply;
using DbDelta.Core.Data;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

[Collection(nameof(LocalDbCollection))]
public sealed class DataEmitterTests
{
    private readonly LocalDbFixture _fixture;

    public DataEmitterTests(LocalDbFixture fixture) => _fixture = fixture;

    private static DataChange Change(RowClassification classification, string id, string? name)
    {
        var keys = new Dictionary<string, string?> { ["CategoryId"] = id };
        var values = new Dictionary<string, string?>(keys) { ["Name"] = name };

        return new DataChange(id, id, classification, values, keys);
    }

    private static async Task<TableDataChanges> ChangesAsync(string connectionString, params DataChange[] changes)
    {
        var schema = await new SqlServerSchemaReader(connectionString).ReadAsync();

        return new TableDataChanges
        {
            Table = schema.Tables.Single(t => t.Identity.Name == "Category"),
            KeyColumns = ["CategoryId"],
            Columns = ["Name"],
            Changes = changes,
            TargetRowCount = 3
        };
    }

    private static async Task<List<string>> RowsAsync(string connectionString)
    {
        var rows = new List<string>();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT CategoryId, Name FROM dbo.Category ORDER BY CategoryId;", connection);
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add($"{reader.GetInt32(0)}:{reader.GetString(1)}");
        }

        return rows;
    }

    // The generated DML has to actually run and land the right rows. Comparing emitted strings would
    // only prove the emitter produces the text expected of it, not text the server accepts.
    [SkippableFact]
    public async Task Generated_dml_inserts_updates_and_deletes_real_rows()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "Dml";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            Assert.Equal(["1:Retail", "2:Wholesale Ltd", "4:Obsolete"], await RowsAsync(connectionString));

            var changes = await ChangesAsync(
                connectionString,
                Change(RowClassification.Update, "2", "Wholesale"),
                Change(RowClassification.Insert, "3", "Energy"),
                Change(RowClassification.Delete, "4", "Obsolete"));

            var steps = new TSqlDataEmitter().Emit(changes);
            var script = new SyncScript { Header = "-- data", Steps = steps.OrderBy(s => s.Phase).ToList() };

            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script);

            Assert.Equal(ApplyOutcome.Committed, result.Outcome);
            Assert.Equal(["1:Retail", "2:Wholesale", "3:Energy"], await RowsAsync(connectionString));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    [SkippableFact]
    public async Task An_insert_brackets_identity_insert_and_reseeds_afterwards()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "DmlIdentity";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var changes = await ChangesAsync(connectionString, Change(RowClassification.Insert, "9", "Added"));
            var steps = new TSqlDataEmitter().Emit(changes);

            var insert = steps.Single(s => s.Description.StartsWith("insert", StringComparison.Ordinal));
            Assert.Contains("SET IDENTITY_INSERT [dbo].[Category] ON;", insert.Sql, StringComparison.Ordinal);
            Assert.Contains("SET IDENTITY_INSERT [dbo].[Category] OFF;", insert.Sql, StringComparison.Ordinal);

            // Without the reseed the application's next insert collides with the row just written.
            Assert.Contains(steps, s => s.Sql.Contains("DBCC CHECKIDENT", StringComparison.Ordinal));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    [SkippableFact]
    public async Task Deletes_are_ordered_before_inserts()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "DmlOrder";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var changes = await ChangesAsync(
                connectionString,
                Change(RowClassification.Insert, "3", "Energy"),
                Change(RowClassification.Delete, "4", "Obsolete"));

            var steps = new TSqlDataEmitter().Emit(changes).OrderBy(s => s.Phase).ToList();

            Assert.Equal(ScriptPhase.DataDeletes, steps[0].Phase);
            Assert.All(steps.Skip(1), s => Assert.Equal(ScriptPhase.DataUpserts, s.Phase));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    [SkippableFact]
    public async Task A_quote_in_a_value_is_escaped_rather_than_breaking_the_statement()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "DmlQuote";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var changes = await ChangesAsync(connectionString, Change(RowClassification.Insert, "7", "O'Brien & Co"));
            var script = new SyncScript
            {
                Header = "-- quoting",
                Steps = new TSqlDataEmitter().Emit(changes).OrderBy(s => s.Phase).ToList()
            };

            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script);

            Assert.Equal(ApplyOutcome.Committed, result.Outcome);
            Assert.Contains("7:O'Brien & Co", await RowsAsync(connectionString));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }
}
