using DbDelta.Core.Model;

namespace DbDelta.SqlServer;

// Which indexes this emitter can write, and what to say about the ones it cannot.
//
// The rule this enforces: writing a different kind of index than the source has is worse than writing
// none. A spatial index emitted as CREATE INDEX does not merely lose the tessellation — it fails with
// error 1978 and takes the whole transaction with it, so one index nobody was thinking about made a
// database impossible to provision. Refusing that one index and saying so keeps the rest applicable,
// which is the same posture as a keyless table waiting for a key.
internal static class IndexEmitSupport
{
    public static string? Refusal(ObjectIdentity table, IndexDefinition index)
    {
        var name = $"{index.Name} on {table.QualifiedName}";

        switch (index.Kind)
        {
            // A hash index exists only inside a memory-optimized table's own CREATE TABLE — there is no
            // statement that adds one afterwards — and this tool does not write memory-optimized tables,
            // so the index is not the whole of what would be missing.
            case IndexKind.Hash:
                return $"{name} is a hash index on a memory-optimized table. It can only be declared "
                    + "inside CREATE TABLE, which this tool does not write for memory-optimized tables, so "
                    + "nothing is emitted for it.";

            case IndexKind.Spatial when index.Extras[SpatialExtras.Scheme] is null:
                return $"{name} is a spatial index whose tessellation scheme could not be read, and the "
                    + "scheme is not optional in the DDL. Nothing is emitted for it rather than a guess.";

            case IndexKind.Spatial when index.Columns.Count == 0:
            case IndexKind.Xml when index.Columns.Count == 0:
                return $"{name} names no column in the catalog, which should not happen for its kind. "
                    + "Nothing is emitted for it.";

            case IndexKind.Xml when IsSelective(index):
                return $"{name} is a selective XML index. Its promoted paths are a separate catalog this "
                    + "tool does not read, so nothing is emitted for it.";

            case IndexKind.Xml when IsSecondary(index) && index.Extras[XmlExtras.PrimaryIndex] is null:
                return $"{name} is a secondary XML index whose primary index could not be resolved. "
                    + "Nothing is emitted for it, since the statement has to name that primary.";

            case IndexKind.Columnstore when !index.IsClustered && index.Columns.Count == 0:
                return $"{name} is a nonclustered columnstore index with no columns in the catalog. "
                    + "Nothing is emitted for it.";

            default:
                return null;
        }
    }

    public static bool IsSecondary(IndexDefinition index) =>
        index.Kind == IndexKind.Xml
        && string.Equals(index.Extras[XmlExtras.Kind], XmlExtras.Secondary, StringComparison.OrdinalIgnoreCase);

    // A selective XML index reports its own type description rather than PRIMARY_XML, and takes a
    // different statement with a WITH XMLNAMESPACES path list.
    private static bool IsSelective(IndexDefinition index) =>
        index.Extras[XmlExtras.Kind] is { } kind
        && kind.Contains("SELECTIVE", StringComparison.OrdinalIgnoreCase);
}
