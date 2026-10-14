using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;

namespace DbDelta.PostgreSql;

// A second IDatabaseProvider, filled in incrementally. Real so far: the quoter, the schema reader, the schema
// and data script emitters, and the data-compare read path (row hash/detail/by-value, fingerprint, key
// uniqueness, volume). Still stubbed: the collation fact reader and the three server-level probe/list/describe
// methods. The script executor is wired apart from the provider (see IDatabaseProvider), so the API does not
// reach the PostgreSQL executor yet.
// See docs/TASK-postgresql-schema-reader.md, docs/TASK-postgresql-write-path.md and docs/TASK-postgresql-data-path.md.
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
