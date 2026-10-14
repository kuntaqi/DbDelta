using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlRowHashReader : IRowHashReader
{
    public PostgreSqlRowHashReader(string connectionString)
    {
    }

    public IAsyncEnumerable<KeyHashRow> StreamAsync(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> comparedColumns,
        RowSetSide side,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlRowHashReader)}.{nameof(StreamAsync)}");
}
