namespace DbDelta.Core.Data;

// TargetHash is the digest the target row carried at the moment of the compare, and null when the target
// had no such row. It is kept so the plan can be re-checked later against the same rows: a row that moved
// between review and apply is the case a schema-only drift check cannot see.
public sealed record RowDifference(string Key, RowClassification Classification, string? TargetHash = null);
