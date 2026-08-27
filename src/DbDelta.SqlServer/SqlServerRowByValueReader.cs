using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerRowByValueReader : IRowByValueReader
{
    // Keys go in as literals rather than parameters, written by the same TSqlLiteral the INSERT
    // statements use. A parameter would arrive as nvarchar and lean on implicit conversion, which is
    // wrong for a binary key and forces a scan on a numeric one; the literal is typed by the column.
    private const int KeysPerQuery = 500;

    private readonly string _connectionString;

    public SqlServerRowByValueReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

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

        if (keys.Count == 0 || keyColumns.Count == 0 || columns.Count == 0)
        {
            return [];
        }

        var quoter = SqlServerQuoter.Instance;
        var selected = columns.Concat(keyColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<RowValues>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in keys.Chunk(KeysPerQuery))
        {
            var sql = $"""
                SELECT {string.Join(", ", selected.Select(quoter.Quote))}
                FROM {quoter.Qualify(table.Identity)}
                WHERE {Predicate(table, keyColumns, batch)};
                """;

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < selected.Count; i++)
                {
                    values[selected[i]] = reader.IsDBNull(i) ? null : SqlValueText.Of(reader.GetValue(i));
                }

                var key = new ParentKey(keyColumns.Select(c => values.GetValueOrDefault(c)).ToList());
                rows.Add(new RowValues(key.Canonical, values));
            }
        }

        return rows;
    }

    // A single-column key becomes IN (...), which the optimiser turns into a seek. Composite keys have
    // to be OR-ed tuple by tuple, so they are the reason for the batch size rather than the IN limit.
    private static string Predicate(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<ParentKey> keys)
    {
        var quoter = SqlServerQuoter.Instance;

        if (keyColumns.Count == 1)
        {
            var column = keyColumns[0];
            var literals = keys.Select(k => TSqlLiteral.For(table, column, k.Values[0]));

            return $"{quoter.Quote(column)} IN ({string.Join(", ", literals)})";
        }

        var tuples = keys.Select(key => "("
            + string.Join(" AND ", keyColumns.Select((column, i) =>
                $"{quoter.Quote(column)} = {TSqlLiteral.For(table, column, key.Values[i])}"))
            + ")");

        return string.Join("\n   OR ", tuples);
    }
}
