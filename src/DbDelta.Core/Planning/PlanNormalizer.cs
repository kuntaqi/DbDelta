namespace DbDelta.Core.Planning;

// Collapses the scope lattice Database > Table > Row. A selection whose ancestor is already selected
// is dropped as covered, not merged: the cart holds intents, so widening never leaves narrower
// fragments behind to be emitted twice.
public static class PlanNormalizer
{
    public static IReadOnlyList<PlanSelection> Normalize(IEnumerable<PlanSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);

        var distinct = selections.Distinct().ToList();

        if (distinct.Any(s => s.Scope == SelectionScope.Database))
        {
            return [PlanSelection.Database()];
        }

        var tables = distinct
            .Where(s => s.Scope == SelectionScope.Table)
            .Select(s => s.Object!)
            .ToHashSet();

        return distinct
            .Where(s => s.Scope != SelectionScope.Row || !tables.Contains(s.Object!))
            .ToList();
    }
}
