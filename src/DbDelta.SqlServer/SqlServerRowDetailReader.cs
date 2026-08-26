using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerRowDetailReader : IRowDetailReader
{
    // Enough to fill a screen. The point of the two-pass design is that full rows are only fetched
    // for what is actually being shown, so this cap is the mechanism, not a limitation.
    public const int MaxKeysPerFetch = 500;

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

        var wanted = keys.Take(MaxKeysPerFetch).ToList();
        var quoter = SqlServerQuoter.Instance;
        var keyExpression = RowDigestBuilder.Concatenation(table, request.KeyColumns);
        var parameters = string.Join(", ", wanted.Select((_, i) => $"@k{i}"));

        // Selecting by the same canonical key expression the compare produced keeps this addressing
        // rows exactly the way the merge join identified them, rather than re-deriving the key.
        var sql = $"""
            SELECT {keyExpression} AS [k],
                   {string.Join(",\n                   ", columns.Select(c => quoter.Quote(c)))}
            FROM {quoter.Qualify(table.Identity)}
            WHERE {keyExpression} IN ({parameters});
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        for (var i = 0; i < wanted.Count; i++)
        {
            command.Parameters.AddWithValue($"@k{i}", wanted[i]);
        }

        var rows = new List<RowValues>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < columns.Count; i++)
            {
                var ordinal = i + 1;
                values[columns[i]] = reader.IsDBNull(ordinal)
                    ? null
                    : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
            }

            rows.Add(new RowValues(reader.GetString(0), values));
        }

        return rows;
    }
}
