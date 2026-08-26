using DbDelta.Core.Comparison;
using DbDelta.Core.Data;
using DbDelta.Core.Model;

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

    // Data enters the plan only for tables picked here. Schema differences are selected for you;
    // moving rows is a heavier decision, so it stays deliberate and per-table.
    public Dictionary<ObjectIdentity, DataSelection> DataSelections { get; } = new();

    // Which tables actually differ is not known until they are compared, so the result is kept for the
    // session rather than recomputed every time the screen opens.
    public Dictionary<string, Contracts.TableScanRow> Scan { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Stable across a session so the SPA can address an object without sending its identity back in
    // pieces, and so a stale id from an old comparison cannot silently resolve against a new one.
    public string IdOf(ObjectIdentity identity) =>
        $"{identity.Type}:{identity.Schema}.{identity.Name}".ToLowerInvariant();

    public ObjectIdentity? Resolve(string id) =>
        Diff.Objects.Select(o => o.Identity).FirstOrDefault(i => IdOf(i) == id);
}
