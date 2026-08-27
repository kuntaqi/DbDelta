namespace DbDelta.Core.Model;

// What one query against server-level catalogs can say about a database without opening it. Everything
// here is free: sizes come from the server's own file metadata, not from counting anything.
//
// Collation is deliberately absent even though it is the property this tool cares most about, because it
// cannot be had at this price — see DatabaseDetail.
public sealed record DatabaseSummary(
    string Name,
    string State,
    string RecoveryModel,
    long DataBytes,
    long LogBytes,
    bool IsReadOnly,
    bool Accessible);
