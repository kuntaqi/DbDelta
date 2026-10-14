using DbDelta.Core.Model;
using DbDelta.Core.Providers;

namespace DbDelta.PostgreSql;

public sealed class PostgreSqlQuoter : IIdentifierQuoter
{
    public static readonly PostgreSqlQuoter Instance = new();

    // PostgreSQL quotes with double quotes and escapes an embedded one by doubling it. Unquoted identifiers
    // fold to lower case, so quoting is also what preserves the catalog's casing. This member is implemented
    // for real — it is pure, needs no connection, and gives the proof one behaviour that is genuinely a
    // second engine's rather than a throw.
    public string Quote(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public string Qualify(ObjectIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"{Quote(identity.Schema)}.{Quote(identity.Name)}";
    }
}
