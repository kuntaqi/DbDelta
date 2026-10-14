using DbDelta.Core.Data;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlFingerprintReader : ITableFingerprintReader
{
    public PostgreSqlFingerprintReader(string connectionString)
    {
    }

    public Task<IReadOnlyList<TableFingerprint>> ReadAsync(
        IReadOnlyList<FingerprintRequest> tables,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlFingerprintReader)}.{nameof(ReadAsync)}");
}
