using DbDelta.Core.Model;

namespace DbDelta.Core.Comparison;

public sealed class ObjectDiff
{
    public required ObjectIdentity Identity { get; init; }

    public required DiffKind Kind { get; init; }

    public IReadOnlyList<PropertyDiff> Properties { get; init; } = [];

    public IReadOnlyList<ObjectDiff> Children { get; init; } = [];

    public bool HasChanges => Kind != DiffKind.Same;

    public IEnumerable<ObjectDiff> DifferingChildren => Children.Where(c => c.HasChanges);

    public override string ToString() => $"{Kind} {Identity}";
}
