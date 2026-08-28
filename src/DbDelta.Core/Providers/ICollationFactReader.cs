using DbDelta.Core.Data;

namespace DbDelta.Core.Providers;

public interface ICollationFactReader
{
    // By name, in one round trip. A schema has a handful of distinct collations however many columns it
    // has, so this is asked once per comparison rather than once per column.
    Task<IReadOnlyDictionary<string, CollationFact>> ReadAsync(
        IEnumerable<string> names,
        CancellationToken cancellationToken = default);
}
