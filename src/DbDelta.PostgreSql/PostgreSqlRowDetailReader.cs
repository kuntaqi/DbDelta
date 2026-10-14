using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Npgsql;

namespace DbDelta.PostgreSql;

// Fetches full column values for a set of canonical keys the merge join already produced. Mirrors
// SqlServerRowDetailReader: the keys are addressed through a VALUES-constructor join on the very same digest
// expression that produced them, not an IN list — the join evaluates the digest once per row, an IN list once
// per key per row. Values come back as ::text, the same terms the digest and the staging cast-back use.
public sealed class PostgreSqlRowDetailReader : IRowDetailReader
{
    private const int KeysPerQuery = 500;

    private readonly string _connectionString;

    public PostgreSqlRowDetailReader(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<RowValues>> FetchAsync(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keys);

        var distinctKeys = keys.Distinct(StringComparer.Ordinal).ToList();
        if (distinctKeys.Count == 0)
        {
            return [];
        }

        var keyExpression = PgRowDigest.Concatenation(request.KeyColumns);
        var qualified = PostgreSqlQuoter.Instance.Qualify(table.Identity);
        var projection = string.Join(",\n       ", columns.Select(c => $"({PostgreSqlQuoter.Instance.Quote(c)})::text"));

        var results = new List<RowValues>();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in Chunk(distinctKeys, KeysPerQuery))
        {
            var values = string.Join(", ", batch.Select((_, i) => $"(@k{i})"));
            var sql = $"""
                SELECT {keyExpression} AS k,
                       {projection}
                FROM {qualified}
                JOIN (VALUES {values}) AS dbdelta_key(k) ON dbdelta_key.k = {keyExpression};
                """;

            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
            for (var i = 0; i < batch.Count; i++)
            {
                command.Parameters.AddWithValue($"k{i}", batch[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Count; i++)
                {
                    var ordinal = i + 1;
                    row[columns[i]] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                }

                results.Add(new RowValues(reader.GetString(0), row));
            }
        }

        return results;
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }
}
