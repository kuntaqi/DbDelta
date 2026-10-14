using DbDelta.Core.Providers;

namespace DbDelta.Api.Services;

// Picks the provider for an engine at request time. The API used to bind one IDatabaseProvider globally; with
// two engines the choice is per connection, so every registered provider is indexed by its own Key and a
// request's (or session's) engine selects among them. The engine string is normalised first, so null defaults
// to SQL Server and the aliases DatabaseEngine accepts all resolve.
public interface IDatabaseProviderFactory
{
    IDatabaseProvider For(string? engine);
}

public sealed class DatabaseProviderFactory : IDatabaseProviderFactory
{
    private readonly IReadOnlyDictionary<string, IDatabaseProvider> _byKey;

    public DatabaseProviderFactory(IEnumerable<IDatabaseProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _byKey = providers.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IDatabaseProvider For(string? engine)
    {
        var key = DatabaseEngine.Normalize(engine);
        return _byKey.TryGetValue(key, out var provider)
            ? provider
            : throw new InvalidOperationException($"No provider is registered for engine '{key}'.");
    }
}
