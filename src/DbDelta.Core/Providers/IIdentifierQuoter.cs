using DbDelta.Core.Model;

namespace DbDelta.Core.Providers;

public interface IIdentifierQuoter
{
    string Quote(string identifier);

    string Qualify(ObjectIdentity identity);
}
