using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlRowDetailReader : IRowDetailReader
{
    public PostgreSqlRowDetailReader(string connectionString)
    {
    }

    public Task<IReadOnlyList<RowValues>> FetchAsync(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlRowDetailReader)}.{nameof(FetchAsync)}");
}
