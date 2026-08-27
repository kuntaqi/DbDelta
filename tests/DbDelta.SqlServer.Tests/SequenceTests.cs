using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Model;

namespace DbDelta.SqlServer.Tests;

// Sequences were read and compared for months while nothing emitted them, so a plan listed one and the
// script contained nothing for it. Nothing caught it because neither the fixture nor the demo data had a
// sequence — which is why both do now.
[Collection(nameof(LocalDbCollection))]
public sealed class SequenceTests
{
    private readonly LocalDbFixture _fixture;

    public SequenceTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_source_only_sequence_is_created_with_its_bounds_and_cycling()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "SeqCreate";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var (source, target, diff) = await CompareAsync(connectionString);
            var script = new TSqlEmitter().Emit(source, target, diff);

            var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var order = after.Sequences.Single(s => s.Identity.Name == "OrderNumber");

            Assert.Equal("BIGINT", order.DataType.ToString());
            Assert.Equal(1000, order.StartValue);
            Assert.Equal(1, order.Increment);
            Assert.Equal(1000, order.MinValue);
            Assert.False(order.IsCycling);

            var ticket = after.Sequences.Single(s => s.Identity.Name == "TicketNumber");
            Assert.Equal(5, ticket.Increment);
            Assert.True(ticket.IsCycling);

            // The whole point: an empty target compares clean afterwards. Before this, the sequences were
            // in the plan, absent from the script, and still missing when it was over.
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // Increment, bounds and cycling are alterable, so a sequence that differs in them is altered in place
    // rather than dropped and rebuilt — dropping it would lose the value it has reached.
    [SkippableFact]
    public async Task A_sequence_that_differs_is_altered_rather_than_recreated()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "SeqAlter";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            // Move the target's sequence on, so a drop and recreate would be visible as a rewind.
            await ExecuteAsync(connectionString, "SELECT NEXT VALUE FOR dbo.TicketNumber;");
            await ExecuteAsync(connectionString, "SELECT NEXT VALUE FOR dbo.TicketNumber;");

            var (source, target, diff) = await CompareAsync(connectionString);
            var script = new TSqlEmitter().Emit(source, target, diff);
            var sql = script.ToSql();

            Assert.Contains("ALTER SEQUENCE [dbo].[TicketNumber]", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("RESTART", sql, StringComparison.Ordinal);

            var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var ticket = after.Sequences.Single(s => s.Identity.Name == "TicketNumber");

            Assert.Equal(5, ticket.Increment);
            Assert.True(ticket.IsCycling);

            // Where it had got to is untouched: RESTART WITH would have rewound it and handed out numbers
            // it had already issued.
            Assert.True(
                await ScalarAsync(connectionString, "SELECT CONVERT(int, current_value) FROM sys.sequences WHERE name = 'TicketNumber'") > 1,
                "the sequence should not have been rewound");
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    [SkippableFact]
    public async Task A_target_only_sequence_is_dropped_and_counts_as_destructive()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "SeqDrop";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var (source, target, diff) = await CompareAsync(connectionString);
            var script = new TSqlEmitter().Emit(source, target, diff);

            var drop = Assert.Single(script.Steps, s => s.Description.Contains("drop sequence"));

            Assert.Contains("LegacyCounter", drop.Description, StringComparison.Ordinal);
            Assert.True(drop.Destructive, "a dropped sequence cannot be brought back to where it was");

            var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, script);
            Assert.Equal(ApplyOutcome.Committed, result.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            Assert.DoesNotContain(after.Sequences, s => s.Identity.Name == "LegacyCounter");
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // A sequence is created before the tables and dropped after them, because a column default can read
    // NEXT VALUE FOR one.
    [SkippableFact]
    public async Task A_sequence_is_created_before_the_tables()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "SeqOrder";
        var connectionString = await _fixture.CreateEmptyTargetAsync(scratch);

        try
        {
            var (source, target, diff) = await CompareAsync(connectionString);
            var sql = new TSqlEmitter().Emit(source, target, diff).ToSql();

            Assert.True(
                sql.IndexOf("CREATE SEQUENCE [dbo].[OrderNumber]", StringComparison.Ordinal)
                    < sql.IndexOf("CREATE TABLE", StringComparison.Ordinal),
                "a column default can read NEXT VALUE FOR a sequence");
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    private static async Task<(DatabaseSchema Source, DatabaseSchema Target, SchemaDiff Diff)> CompareAsync(
        string targetConnection)
    {
        var source = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
        var target = await new SqlServerSchemaReader(targetConnection).ReadAsync();

        return (source, target, new SchemaComparer().Compare(source, target));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = new Microsoft.Data.SqlClient.SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
