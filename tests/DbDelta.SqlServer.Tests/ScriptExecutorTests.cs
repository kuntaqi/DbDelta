using DbDelta.Core.Apply;
using DbDelta.Core.Comparison;
using DbDelta.Core.Scripting;

namespace DbDelta.SqlServer.Tests;

[Collection(nameof(LocalDbCollection))]
public sealed class ScriptExecutorTests
{
    private readonly LocalDbFixture _fixture;

    public ScriptExecutorTests(LocalDbFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_successful_script_reports_committed_and_changes_the_target()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ExecOk";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var source = await new SqlServerSchemaReader(
                LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();

            var diff = new SchemaComparer().Compare(source, target);
            var script = new TSqlEmitter().Emit(source, target, diff);

            var result = await new SqlServerScriptExecutor()
                .ExecuteAsync(connectionString, script.ToSql(), script.Count);

            Assert.Equal(ApplyOutcome.Committed, result.Outcome);
            Assert.True(result.Succeeded);
            Assert.Equal(script.Count, result.StepCount);
            Assert.Null(result.ServerMessage);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }

    // The executor must report a failure as a rollback rather than letting the exception escape, and
    // the target has to come back untouched.
    [SkippableFact]
    public async Task A_failing_script_reports_rolled_back_and_leaves_nothing_behind()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ExecFail";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var before = await new SqlServerSchemaReader(connectionString).ReadAsync();

            var sql = new SyncScript
            {
                Header = "-- deliberate failure",
                Steps =
                [
                    new ScriptStep(ScriptPhase.AlterColumns, "add a column",
                        "ALTER TABLE [dbo].[Company] ADD [ShouldNotSurvive] INT NULL;"),
                    new ScriptStep(ScriptPhase.AlterColumns, "touch a table that is not there",
                        "ALTER TABLE [dbo].[NoSuchTable] ADD [X] INT NULL;")
                ]
            }.ToSql();

            var result = await new SqlServerScriptExecutor().ExecuteAsync(connectionString, sql, 2);

            Assert.Equal(ApplyOutcome.RolledBack, result.Outcome);
            Assert.False(result.Succeeded);
            Assert.NotNull(result.ServerMessage);
            Assert.NotNull(result.ErrorNumber);
            Assert.Contains("unchanged", result.Message, StringComparison.Ordinal);

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

    // Applying a script twice is not idempotent, and the second run has to fail cleanly rather than
    // leaving the target half-changed.
    [SkippableFact]
    public async Task Re_running_a_committed_script_fails_without_partial_damage()
    {
        Skip.IfNot(_fixture.Available, $"LocalDB is not available: {_fixture.UnavailableReason}");

        const string scratch = "ExecTwice";
        var connectionString = await _fixture.CreateScratchTargetAsync(scratch);

        try
        {
            var source = await new SqlServerSchemaReader(
                LocalDbFixture.ConnectionStringFor(LocalDbFixture.SourceDatabase)).ReadAsync();
            var target = await new SqlServerSchemaReader(connectionString).ReadAsync();
            var script = new TSqlEmitter().Emit(source, target, new SchemaComparer().Compare(source, target));
            var sql = script.ToSql();

            var executor = new SqlServerScriptExecutor();
            var first = await executor.ExecuteAsync(connectionString, sql, script.Count);
            Assert.Equal(ApplyOutcome.Committed, first.Outcome);

            var second = await executor.ExecuteAsync(connectionString, sql, script.Count);
            Assert.Equal(ApplyOutcome.RolledBack, second.Outcome);

            var after = await new SqlServerSchemaReader(connectionString).ReadAsync();
            Assert.Empty(new SchemaComparer().Compare(source, after).Differing);
        }
        finally
        {
            await _fixture.DropScratchAsync(scratch);
        }
    }
}
