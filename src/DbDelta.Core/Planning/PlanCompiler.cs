namespace DbDelta.Core.Planning;

public static class PlanCompiler
{
    public static CompiledPlan Compile(
        IEnumerable<PlanSelection> selections,
        IEnumerable<ChangeUnit> available,
        IEnumerable<Exclusion>? exclusions = null)
    {
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(available);

        var normalized = PlanNormalizer.Normalize(selections);
        var excluded = (exclusions ?? []).ToList();
        var excludedIds = excluded.Select(e => e.Unit).ToHashSet();

        var units = new List<ChangeUnit>();
        var seen = new HashSet<ChangeUnitId>();
        var hit = new HashSet<ChangeUnitId>();

        foreach (var unit in available)
        {
            if (!normalized.Any(s => s.Covers(unit.Id)))
            {
                continue;
            }

            if (excludedIds.Contains(unit.Id))
            {
                hit.Add(unit.Id);
                continue;
            }

            if (seen.Add(unit.Id))
            {
                units.Add(unit);
            }
        }

        return new CompiledPlan
        {
            Units = units,
            NormalizedSelections = normalized,
            AppliedExclusions = excluded.Where(e => hit.Contains(e.Unit)).ToList()
        };
    }
}
