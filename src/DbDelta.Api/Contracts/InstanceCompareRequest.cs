namespace DbDelta.Api.Contracts;

// Describe is the expensive half and therefore opt-in: matching two database lists is two queries, while
// describing what is in them costs a connection per database per side.
public sealed record InstanceCompareRequest(
    ConnectionRequest Source,
    ConnectionRequest Target,
    IReadOnlyList<DatabasePairingDto>? Pairings = null,
    bool Describe = false);
