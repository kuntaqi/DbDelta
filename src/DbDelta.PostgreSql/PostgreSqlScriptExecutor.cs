using DbDelta.Core.Apply;
using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

// IScriptExecutor is wired apart from IDatabaseProvider, so a second engine needs two registrations rather
// than one. The stub carries an executor too, so the apply-path shape is complete even though the API never
// wires it — the one-provider wiring in Program.cs is a separate gap noted in the TASK doc.
public sealed class PostgreSqlScriptExecutor : IScriptExecutor
{
    public Task<ApplyResult> ExecuteAsync(
        string connectionString,
        SyncScript script,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlScriptExecutor)}.{nameof(ExecuteAsync)}");
}
