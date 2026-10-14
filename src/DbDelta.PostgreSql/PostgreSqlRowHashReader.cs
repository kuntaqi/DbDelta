using System.Runtime.CompilerServices;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Npgsql;

namespace DbDelta.PostgreSql;

// Streams (key, hash) pairs for the client merge join, one row at a time, mirroring SqlServerRowHashReader.
// The outer ORDER BY uses COLLATE "C" — PostgreSQL's byte-ordinal collation — because the merge join compares
// keys with StringComparer.Ordinal, and only a byte-order sort agrees with that; the default collation would
// desynchronise the merge (DataComparer.Guard throws if it does).
public sealed class PostgreSqlRowHashReader : IRowHashReader
{
    private readonly string _connectionString;

    public PostgreSqlRowHashReader(string connectionString) => _connectionString = connectionString;

    public async IAsyncEnumerable<KeyHashRow> StreamAsync(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> comparedColumns,
        RowSetSide side,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(BuildQuery(table, request, comparedColumns, side), connection)
        {
            CommandTimeout = 0,
        };

        if (side == RowSetSide.Source && request.Mode == TableDataMode.TopN)
        {
            command.Parameters.AddWithValue("top", request.TopCount);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new KeyHashRow(reader.GetString(0), reader.GetString(1));
        }
    }

    private static string BuildQuery(
        TableDefinition table, DataCompareRequest request, IReadOnlyList<string> comparedColumns, RowSetSide side)
    {
        var key = PgRowDigest.Concatenation(request.KeyColumns);
        var hash = PgRowDigest.Hash(comparedColumns);
        var qualified = PostgreSqlQuoter.Instance.Qualify(table.Identity);

        var where = request.Mode == TableDataMode.Filter && !string.IsNullOrWhiteSpace(request.FilterPredicate)
            ? $"\n    WHERE {request.FilterPredicate}"
            : string.Empty;

        // Top-N applies to the source only: taking the target's own top N would compare two unrelated windows.
        // The inner ORDER BY makes the same N rows come back every run.
        var inner = string.Empty;
        var limit = string.Empty;
        if (side == RowSetSide.Source && request.Mode == TableDataMode.TopN)
        {
            inner = $"\n    ORDER BY {string.Join(", ", request.KeyColumns.Select(PostgreSqlQuoter.Instance.Quote))}";
            limit = "\n    LIMIT @top";
        }

        return $"""
            SELECT k, h FROM (
                SELECT {key} AS k,
                       {hash} AS h
                FROM {qualified}{where}{inner}{limit}
            ) x
            ORDER BY k COLLATE "C";
            """;
    }
}
