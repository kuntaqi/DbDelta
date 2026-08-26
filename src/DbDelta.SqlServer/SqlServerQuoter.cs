using DbDelta.Core.Model;
using DbDelta.Core.Providers;

namespace DbDelta.SqlServer;

public sealed class SqlServerQuoter : IIdentifierQuoter
{
    public static readonly SqlServerQuoter Instance = new();

    public string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    public string Qualify(ObjectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"{Quote(identity.Schema)}.{Quote(identity.Name)}";
    }
}
