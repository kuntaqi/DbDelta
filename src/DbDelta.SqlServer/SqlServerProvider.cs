using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Providers;
using DbDelta.Core.Scripting;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerProvider : IDatabaseProvider
{
    public string Key => "sqlserver";

    public IIdentifierQuoter Quoter => SqlServerQuoter.Instance;

    public ISchemaReader CreateSchemaReader(string connectionString) =>
        new SqlServerSchemaReader(connectionString);

    public IScriptEmitter CreateScriptEmitter() => new TSqlEmitter();

    public IVolumeReader CreateVolumeReader(string connectionString) =>
        new SqlServerVolumeReader(connectionString);

    public IRowHashReader CreateRowHashReader(string connectionString) =>
        new SqlServerRowHashReader(connectionString);

    public IRowDetailReader CreateRowDetailReader(string connectionString) =>
        new SqlServerRowDetailReader(connectionString);

    public IRowByValueReader CreateRowByValueReader(string connectionString) =>
        new SqlServerRowByValueReader(connectionString);

    public IDataScriptEmitter CreateDataScriptEmitter() => new TSqlDataEmitter();

    public ITableFingerprintReader CreateFingerprintReader(string connectionString) =>
        new SqlServerFingerprintReader(connectionString);

    public IKeyUniquenessChecker CreateKeyUniquenessChecker(string connectionString) =>
        new SqlServerKeyUniquenessChecker(connectionString);

    public ICollationFactReader CreateCollationFactReader(string connectionString) =>
        new SqlServerCollationFactReader(connectionString);

    public async Task<ServerInfo> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadServerInfoAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DatabaseSummary>> ListDatabasesAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ReadDatabasesAsync(connection, CatalogQueries.Databases, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is PermissionDenied)
        {
            // A login that can see the databases but not the server's file metadata still gets the list.
            return await ReadDatabasesAsync(connection, CatalogQueries.DatabasesWithoutSizes, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<DatabaseDetail> DescribeDatabaseAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var name = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new SqlCommand(CatalogQueries.DatabaseDetail, connection);
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
        catch (SqlException ex)
        {
            // Offline, restoring, or simply not ours to open. Which one is the useful part, so the message
            // is kept rather than the row being dropped.
            return new DatabaseDetail(name, null, 0, 0, 0, ex.Message);
        }
    }

    private const int PermissionDenied = 229;

    private static async Task<List<DatabaseSummary>> ReadDatabasesAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var databases = new List<DatabaseSummary>();

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // By name, not by position: the two queries behind this differ, and a record whose fields do not
            // run in the same order as the SELECT is exactly how sizes end up in the read-only flag.
            databases.Add(new DatabaseSummary(
                reader.GetString(reader.GetOrdinal("Name")),
                reader.GetString(reader.GetOrdinal("State")),
                reader.GetString(reader.GetOrdinal("RecoveryModel")),
                reader.GetInt64(reader.GetOrdinal("DataBytes")),
                reader.GetInt64(reader.GetOrdinal("LogBytes")),
                reader.GetBoolean(reader.GetOrdinal("IsReadOnly")),
                reader.GetBoolean(reader.GetOrdinal("Accessible"))));
        }

        return databases;
    }

    internal static async Task<ServerInfo> ReadServerInfoAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(CatalogQueries.Probe, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The server did not return its properties.");
        }

        return new ServerInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }
}
