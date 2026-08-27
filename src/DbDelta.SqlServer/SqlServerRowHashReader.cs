using System.Runtime.CompilerServices;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerRowHashReader : IRowHashReader
{
    // Ordering has to agree with the ordinal comparison the merge join uses. A binary collation is
    // the only one that does; under the server's default case-insensitive collation the two orderings
    // disagree and the merge silently desynchronises.
    private const string BinaryCollation = "Latin1_General_BIN2";

    private readonly string _connectionString;

    public SqlServerRowHashReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async IAsyncEnumerable<KeyHashRow> StreamAsync(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> comparedColumns,
        RowSetSide side,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(comparedColumns);

        if (!request.HasKey)
        {
            throw new InvalidOperationException(
                $"{table.Identity.QualifiedName} has no key columns. Choose them before comparing data.");
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(BuildQuery(table, request, comparedColumns, side), connection);
        command.CommandTimeout = 0;

        if (side == RowSetSide.Source && request.Mode == TableDataMode.TopN)
        {
            command.Parameters.AddWithValue("@top", request.TopCount);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new KeyHashRow(reader.GetString(0), reader.GetString(1));
        }
    }

    internal static string BuildQuery(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> comparedColumns,
        RowSetSide side)
    {
        var quoter = SqlServerQuoter.Instance;
        var key = RowDigestBuilder.Concatenation(table, request.KeyColumns);
        var hash = RowDigestBuilder.HashExpression(table, comparedColumns);

        // Top N applies to the source only. Taking the target's own top N would compare two unrelated
        // windows: if the target is missing rows its window reaches further and the overlap is wrong.
        var top = side == RowSetSide.Source && request.Mode == TableDataMode.TopN ? "TOP (@top) " : string.Empty;

        // The predicate goes in as written, having been held to columns, constants and operators by
        // FilterPredicateValidator before a request carrying one is ever built. It cannot be parameterised:
        // it is an expression, not a value. See that validator for why the check is where it is.
        var where = request.Mode == TableDataMode.Filter && !string.IsNullOrWhiteSpace(request.FilterPredicate)
            ? $"\n    WHERE {request.FilterPredicate}"
            : string.Empty;

        // Top N is ordered by the primary key so the same N rows come back on every run; an unbounded
        // TOP would make a seed irreproducible.
        var inner = top.Length > 0
            ? $"\n    ORDER BY {string.Join(", ", request.KeyColumns.Select(c => quoter.Quote(c)))}"
            : string.Empty;

        return $"""
            SELECT [k], [h]
            FROM (
                SELECT {top}{key} AS [k],
                       {hash} AS [h]
                FROM {quoter.Qualify(table.Identity)}{where}{inner}
            ) AS x
            ORDER BY [k] COLLATE {BinaryCollation};
            """;
    }
}
