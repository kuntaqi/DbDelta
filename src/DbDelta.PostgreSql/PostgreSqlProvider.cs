using DbDelta.Core.Apply;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;
using Npgsql;

namespace DbDelta.PostgreSql;

// A complete IDatabaseProvider: the quoter, schema reader, schema and data script emitters, script executor,
// the data-compare read path (row hash/detail/by-value, fingerprint, key uniqueness, volume), the collation
// fact reader, and the server-level probe/list/describe methods. The one soft spot is collation sensitivity,
// which CollationFact cannot express for PostgreSQL (see PostgreSqlCollationFactReader).
// See docs/TASK-postgresql-schema-reader.md, docs/TASK-postgresql-write-path.md,
// docs/TASK-postgresql-data-path.md and docs/TASK-api-provider-selection.md.
public sealed class PostgreSqlProvider : IDatabaseProvider
{
    public string Key => "postgresql";

    public IIdentifierQuoter Quoter => PostgreSqlQuoter.Instance;

    public ISchemaReader CreateSchemaReader(string connectionString) =>
        new PostgreSqlSchemaReader(connectionString);

    public IScriptEmitter CreateScriptEmitter() => new PostgreSqlScriptEmitter();

    public IScriptExecutor CreateScriptExecutor() => new PostgreSqlScriptExecutor();

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

    public async Task<ServerInfo> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(PgCatalogQueries.ServerProbe, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new ServerInfo(
            connection.Host ?? string.Empty,
            reader.GetString(0),
            reader.GetString(1),
            "PostgreSQL",
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
    }

    public async Task<IReadOnlyList<DatabaseSummary>> ListDatabasesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(PgCatalogQueries.Databases, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var databases = new List<DatabaseSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            databases.Add(new DatabaseSummary(
                reader.GetString(0),
                reader.GetString(1),
                // PostgreSQL has no recovery model, and a database read-only flag is a per-session GUC, not a
                // catalog property; both are left empty/false rather than invented.
                string.Empty,
                reader.GetInt64(2),
                0,
                false,
                reader.GetBoolean(3)));
        }

        return databases;
    }

    public async Task<DatabaseDetail> DescribeDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database ?? string.Empty;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(PgCatalogQueries.DatabaseDetail, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new DatabaseDetail(name, null, 0, 0, 0, "The database returned no properties.");
            }

            return new DatabaseDetail(
                name,
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                null);
        }
        catch (PostgresException ex)
        {
            return new DatabaseDetail(name, null, 0, 0, 0, ex.MessageText);
        }
    }
}
