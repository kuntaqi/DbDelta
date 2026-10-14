using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Npgsql;

namespace DbDelta.PostgreSql;

// Table footprints from the catalog's maintained estimates rather than a scan, mirroring
// SqlServerVolumeReader. Row counts come from pg_class.reltuples (an estimate, -1 until the table is analysed,
// clamped to 0); sizes from pg_relation_size / pg_indexes_size. PostgreSQL has no per-database log file — WAL is
// cluster-wide — so LogBytes is 0, and DataBytes is the whole database size.
public sealed class PostgreSqlVolumeReader : IVolumeReader
{
    private const string TablesQuery = """
        SELECT n.nspname AS schema_name, c.relname AS table_name,
               (case when c.reltuples < 0 then 0 else c.reltuples end)::bigint AS rows,
               pg_relation_size(c.oid)::bigint AS data_bytes,
               pg_indexes_size(c.oid)::bigint AS index_bytes
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
          AND n.nspname NOT LIKE 'pg_temp%'
        ORDER BY n.nspname, c.relname;
        """;

    private const string DatabaseSizeQuery = "SELECT pg_database_size(current_database())::bigint;";

    private readonly string _connectionString;

    public PostgreSqlVolumeReader(string connectionString) => _connectionString = connectionString;

    public async Task<DatabaseVolume> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = new List<TableVolume>();
        long totalRows = 0;

        await using (var command = new NpgsqlCommand(TablesQuery, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var rows = reader.GetInt64(2);
                totalRows += rows;
                tables.Add(new TableVolume(
                    new ObjectIdentity(ObjectType.Table, reader.GetString(0), reader.GetString(1)),
                    rows, reader.GetInt64(3), reader.GetInt64(4)));
            }
        }

        long dataBytes;
        await using (var command = new NpgsqlCommand(DatabaseSizeQuery, connection))
        {
            dataBytes = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        return new DatabaseVolume
        {
            DataBytes = dataBytes,
            LogBytes = 0,
            TotalRows = totalRows,
            Tables = tables,
        };
    }
}
