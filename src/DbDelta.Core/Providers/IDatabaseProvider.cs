using DbDelta.Core.Data;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Providers;

public interface IDatabaseProvider
{
    string Key { get; }

    IIdentifierQuoter Quoter { get; }

    ISchemaReader CreateSchemaReader(string connectionString);

    IScriptEmitter CreateScriptEmitter();

    IVolumeReader CreateVolumeReader(string connectionString);

    IRowHashReader CreateRowHashReader(string connectionString);

    IRowDetailReader CreateRowDetailReader(string connectionString);

    IDataScriptEmitter CreateDataScriptEmitter();

    ITableFingerprintReader CreateFingerprintReader(string connectionString);

    Task<ServerInfo> ProbeAsync(string connectionString, CancellationToken cancellationToken = default);
}
