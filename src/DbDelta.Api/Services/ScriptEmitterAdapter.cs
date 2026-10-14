using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.Api.Services;

// Keeps the provider's emitter behind one call so the service never reaches for an engine-specific type. The
// provider it wraps is chosen per session, so this emits T-SQL or PostgreSQL DDL depending on which built it.
public sealed class ScriptEmitterAdapter
{
    private readonly IDatabaseProvider _provider;

    public ScriptEmitterAdapter(IDatabaseProvider provider) => _provider = provider;

    public SyncScript Emit(CompareSession session, IReadOnlySet<ObjectIdentity> include) =>
        _provider.CreateScriptEmitter().Emit(session.Source, session.Target, session.Diff, include);
}
