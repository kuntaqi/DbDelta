using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

// One foreign key's worth of demand: the parent rows a set of child rows points at. Grouped per key
// rather than per row because the lookup that answers "which of these are already on the target" is
// one query for the whole set, not one per row.
public sealed record ParentReference(
    ObjectIdentity Child,
    string ForeignKeyName,
    ObjectIdentity Parent,
    IReadOnlyList<string> ParentColumns,
    IReadOnlyList<ParentKey> Keys);
