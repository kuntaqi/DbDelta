using System.Globalization;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerFingerprintReader : ITableFingerprintReader
{
    // Several tables per round trip, but not all of them: one enormous UNION ALL takes a long time to
    // compile and gives the caller nothing to parallelise.
    private const int TablesPerQuery = 12;

    private readonly string _connectionString;

    public SqlServerFingerprintReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyList<TableFingerprint>> ReadAsync(
        IReadOnlyList<FingerprintRequest> tables,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count == 0)
        {
            return [];
        }

        var byName = tables.ToDictionary(t => t.Table.Identity.QualifiedName, StringComparer.OrdinalIgnoreCase);
        var results = new List<TableFingerprint>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in tables.Chunk(TablesPerQuery))
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = new SqlCommand(BuildQuery(batch), connection) { CommandTimeout = 0 };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            do
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetString(0);
                    if (byName.TryGetValue(name, out var request))
                    {
                        results.Add(new TableFingerprint(
                            request.Table.Identity,
                            reader.GetInt64(1),
                            reader.GetInt64(2),
                            reader.GetInt64(3)));
                    }
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    internal static string BuildQuery(IReadOnlyList<FingerprintRequest> batch)
    {
        var quoter = SqlServerQuoter.Instance;

        // Separate statements rather than one UNION ALL: a table that cannot be read takes only its own
        // result set down, and the rest of the batch still comes back.
        var statements = batch.Select(request =>
        {
            var columns = request.KeyColumns.Concat(request.Columns)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(quoter.Quote);

            // BINARY_CHECKSUM rather than SHA2_256, and computed once per row via CROSS APPLY rather
            // than twice. Measured: SHA2 evaluated twice per row made a whole-database scan slower than
            // streaming every row to the client, because the cost is server CPU, not the wire.
            // It is weaker than a cryptographic hash, which is the right trade for a screening pass —
            // the exact merge join still runs on whichever table is actually opened.
            var name = request.Identity().Replace("'", "''", StringComparison.Ordinal);

            return $"""
                SELECT N'{name}' AS [t],
                       COUNT_BIG(*) AS [n],
                       CONVERT(bigint, COALESCE(CHECKSUM_AGG(d.c), 0)) AS [x],
                       COALESCE(SUM(CONVERT(bigint, d.c)), 0) AS [s]
                FROM {quoter.Qualify(request.Table.Identity)} AS r
                CROSS APPLY (SELECT BINARY_CHECKSUM({string.Join(", ", columns.Select(c => $"r.{c}"))}) AS c) AS d;
                """;
        });

        return string.Join("\n", statements);
    }

    // Kept for the size heuristic the scan uses to decide what is worth fingerprinting at all.
    internal static string Describe(long bytes) =>
        (bytes / 1024d / 1024d).ToString("F0", CultureInfo.InvariantCulture);
}
