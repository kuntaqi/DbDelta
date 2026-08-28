using DbDelta.Core.Model;

namespace DbDelta.Core.Data;

public sealed record CollationFinding(
    ObjectIdentity Table,
    string Column,
    CollationRisk Risk,
    string SourceCollation,
    string TargetCollation,
    string Reason);
