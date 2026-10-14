using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlScriptEmitter : IScriptEmitter
{
    public SyncScript Emit(
        DatabaseSchema source,
        DatabaseSchema target,
        SchemaDiff diff,
        IReadOnlySet<ObjectIdentity>? include = null) =>
        throw StubGap.For($"{nameof(PostgreSqlScriptEmitter)}.{nameof(Emit)}");
}
