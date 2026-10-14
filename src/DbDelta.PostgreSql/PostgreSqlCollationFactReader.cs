using DbDelta.Core.Data;
using DbDelta.Core.Providers;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlCollationFactReader : ICollationFactReader
{
    public PostgreSqlCollationFactReader(string connectionString)
    {
    }

    public Task<IReadOnlyDictionary<string, CollationFact>> ReadAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlCollationFactReader)}.{nameof(ReadAsync)}");
}
