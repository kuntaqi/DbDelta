using DbDelta.Core.Model;

namespace DbDelta.Core.Comparison;

public sealed class SchemaDiff
{
    public required string SourceDatabase { get; init; }

    public required string TargetDatabase { get; init; }

    public IReadOnlyList<ObjectDiff> Objects { get; init; } = [];

    public IEnumerable<ObjectDiff> Differing => Objects.Where(o => o.HasChanges);

    public int Count(DiffKind kind) => Objects.Count(o => o.Kind == kind);

    public IEnumerable<ObjectDiff> OfType(ObjectType type) =>
        Objects.Where(o => o.Identity.Type == type);

    public ObjectDiff? Find(ObjectIdentity identity) =>
        Objects.FirstOrDefault(o => o.Identity == identity);
}
