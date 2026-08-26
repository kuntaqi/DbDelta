namespace DbDelta.Core.Data;

// A screening pass: it answers "does this table differ" without moving row data to the client at all.
// Exact classification still comes from the streaming merge join, on the one table being worked on.
public interface ITableFingerprintReader
{
    Task<IReadOnlyList<TableFingerprint>> ReadAsync(
        IReadOnlyList<FingerprintRequest> tables,
        CancellationToken cancellationToken = default);
}
