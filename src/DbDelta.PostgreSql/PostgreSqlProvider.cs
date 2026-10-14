using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

// A second IDatabaseProvider whose only job is to prove the abstraction holds: it satisfies every member of
// the provider surface against an unchanged Core. The Quoter is real; everything else throws, because a
// compile proof needs no catalog reads or emitted SQL. See docs/TASK-postgresql-provider-stub.md.
public sealed class PostgreSqlProvider : IDatabaseProvider
{
    public string Key => "postgresql";

    public IIdentifierQuoter Quoter => PostgreSqlQuoter.Instance;

    public ISchemaReader CreateSchemaReader(string connectionString) =>
        new PostgreSqlSchemaReader(connectionString);

    public IScriptEmitter CreateScriptEmitter() => new PostgreSqlScriptEmitter();

    public IVolumeReader CreateVolumeReader(string connectionString) =>
        new PostgreSqlVolumeReader(connectionString);

    public IRowHashReader CreateRowHashReader(string connectionString) =>
        new PostgreSqlRowHashReader(connectionString);

    public IRowDetailReader CreateRowDetailReader(string connectionString) =>
        new PostgreSqlRowDetailReader(connectionString);

    public IRowByValueReader CreateRowByValueReader(string connectionString) =>
        new PostgreSqlRowByValueReader(connectionString);

    public IDataScriptEmitter CreateDataScriptEmitter() => new PostgreSqlDataScriptEmitter();

    public ITableFingerprintReader CreateFingerprintReader(string connectionString) =>
        new PostgreSqlFingerprintReader(connectionString);

    public IKeyUniquenessChecker CreateKeyUniquenessChecker(string connectionString) =>
        new PostgreSqlKeyUniquenessChecker(connectionString);

    public ICollationFactReader CreateCollationFactReader(string connectionString) =>
        new PostgreSqlCollationFactReader(connectionString);

    public Task<ServerInfo> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For(nameof(ProbeAsync));

    public Task<IReadOnlyList<DatabaseSummary>> ListDatabasesAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For(nameof(ListDatabasesAsync));

    public Task<DatabaseDetail> DescribeDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For(nameof(DescribeDatabaseAsync));
}
