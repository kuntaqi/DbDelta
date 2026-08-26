namespace DbDelta.Core.Model;

// Engine-specific attributes the reader captured but the comparer has no opinion about (filegroup,
// fill factor, compression). Kept so a provider round-trip loses nothing.
public sealed class ProviderExtras
{
    public static readonly ProviderExtras Empty = new(new Dictionary<string, string?>());

    private readonly IReadOnlyDictionary<string, string?> _values;

    public ProviderExtras(IReadOnlyDictionary<string, string?> values) => _values = values;

    public int Count => _values.Count;

    public IEnumerable<string> Keys => _values.Keys;

    public string? this[string key] => _values.TryGetValue(key, out var v) ? v : null;

    public bool TryGet(string key, out string? value) => _values.TryGetValue(key, out value);
}
