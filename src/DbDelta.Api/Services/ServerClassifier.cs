using DbDelta.Api.Contracts;
using Microsoft.Extensions.Options;

namespace DbDelta.Api.Services;

// Environment is derived from the server name through configuration rather than chosen in the UI, so
// a production server cannot be left unlabelled by someone in a hurry.
public sealed class ServerClassifier
{
    private readonly SafetyOptions _options;

    public ServerClassifier(IOptions<SafetyOptions> options) => _options = options.Value;

    public EnvironmentClass Classify(string server)
    {
        ArgumentNullException.ThrowIfNull(server);

        foreach (var (name, patterns) in _options.Environments)
        {
            if (patterns.Any(p => Matches(server, p)) && Enum.TryParse<EnvironmentClass>(name, true, out var parsed))
            {
                return parsed;
            }
        }

        return EnvironmentClass.Unknown;
    }

    public bool IsReadOnly(string server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return _options.ReadOnlyServers.Any(p => Matches(server, p))
            || Classify(server) == EnvironmentClass.Prod;
    }

    private static bool Matches(string value, string pattern)
    {
        if (pattern.EndsWith('*'))
        {
            return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.StartsWith('*'))
        {
            return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}
