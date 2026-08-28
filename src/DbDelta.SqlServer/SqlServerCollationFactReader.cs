using DbDelta.Core.Data;
using DbDelta.Core.Providers;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerCollationFactReader : ICollationFactReader
{
    private readonly string _connectionString;

    public SqlServerCollationFactReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<IReadOnlyDictionary<string, CollationFact>> ReadAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        var wanted = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var facts = new Dictionary<string, CollationFact>(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            return facts;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new SqlCommand(Query(wanted.Count), connection);

        for (var i = 0; i < wanted.Count; i++)
        {
            command.Parameters.AddWithValue($"@n{i}", wanted[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(0);

            facts[name] = new CollationFact(
                name,
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2));
        }

        // A name the server does not know returns NULL from COLLATIONPROPERTY rather than an error, and a
        // name it does not return at all would silently go missing. Both end up here as unresolved, which
        // the precondition treats as risky rather than as fine.
        foreach (var name in wanted.Where(n => !facts.ContainsKey(n)))
        {
            facts[name] = CollationFact.Unresolved(name);
        }

        return facts;
    }

    // COLLATIONPROPERTY is a scalar function, so this is a UNION of one-row selects rather than a lookup
    // against a table. Parameterised: a collation name is user-supplied text like any other.
    private static string Query(int count) =>
        string.Join(
            "\nUNION ALL ",
            Enumerable.Range(0, count).Select(i =>
                $"SELECT @n{i} AS [Name], "
                + $"CONVERT(int, COLLATIONPROPERTY(@n{i}, 'CodePage')) AS [CodePage], "
                + $"CONVERT(int, COLLATIONPROPERTY(@n{i}, 'ComparisonStyle')) AS [ComparisonStyle]"));
}
