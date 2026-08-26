using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.Api.Services;

// Keeps the provider's emitter behind one call so the service never reaches for a SQL Server type.
public sealed class TSqlEmitterAdapter
{
    private readonly IDatabaseProvider _provider;

    public TSqlEmitterAdapter(IDatabaseProvider provider) => _provider = provider;

    public SyncScript Emit(CompareSession session, IReadOnlySet<ObjectIdentity> include) =>
        _provider.CreateScriptEmitter().Emit(session.Source, session.Target, session.Diff, include);
}
