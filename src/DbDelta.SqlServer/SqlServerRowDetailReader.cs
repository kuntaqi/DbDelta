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

        // Distinct, because the keys arrive from a set on the compare side but nothing in the signature
        // says so, and the join below would return a row once per matching entry where IN returned it once.
        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();

        foreach (var batch in wanted.Chunk(KeysPerQuery))
        {
            var keyRows = string.Join(", ", batch.Select((_, i) => $"(@k{i})"));

            // Selecting by the same canonical key expression the compare produced keeps this addressing
            // rows exactly the way the merge join identified them, rather than re-deriving the key.
            //
            // Joined to the keys rather than `WHERE <expression> IN (@k0…@k499)`, and the difference is
            // not stylistic. An IN list expands to one comparison per element, and each one re-evaluates
            // the key expression — which is an nvarchar(max) CONCAT, so 1200 rows against 500 keys meant
            // 600,000 LOB concatenations. Measured on the 1200-row fixture: 17.5 seconds for the IN form,
            // 54ms for this one. The join evaluates the expression once per row and hashes it.
            //
            // Bounding the expression to nvarchar(400) would also have fixed it (293ms) but is not worth
            // the risk: the expression produces the key that identifies a row, and a truncated key silently
            // matches the wrong one. Nothing about the digest changes here — only how it is compared.
            //
            // A table value constructor takes at most 1000 rows, so KeysPerQuery cannot be raised past it
            // without changing this shape again.
            var sql = $"""
                SELECT {keyExpression} AS [k],
                       {string.Join(",\n                       ", columns.Select(c => quoter.Quote(c)))}
                FROM {quoter.Qualify(table.Identity)}
                JOIN (VALUES {keyRows}) AS [dbdelta_key]([k]) ON [dbdelta_key].[k] = {keyExpression};
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
