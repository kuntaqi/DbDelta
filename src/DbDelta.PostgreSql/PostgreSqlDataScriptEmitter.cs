using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlDataScriptEmitter : IDataScriptEmitter
{
    public IReadOnlyList<ScriptStep> Emit(TableDataChanges changes, long maxInlineBytes) =>
        throw StubGap.For($"{nameof(PostgreSqlDataScriptEmitter)}.{nameof(Emit)}");
}
