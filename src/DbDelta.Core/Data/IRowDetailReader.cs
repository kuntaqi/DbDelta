using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

public interface IRowDetailReader
{
    Task<IReadOnlyList<RowValues>> FetchAsync(
        TableDefinition table,
        DataCompareRequest request,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);
}
