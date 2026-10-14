using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Npgsql;

namespace DbDelta.PostgreSql;

// Verifies a key is unique before the merge join is asked to trust it, mirroring SqlServerKeyUniquenessChecker.
// ProfileAsync measures every candidate column in one scan so the picker offers what actually works; CheckAsync
// validates a chosen key through the same digest the compare uses, so "distinct" means the same thing.
public sealed class PostgreSqlKeyUniquenessChecker : IKeyUniquenessChecker
{
    private const int MaxProbedColumns = 40;

    // count(distinct …) needs an equality operator; json and xml have none.
    private static readonly HashSet<string> NotProbeable =
        new(StringComparer.OrdinalIgnoreCase) { "json", "xml" };

    private readonly string _connectionString;

    public PostgreSqlKeyUniquenessChecker(string connectionString) => _connectionString = connectionString;

    public async Task<TableUniquenessProfile> ProfileAsync(
        TableDefinition table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(candidates);

        var types = table.Columns.ToDictionary(c => c.Name, c => c.DataType.Name, StringComparer.OrdinalIgnoreCase);
        var probeable = candidates
            .Where(c => !types.TryGetValue(c, out var type) || !NotProbeable.Contains(type))
            .Take(MaxProbedColumns)
            .ToList();

        if (probeable.Count == 0)
        {
            return new TableUniquenessProfile { RowCount = 0, Columns = [], WasProbed = false, Problem = "No probeable columns." };
        }

        var projections = string.Join(",\n       ", probeable.Select((c, i) =>
        {
            var col = PostgreSqlQuoter.Instance.Quote(c);
            return $"count(distinct {col})::bigint AS d{i}, sum(case when {col} is null then 1 else 0 end)::bigint AS z{i}";
        }));

        var sql = $"SELECT count(*)::bigint AS n,\n       {projections}\nFROM {PostgreSqlQuoter.Instance.Qualify(table.Identity)};";

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new TableUniquenessProfile { RowCount = 0, Columns = [], WasProbed = false, Problem = "No result." };
            }

            var rowCount = reader.GetInt64(0);
            var columns = new List<ColumnUniqueness>();
            for (var i = 0; i < probeable.Count; i++)
            {
                columns.Add(new ColumnUniqueness(probeable[i], reader.GetInt64(1 + (i * 2)), reader.GetInt64(2 + (i * 2))));
            }

            return new TableUniquenessProfile { RowCount = rowCount, Columns = columns };
        }
        catch (PostgresException ex)
        {
            return new TableUniquenessProfile { RowCount = 0, Columns = [], WasProbed = false, Problem = ex.MessageText };
        }
    }

    public async Task<KeyUniqueness> CheckAsync(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keyColumns);

        var digest = PgRowDigest.Concatenation(keyColumns);
        var nullCheck = string.Join(" OR ", keyColumns.Select(c => $"{PostgreSqlQuoter.Instance.Quote(c)} IS NULL"));
        var sql = $"""
            SELECT count(*)::bigint AS n,
                   count(distinct {digest})::bigint AS d,
                   sum(case when {nullCheck} then 1 else 0 end)::bigint AS z
            FROM {PostgreSqlQuoter.Instance.Qualify(table.Identity)};
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new KeyUniqueness(0, 0, 0);
        }

        return new KeyUniqueness(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
