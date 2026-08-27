using DbDelta.Api.Contracts;
using DbDelta.Core.Planning;

namespace DbDelta.Api.Services;

// Everything that decides what is in the schema half of the plan. Split out from CompareService because
// this is where the cart's rules live — scope, exclusions, and what closure is allowed to add — and none
// of them touch a database, which is what lets them be tested directly.
public static class SchemaSelectionService
{
    public static SchemaSelectionResponse Select(CompareSession session, SchemaSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        var identity = session.Resolve(request.ObjectId)
            ?? throw new InvalidOperationException($"'{request.ObjectId}' is not part of this comparison.");

        var diff = session.Diff.Find(identity);
        var unit = diff is null || !diff.HasChanges ? null : SchemaChangeUnits.IdOf(diff);

        if (unit is null)
        {
            throw new InvalidOperationException($"{identity.QualifiedName} has nothing to sync.");
        }

        if (request.Selected)
        {
            // Ticking is first a revocation: an object left out under a database-wide plan is held out by
            // an exclusion, and removing that is what puts it back.
            session.Exclusions.RemoveAll(e => e.Unit == unit);

            if (!session.Covers(unit))
            {
                session.Selections.Add(PlanSelection.Table(identity));
            }
        }
        else
        {
            session.Selections.RemoveAll(s => s.Scope == SelectionScope.Table && s.Object == identity);

            // Still covered after dropping the explicit pick means the cover is the database-wide intent.
            // There is no list to take it off, so the only way to say no is to say it.
            if (session.Covers(unit) && !session.Exclusions.Any(e => e.Unit == unit))
            {
                session.Exclusions.Add(new Exclusion(unit, SchemaChangeUnits.Describe(unit)));
            }
        }

        return Describe(session);
    }

    // Two scopes, and the difference between them is the whole point. "Picked items" is a list. "Entire
    // database" is a standing intent that also covers what has not been looked at.
    public static SchemaSelectionResponse SetScope(CompareSession session, string scope)
    {
        ArgumentNullException.ThrowIfNull(session);

        var wholeDatabase = string.Equals(scope, "Database", StringComparison.OrdinalIgnoreCase);

        // Narrowing keeps what is effectively in the plan, as explicit picks: nothing is lost, and the
        // exclusions become unnecessary because under a list an object left out is simply not on it.
        var effective = wholeDatabase
            ? []
            : session.SchemaPlan().Units.Select(u => u.Id.Object).Distinct().ToList();

        session.Selections.Clear();
        session.Exclusions.Clear();

        if (wholeDatabase)
        {
            session.Selections.Add(PlanSelection.Database());
        }
        else
        {
            session.Selections.AddRange(effective.Select(PlanSelection.Table));
        }

        return Describe(session);
    }

    public static SchemaSelectionResponse Clear(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.Selections.Clear();
        session.Exclusions.Clear();

        return Describe(session);
    }

    public static SchemaSelectionResponse Describe(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var plan = session.SchemaPlan();
        var closure = Closure(session);

        return new SchemaSelectionResponse(
            plan.Units.Select(u => session.IdOf(u.Id.Object)).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            session.Diff.Differing.Count(),
            session.DataSelections.Count,
            closure.Required.Select(r => Required(session, r)).ToList(),
            [.. closure.Unsatisfiable, .. closure.Blocked.Select(Conflict)],
            session.IsWholeDatabase ? "Database" : "Picked",
            Exclusions(session));
    }

    // Recomputed on every read rather than kept on the session. Prerequisites are a consequence of the
    // ticks, so storing them would let one survive the untick of the only object that wanted it.
    //
    // Exclusions go in as blocked: closure adds what the plan needs, but a refusal already given is not
    // something it gets to reverse.
    public static ClosureResult Closure(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return SchemaClosure.Expand(
            session.Source,
            session.Target,
            session.Diff,
            session.SchemaPlan().Units.Select(u => u.Id.Object).Distinct(),
            session.Exclusions.Select(e => e.Unit.Object).ToHashSet());
    }

    public static IReadOnlyList<ExcludedObjectDto> Exclusions(CompareSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.Exclusions
            .Select(e => new ExcludedObjectDto(
                session.IdOf(e.Unit.Object),
                e.Unit.Object.QualifiedName,
                e.Unit.Kind.ToString(),
                e.Reason))
            .ToList();
    }

    public static RequiredObjectDto Required(CompareSession session, RequiredObject required)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(required);

        return new RequiredObjectDto(
            session.IdOf(required.Identity),
            required.Identity.Type.ToString(),
            required.Identity.QualifiedName,
            required.RequiredBy.QualifiedName,
            required.Reason);
    }

    public static string Conflict(RequiredObject blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);

        return $"{blocked.RequiredBy.QualifiedName} needs {blocked.Identity.QualifiedName} "
            + $"({blocked.Reason}), but {blocked.Identity.QualifiedName} is excluded. The exclusion stands "
            + "and the script will fail on that reference — revoke it, or take out what needs it.";
    }
}
