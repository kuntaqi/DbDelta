using DbDelta.Core.Apply;
using DbDelta.PostgreSql;

namespace DbDelta.PostgreSql.Tests;

public sealed class PostgreSqlScriptExecutorTests
{
    [Fact]
    public void Execute_refuses_because_the_provider_is_a_stub()
    {
        IScriptExecutor executor = new PostgreSqlScriptExecutor();
        Assert.Throws<NotSupportedException>(() => { _ = executor.ExecuteAsync("Host=localhost", null!); });
    }
}
