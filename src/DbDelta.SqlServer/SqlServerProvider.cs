using DbDelta.Core.Providers;
using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer;

public sealed class SqlServerProvider : IDatabaseProvider
{
    public string Key => "sqlserver";

    public IIdentifierQuoter Quoter => SqlServerQuoter.Instance;

    public ISchemaReader CreateSchemaReader(string connectionString) =>
        new SqlServerSchemaReader(connectionString);

    public IScriptEmitter CreateScriptEmitter() => new TSqlEmitter();

    public async Task<ServerInfo> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadServerInfoAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ServerInfo> ReadServerInfoAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(CatalogQueries.Probe, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The server did not return its properties.");
        }

        return new ServerInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4));
    }
}
