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

    IVolumeReader CreateVolumeReader(string connectionString);

    IRowHashReader CreateRowHashReader(string connectionString);

    IRowDetailReader CreateRowDetailReader(string connectionString);

    IRowByValueReader CreateRowByValueReader(string connectionString);

    IDataScriptEmitter CreateDataScriptEmitter();

    ITableFingerprintReader CreateFingerprintReader(string connectionString);

    IKeyUniquenessChecker CreateKeyUniquenessChecker(string connectionString);

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
