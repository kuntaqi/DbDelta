using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerRowDetailReader : IRowDetailReader
{
    // A batch size, not a cap. It used to be a cap — keys past the first 500 were dropped — which was
    // harmless for a screen showing 200 rows and silently wrong for a script, which needs every row it
    // will write. SQL Server allows 2100 parameters per command, so this sits well under it.
    public const int KeysPerQuery = 500;

    private readonly string _connectionString;

    public SqlServerRowDetailReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

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

        if (keys.Count == 0 || columns.Count == 0)
        {
            return [];
        }

        var quoter = SqlServerQuoter.Instance;
        var keyExpression = RowDigestBuilder.Concatenation(table, request.KeyColumns);
        var rows = new List<RowValues>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in keys.Chunk(KeysPerQuery))
        {
            var parameters = string.Join(", ", batch.Select((_, i) => $"@k{i}"));

            // Selecting by the same canonical key expression the compare produced keeps this addressing
            // rows exactly the way the merge join identified them, rather than re-deriving the key.
            var sql = $"""
                SELECT {keyExpression} AS [k],
                       {string.Join(",\n                       ", columns.Select(c => quoter.Quote(c)))}
                FROM {quoter.Qualify(table.Identity)}
                WHERE {keyExpression} IN ({parameters});
                """;

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
            for (var i = 0; i < batch.Length; i++)
            {
                command.Parameters.AddWithValue($"@k{i}", batch[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                for (var i = 0; i < columns.Count; i++)
                {
                    var ordinal = i + 1;
                    values[columns[i]] = reader.IsDBNull(ordinal) ? null : SqlValueText.Of(reader.GetValue(ordinal));
                }

                rows.Add(new RowValues(reader.GetString(0), values));
            }
        }

        return rows;
    }
}
