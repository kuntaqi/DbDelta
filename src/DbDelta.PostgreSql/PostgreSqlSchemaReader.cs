using DbDelta.Core.Model;
using DbDelta.Core.Providers;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlSchemaReader : ISchemaReader
{
    public PostgreSqlSchemaReader(string connectionString)
    {
    }

    public Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlSchemaReader)}.{nameof(ReadAsync)}");
}
