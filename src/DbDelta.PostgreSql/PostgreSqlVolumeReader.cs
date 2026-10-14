using DbDelta.Core.Data;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlVolumeReader : IVolumeReader
{
    public PostgreSqlVolumeReader(string connectionString)
    {
    }

    public Task<DatabaseVolume> ReadAsync(CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlVolumeReader)}.{nameof(ReadAsync)}");
}
