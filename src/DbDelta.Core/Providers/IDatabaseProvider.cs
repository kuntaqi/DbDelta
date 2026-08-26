namespace DbDelta.Core.Providers;

public interface IDatabaseProvider
{
    string Key { get; }

    IIdentifierQuoter Quoter { get; }

    ISchemaReader CreateSchemaReader(string connectionString);

    Task<ServerInfo> ProbeAsync(string connectionString, CancellationToken cancellationToken = default);
}
