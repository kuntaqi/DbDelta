using DbDelta.Core.Model;

namespace DbDelta.Core.Providers;

public interface ISchemaReader
{
    Task<DatabaseSchema> ReadAsync(CancellationToken cancellationToken = default);
}
