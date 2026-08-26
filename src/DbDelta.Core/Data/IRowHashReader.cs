using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

public interface IRowHashReader
{
    IAsyncEnumerable<KeyHashRow> StreamAsync(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> comparedColumns,
        RowSetSide side,
        CancellationToken cancellationToken = default);
}
