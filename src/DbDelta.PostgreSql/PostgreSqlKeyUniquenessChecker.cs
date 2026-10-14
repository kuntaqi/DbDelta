using DbDelta.Core.Data;
using DbDelta.Core.Model;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlKeyUniquenessChecker : IKeyUniquenessChecker
{
    public PostgreSqlKeyUniquenessChecker(string connectionString)
    {
    }

    public Task<TableUniquenessProfile> ProfileAsync(
        TableDefinition table,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlKeyUniquenessChecker)}.{nameof(ProfileAsync)}");

    public Task<KeyUniqueness> CheckAsync(
        TableDefinition table,
        IReadOnlyList<string> keyColumns,
        CancellationToken cancellationToken = default) =>
        throw StubGap.For($"{nameof(PostgreSqlKeyUniquenessChecker)}.{nameof(CheckAsync)}");
}
