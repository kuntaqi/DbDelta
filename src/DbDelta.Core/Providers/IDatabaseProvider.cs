using DbDelta.Core.Apply;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Scripting;

namespace DbDelta.Core.Providers;

public interface IDatabaseProvider
{
    string Key { get; }

    IIdentifierQuoter Quoter { get; }

    ISchemaReader CreateSchemaReader(string connectionString);

    IScriptEmitter CreateScriptEmitter();

    // The executor belongs to the provider, like the emitter: applying a script is as engine-specific as
    // writing it. Kept here rather than resolved separately so a caller that has the provider for an engine
    // has its executor too, with no second lookup that could disagree about which engine.
    IScriptExecutor CreateScriptExecutor();

    IVolumeReader CreateVolumeReader(string connectionString);

    IRowHashReader CreateRowHashReader(string connectionString);

    IRowDetailReader CreateRowDetailReader(string connectionString);

    IRowByValueReader CreateRowByValueReader(string connectionString);

    IDataScriptEmitter CreateDataScriptEmitter();

    ITableFingerprintReader CreateFingerprintReader(string connectionString);

    IKeyUniquenessChecker CreateKeyUniquenessChecker(string connectionString);

    // Collation facts are a property of the server, not of a database, but they are asked through a
    // connection because that is the only thing this interface has. Names in, meanings out.
    ICollationFactReader CreateCollationFactReader(string connectionString);

    Task<ServerInfo> ProbeAsync(string connectionString, CancellationToken cancellationToken = default);

    // Server-level, not per-database: one connection answers for every database on the instance. A direct
    // method rather than a reader factory for the same reason ProbeAsync is one — there is nothing to keep
    // open between calls.
    Task<IReadOnlyList<DatabaseSummary>> ListDatabasesAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    // Costs one connection per database, which is why it is a separate call the caller opts into rather
    // than something ListDatabasesAsync does on its behalf.
    Task<DatabaseDetail> DescribeDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken = default);
}
