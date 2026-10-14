using System.Text;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Npgsql;

namespace DbDelta.PostgreSql;

// A screening pass that answers "does this table differ" without moving row data to the client, mirroring
// SqlServerFingerprintReader. SQL Server's BINARY_CHECKSUM / CHECKSUM_AGG have no PostgreSQL equivalent, so a
// per-row hashtext() is aggregated three independent ways — count, an order-independent XOR (bit_xor) and a
// sum — so an accidental collision across all three is vanishingly unlikely. Tables are batched as separate
// statements so one failing table takes only its own result set down.
public sealed class PostgreSqlFingerprintReader : ITableFingerprintReader
{
    private const int TablesPerQuery = 12;

    private readonly string _connectionString;

    public PostgreSqlFingerprintReader(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<TableFingerprint>> ReadAsync(
        IReadOnlyList<FingerprintRequest> tables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (tables.Count == 0)
        {
            return [];
        }

        var results = new List<TableFingerprint>();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in Chunk(tables, TablesPerQuery))
        {
            var sql = new StringBuilder();
            foreach (var request in batch)
            {
                sql.Append(BuildQuery(request)).Append('\n');
            }

            await using var command = new NpgsqlCommand(sql.ToString(), connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            foreach (var request in batch)
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(new TableFingerprint(
                        request.Table.Identity, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return results;
    }

    private static string BuildQuery(FingerprintRequest request)
    {
        var columns = request.KeyColumns.Concat(request.Columns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var concat = PgRowDigest.Concatenation(columns);
        var qualified = PostgreSqlQuoter.Instance.Qualify(request.Table.Identity);

        return $"""
            SELECT count(*)::bigint AS n,
                   coalesce(bit_xor(hashtext({concat})), 0)::bigint AS x,
                   coalesce(sum(hashtext({concat})), 0)::bigint AS s
            FROM {qualified};
            """;
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }
}
