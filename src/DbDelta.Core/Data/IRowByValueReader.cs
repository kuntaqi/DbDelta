using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

// Addresses rows by literal column values rather than by the compare's canonical key. Parent closure
// starts from a child's foreign key value, which is a value and not a key the merge join ever produced,
// so the canonical form cannot be rebuilt on this side without reimplementing the server's DATALENGTH
// semantics per type. Asking by value avoids that entirely.
//
// Used both ways: against the target with the key columns alone it answers which parents are already
// there, and against the source with the full column list it fetches the ones that are not.
public interface IRowByValueReader
{
    Task<IReadOnlyList<RowValues>> FetchAsync(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<ParentKey> keys,
        IReadOnlyList<string> columns,
        CancellationToken cancellationToken = default);
}
