using DbDelta.Core.Apply;
using DbDelta.Core.Scripting;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

public sealed class PostgreSqlScriptExecutorTests
{
    // No database needed: a script carrying rows is refused before the executor opens a connection, because
    // the PostgreSQL data path is a later increment. This pins that the schema-only scope is enforced, not
    // assumed.
    [Fact]
    public async Task Execute_blocks_a_script_that_carries_rows()
    {
        var load = new BulkLoad("app.t", "stg", "t.dat", ["id"], [["1"]]);
        var script = new SyncScript
        {
            Header = "-- test",
            Steps = [new ScriptStep(ScriptPhase.DataUpserts, "load", "-- rows", load)],
        };

        var result = await new PostgreSqlScriptExecutor().ExecuteAsync("Host=localhost;Database=app", script);

        Assert.Equal(ApplyOutcome.Blocked, result.Outcome);
        Assert.NotEmpty(result.Blockers);
    }
}
