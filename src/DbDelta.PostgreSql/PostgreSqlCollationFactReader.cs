using DbDelta.Core.Data;
using DbDelta.Core.Providers;

namespace DbDelta.PostgreSql;

// CollationFact is shaped for SQL Server — a Windows code page and a comparison-style bitmask — and PostgreSQL
// expresses collation sensitivity differently (ICU locale attributes, libc locales, a determinism flag), with
// no faithful mapping onto those two fields. Rather than invent a code page it does not have, this returns each
// name as Unresolved: the collation precondition then treats a PostgreSQL collation as "sensitivity unknown"
// rather than claiming a sameness it has not checked. Modelling PostgreSQL collation sensitivity properly is a
// separate task; this keeps a PostgreSQL compare from stalling on the one reader it still lacked.
public sealed class PostgreSqlCollationFactReader : ICollationFactReader
{
    public PostgreSqlCollationFactReader(string connectionString)
    {
    }

    public Task<IReadOnlyDictionary<string, CollationFact>> ReadAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        IReadOnlyDictionary<string, CollationFact> facts = names
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(n => n, CollationFact.Unresolved, StringComparer.Ordinal);

        return Task.FromResult(facts);
    }
}
