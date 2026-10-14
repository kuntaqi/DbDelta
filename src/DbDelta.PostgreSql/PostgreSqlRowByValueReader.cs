using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Npgsql;

namespace DbDelta.PostgreSql;

// Addresses rows by literal column values rather than by the compare's canonical key — parent closure starts
// from a child's foreign-key value, which the merge join never turned into a key. Mirrors
// SqlServerRowByValueReader: a single-column key becomes an IN list, a composite key becomes OR-ed tuples, and
// the values are typed literals (quoted text cast with ::type) rather than parameters, so a numeric or binary
// key is addressed as its own type instead of being coerced from text.
public sealed class PostgreSqlRowByValueReader : IRowByValueReader
{
    private const int KeysPerQuery = 500;

    private readonly string _connectionString;

    public PostgreSqlRowByValueReader(string connectionString) => _connectionString = connectionString;

    public async Task<IReadOnlyList<RowValues>> FetchAsync(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<ParentKey> keys,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(columns);

        if (keys.Count == 0 || keyColumns.Count == 0)
        {
            return [];
        }

        var types = table.Columns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);
        var selected = columns.Concat(keyColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var projection = string.Join(",\n       ", selected.Select(c => $"({PostgreSqlQuoter.Instance.Quote(c)})::text"));
        var qualified = PostgreSqlQuoter.Instance.Qualify(table.Identity);

        var results = new List<RowValues>();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in Chunk(keys, KeysPerQuery))
        {
            var predicate = Predicate(keyColumns, batch, types);
            var sql = $"SELECT {projection}\nFROM {qualified}\nWHERE {predicate};";

            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var values = new List<string?>();
                for (var i = 0; i < selected.Count; i++)
                {
                    row[selected[i]] = reader.IsDBNull(i) ? null : reader.GetString(i);
                }

                foreach (var keyColumn in keyColumns)
                {
                    values.Add(row[keyColumn]);
                }

                results.Add(new RowValues(new ParentKey(values).Canonical, row));
            }
        }

        return results;
    }

    private static string Predicate(
        IReadOnlyList<string> keyColumns, IReadOnlyList<ParentKey> keys, IReadOnlyDictionary<string, DataTypeSpec> types)
    {
        // A key with a NULL cannot be addressed by equality (NULL = NULL is not true), so it is left out. If
        // that leaves nothing, the batch matches no rows.
        var addressable = keys.Where(k => !k.HasNull).ToList();
        if (addressable.Count == 0)
        {
            return "false";
        }

        if (keyColumns.Count == 1)
        {
            var column = PostgreSqlQuoter.Instance.Quote(keyColumns[0]);
            var type = types[keyColumns[0]];
            var literals = addressable.Select(k => PgLiteral.For(type, k.Values[0]));
            return $"{column} IN ({string.Join(", ", literals)})";
        }

        var tuples = addressable.Select(k =>
        {
            var parts = keyColumns.Select((c, i) =>
                $"{PostgreSqlQuoter.Instance.Quote(c)} = {PgLiteral.For(types[c], k.Values[i])}");
            return $"({string.Join(" AND ", parts)})";
        });

        return string.Join("\n   OR ", tuples);
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            yield return items.Skip(i).Take(size).ToList();
        }
    }
}
