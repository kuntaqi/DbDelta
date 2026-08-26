using DbDelta.Core.Comparison;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Providers;

public interface IScriptEmitter
{
    SyncScript Emit(
        DatabaseSchema source,
        DatabaseSchema target,
        SchemaDiff diff,
        IReadOnlySet<ObjectIdentity>? include = null);
}
