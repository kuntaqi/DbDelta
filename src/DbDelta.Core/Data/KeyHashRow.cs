namespace DbDelta.Core.Data;

// One row reduced to its identity and a digest of everything being compared. The whole point of the
// first pass is that this is all that crosses the wire.
public sealed record KeyHashRow(string Key, string Hash);
