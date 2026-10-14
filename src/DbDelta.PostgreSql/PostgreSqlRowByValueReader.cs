using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlRowByValueReader : IRowByValueReader
{
    public PostgreSqlRowByValueReader(string connectionString)
    {
    }

    public Task<IReadOnlyList<RowValues>> FetchAsync(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<ParentKey> keys,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlRowByValueReader)}.{nameof(FetchAsync)}");
}
