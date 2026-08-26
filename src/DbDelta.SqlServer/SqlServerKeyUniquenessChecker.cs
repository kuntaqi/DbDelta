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

    // COUNT(DISTINCT ...) refuses these outright, so probing them would fail the whole query rather
    // than one column.
    private static readonly HashSet<string> NotProbeable =
        new(StringComparer.OrdinalIgnoreCase) { "text", "ntext", "image", "xml", "geography", "geometry", "hierarchyid" };

    private const int MaxProbedColumns = 40;

    // Every candidate measured in one scan. Asking the user to guess a column and wait for a rejection
    // each time is the wrong shape when a table can have forty columns.
    public async Task<TableUniquenessProfile> ProfileAsync(
        TableDefinition table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(candidates);

        var quoter = SqlServerQuoter.Instance;

        var probeable = candidates
            .Select(name => table.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<ColumnDefinition>()
            .Where(c => !NotProbeable.Contains(c.DataType.Name))
            .Take(MaxProbedColumns)
            .ToList();

        if (probeable.Count == 0)
        {
            return new TableUniquenessProfile
            {
                RowCount = 0,
                Columns = [],
                WasProbed = false,
                Problem = "No column in this table can be tested for uniqueness."
            };
        }

        var projections = probeable.SelectMany((c, i) => new[]
        {
            $"COUNT(DISTINCT {quoter.Quote(c.Name)}) AS [d{i}]",
            $"SUM(CASE WHEN {quoter.Quote(c.Name)} IS NULL THEN 1 ELSE 0 END) AS [z{i}]"
        });

        var sql = $"""
            SELECT COUNT_BIG(*) AS [n],
                   {string.Join(",\n                   ", projections)}
            FROM {quoter.Qualify(table.Identity)};
            """;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new TableUniquenessProfile { RowCount = 0, Columns = [] };
            }

            var columns = probeable.Select((c, i) => new ColumnUniqueness(
                c.Name,
                reader.IsDBNull(1 + (i * 2)) ? 0 : reader.GetInt32(1 + (i * 2)),
                reader.IsDBNull(2 + (i * 2)) ? 0 : reader.GetInt32(2 + (i * 2)))).ToList();

            return new TableUniquenessProfile { RowCount = reader.GetInt64(0), Columns = columns };
        }
        catch (SqlException ex)
        {
            // A profile that cannot be taken must not stop the picker from being usable by hand.
            return new TableUniquenessProfile
            {
                RowCount = 0,
                Columns = [],
                WasProbed = false,
                Problem = ex.Message
            };
        }
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
