using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerVolumeReader : IVolumeReader
{
    // dm_db_partition_stats holds maintained values, not scans, so this is instant even on a 40 GB
    // database. Row counts there are approximate; exact counts are only worth taking for tables that
    // actually enter a data compare.
    private const string TableVolumes = """
        SELECT
            s.name                                          AS [SchemaName],
            t.name                                          AS [TableName],
            SUM(CASE WHEN p.index_id < 2 THEN p.row_count ELSE 0 END)                    AS [Rows],
            SUM(CASE WHEN p.index_id < 2 THEN p.used_page_count ELSE 0 END) * 8192       AS [DataBytes],
            SUM(CASE WHEN p.index_id >= 2 THEN p.used_page_count ELSE 0 END) * 8192      AS [IndexBytes]
        FROM sys.dm_db_partition_stats p
        JOIN sys.tables t   ON t.object_id = p.object_id
        JOIN sys.schemas s  ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0
        GROUP BY s.name, t.name
        ORDER BY s.name, t.name;
        """;

    private const string FileSizes = """
        SELECT
            SUM(CASE WHEN type_desc = 'ROWS' THEN CONVERT(bigint, size) ELSE 0 END) * 8192 AS [DataBytes],
            SUM(CASE WHEN type_desc = 'LOG'  THEN CONVERT(bigint, size) ELSE 0 END) * 8192 AS [LogBytes]
        FROM sys.database_files;
        """;

    private readonly string _connectionString;

    public SqlServerVolumeReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<DatabaseVolume> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tables = new List<TableVolume>();

        await using (var command = new SqlCommand(TableVolumes, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(new TableVolume(
                    new ObjectIdentity(ObjectType.Table, reader.GetString(0), reader.GetString(1)),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4)));
            }
        }

        long dataBytes = 0;
        long logBytes = 0;

        await using (var command = new SqlCommand(FileSizes, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                dataBytes = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                logBytes = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
            }
        }

        return new DatabaseVolume
        {
            DataBytes = dataBytes,
            LogBytes = logBytes,
            TotalRows = tables.Sum(t => t.RowCount),
            Tables = tables
        };
    }
}
