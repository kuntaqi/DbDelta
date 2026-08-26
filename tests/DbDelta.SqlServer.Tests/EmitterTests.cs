using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

[Collection(nameof(LocalDbCollection))]
public sealed class EmitterTests
{
    private readonly LocalDbFixture _fixture;

    public EmitterTests(LocalDbFixture fixture) => _fixture = fixture;

    private async Task<(DatabaseSchema Source, DatabaseSchema Target, SyncScript Script)> BuildAsync()
    {
        var source = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
        var target = await new SqlServerSchemaReader(
            LocalDbFixture.ConnectionStringFor(LocalDbFixture.TargetDatabase)).ReadAsync();

        var diff = new SchemaComparer().Compare(source, target);
        return (source, target, new TSqlEmitter().Emit(source, target, diff));
    }

    [SkippableFact]
    public async Task Steps_are_ordered_by_phase()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (_, _, script) = await BuildAsync();

        var phases = script.Steps.Select(s => (int)s.Phase).ToList();
        Assert.Equal(phases.OrderBy(p => p), phases);
    }

    [SkippableFact]
    public async Task A_view_is_wrapped_so_it_can_share_one_transaction()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (_, _, script) = await BuildAsync();

        var programmables = script.InPhase(ScriptPhase.Programmables).ToList();

        Assert.NotEmpty(programmables);
        Assert.All(programmables, step =>
        {
            Assert.StartsWith("EXEC sp_executesql N'", step.Sql, StringComparison.Ordinal);
            Assert.Contains("CREATE OR ALTER", step.Sql, StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task A_dependent_index_is_dropped_before_the_column_it_indexes_is_altered()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (_, _, script) = await BuildAsync();

        var steps = script.InPhase(ScriptPhase.AlterColumns).ToList();
        var drop = steps.FindIndex(s => s.Sql.Contains("DROP INDEX", StringComparison.Ordinal)
            && s.Sql.Contains("UX_Company_Segment", StringComparison.Ordinal));
        var alter = steps.FindIndex(s => s.Sql.Contains("ALTER COLUMN", StringComparison.Ordinal)
            && s.Sql.Contains("Segment", StringComparison.Ordinal));

        Assert.True(drop >= 0, "the filtered index on Segment should be dropped");
        Assert.True(alter > drop, "the column alter must come after the index that depends on it is gone");
        Assert.Contains(
            script.InPhase(ScriptPhase.Indexes),
            s => s.Sql.Contains("UX_Company_Segment", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task The_script_contains_the_transaction_wrapper()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        var (_, _, script) = await BuildAsync();
        var sql = script.ToSql();

        Assert.Contains("SET XACT_ABORT ON;", sql, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION;", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT TRANSACTION;", sql, StringComparison.Ordinal);
    }

    // The one that matters: run the generated script against a real database and prove the target
    // ends up matching the source. Anything the emitter gets wrong shows up here as a SQL error or
    // as a diff that refuses to go empty.
    [SkippableFact]
    public async Task Running_the_script_makes_the_target_match_the_source()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "Apply";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var source = await new SqlServerSchemaReader(
                LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
            var before = await new SqlServerSchemaReader(connectionString).ReadAsync();

            var comparer = new SchemaComparer();
            var diff = comparer.Compare(source, before);
            Assert.NotEmpty(diff.Differing);

            var script = new TSqlEmitter().Emit(source, before, diff);

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(script.ToSql(), connection);
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync();
            }

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var remaining = comparer.Compare(source, after);

            Assert.Empty(remaining.Differing.Select(o => o.ToString()));
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    [SkippableFact]
    public async Task A_failing_script_leaves_the_target_untouched()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "Rollback";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var before = await new SqlServerSchemaReader(connectionString).ReadAsync();

            // A valid first statement followed by a broken one, inside the same wrapper the emitter uses.
            var sql = new SyncScript
            {
                Header = "-- deliberate failure",
                Steps =
                [
                    new ScriptStep(ScriptPhase.AlterColumns, "add a column",
                        "ALTER TABLE [dbo].[Company] ADD [ShouldNotSurvive] INT NULL;"),
                    new ScriptStep(ScriptPhase.AlterColumns, "reference a table that is not there",
                        "ALTER TABLE [dbo].[NoSuchTable] ADD [X] INT NULL;")
                ]
            }.ToSql();

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(sql, connection);
                await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            }

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var company = after.Tables.Single(t => t.Identity.Name == "Company");

            Assert.DoesNotContain(company.Columns, c => c.Name == "ShouldNotSurvive");
            Assert.Equal(before.Tables.Count, after.Tables.Count);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }
}
