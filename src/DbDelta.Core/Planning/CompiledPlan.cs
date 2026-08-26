namespace DbDelta.Core.Planning;

public sealed class CompiledPlan
{
    public required IReadOnlyList<ChangeUnit> Units { get; init; }

    public required IReadOnlyList<PlanSelection> NormalizedSelections { get; init; }

    public required IReadOnlyList<Exclusion> AppliedExclusions { get; init; }

    public int Count => Units.Count;

    public bool IsEmpty => Units.Count == 0;

    public IEnumerable<ChangeUnit> RowChanges => Units.Where(u => u.Id.IsRowChange);

    public IEnumerable<ChangeUnit> SchemaChanges => Units.Where(u => !u.Id.IsRowChange);

    public int CountOf(ChangeUnitKind kind) => Units.Count(u => u.Id.Kind == kind);
}
