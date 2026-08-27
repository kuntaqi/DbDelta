namespace DbDelta.Api.Contracts;

// The cheap half is always filled in; Collation through Problem stay null until the per-database pass runs.
// One shape for both, so the screen does not have to hold two lists and reconcile them.
public sealed record InstanceDatabaseDto(
    string Name,
    string State,
    string RecoveryModel,
    long DataBytes,
    long LogBytes,
    bool IsReadOnly,
    bool Accessible,
    string? Collation,
    int? Tables,
    int? Views,
    int? Routines,
    string? Problem);
