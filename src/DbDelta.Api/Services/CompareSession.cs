using DbDelta.Core.Comparison;
using DbDelta.Core.Data;
using DbDelta.Core.Model;
using DbDelta.Core.Planning;

namespace DbDelta.Api.Services;

public sealed class CompareSession
{
    public required string Id { get; init; }

    public required DatabaseSchema Source { get; init; }

    public required DatabaseSchema Target { get; init; }

    public required SchemaDiff Diff { get; init; }

    public required string SourceServer { get; init; }

    public required string TargetServer { get; init; }

    public required string SourceConnectionString { get; init; }

    public required string TargetConnectionString { get; init; }

    public required long DurationMs { get; init; }

    public DateTimeOffset ComparedAt { get; init; }

    // Data enters the plan only for tables picked here. Nothing is selected for you on either side, and
    // moving rows is the heavier decision of the two, so it stays per-table rather than per-database.
    public Dictionary<ObjectIdentity, DataSelection> DataSelections { get; } = new();

    // Which tables actually differ is not known until they are compared, so the result is kept for the
    // session rather than recomputed every time the screen opens.
    public Dictionary<string, Contracts.TableScanRow> Scan { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Nothing reaches the plan without being picked — schema included. A tool that pre-selects the
    // changes it found makes the review step optional in practice.
    //
    // Intents, not a list of objects: one entry can be "this object" or "the whole database". That
    // distinction is what makes an exclusion necessary — under a database-wide intent there is no set to
    // remove an object from, so declining one has to be said rather than left unsaid.
    public List<PlanSelection> Selections { get; } = [];

    // First-class refusals. Kept apart from the selections so widening the scope cannot swallow them.
    public List<Exclusion> Exclusions { get; } = [];

    public CompiledPlan SchemaPlan() =>
        PlanCompiler.Compile(Selections, SchemaChangeUnits.From(Diff), Exclusions);

    public bool Covers(ChangeUnitId unit) =>
        PlanNormalizer.Normalize(Selections).Any(s => s.Covers(unit));

    public bool IsWholeDatabase =>
        PlanNormalizer.Normalize(Selections).Any(s => s.Scope == SelectionScope.Database);

    // Tables without a primary key are not compared on a guess. A key chosen here is verified unique on
    // both sides first, because the merge join is only correct when a key identifies at most one row.
    public Dictionary<ObjectIdentity, IReadOnlyList<string>> KeyOverrides { get; } = new();

    public IReadOnlyList<string> KeyFor(TableDefinition table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return KeyOverrides.TryGetValue(table.Identity, out var chosen)
            ? chosen
            : Core.Data.ColumnSetResolver.DefaultKeyFor(table);
    }

    // Stable across a session so the SPA can address an object without sending its identity back in
    // pieces, and so a stale id from an old comparison cannot silently resolve against a new one.
    public string IdOf(ObjectIdentity identity) =>
        $"{identity.Type}:{identity.Schema}.{identity.Name}".ToLowerInvariant();

    public ObjectIdentity? Resolve(string id) =>
        Diff.Objects.Select(o => o.Identity).FirstOrDefault(i => IdOf(i) == id);
}
