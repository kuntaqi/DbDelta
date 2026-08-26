using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerKeyUniquenessChecker : IKeyUniquenessChecker
{
    private readonly string _connectionString;

    public SqlServerKeyUniquenessChecker(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<KeyUniqueness> CheckAsync(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keyColumns);

        if (keyColumns.Count == 0)
        {
            throw new InvalidOperationException("A key needs at least one column.");
        }

        var quoter = SqlServerQuoter.Instance;
        var quoted = keyColumns.Select(quoter.Quote).ToList();

        // COUNT(DISTINCT ...) takes one expression, so the columns are concatenated the same way the
        // compare builds its key — the same digest, so the same notion of "distinct".
        var digest = RowDigestBuilder.Concatenation(table, keyColumns);
        var nullTest = string.Join(" OR ", quoted.Select(c => $"{c} IS NULL"));

        var sql = $"""
            SELECT COUNT_BIG(*) AS [n],
                   COUNT(DISTINCT {digest}) AS [d],
                   SUM(CASE WHEN {nullTest} THEN 1 ELSE 0 END) AS [z]
            FROM {quoter.Qualify(table.Identity)};
            """;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new KeyUniqueness(0, 0, 0);
        }

        return new KeyUniqueness(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
    }
}
