using DbDelta.Api.Contracts;
using Microsoft.Data.SqlClient;

namespace DbDelta.Api.Services;

// Taking a connection apart into the half that is not a secret. This was inside ProfileStore.Build and is
// out here because the recently-compared list needs exactly the same thing — and a second implementation of
// "which parts of a connection are safe to write down" is the last thing this should have.
//
// A pasted connection string is the case that matters. It can carry a password, so it is parsed and the
// pieces are taken individually; refusing to accept one at all would be safe and needlessly unhelpful.
public static class ConnectionEndpoint
{
    public static ComparedEndpoint From(
        string? connectionString,
        string? server,
        int? port,
        string? database,
        string? authentication,
        string? username,
        bool trustServerCertificate)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var (host, parsedPort) = Split(builder.DataSource);

            return new ComparedEndpoint(
                host,
                parsedPort,
                builder.InitialCatalog,
                builder.IntegratedSecurity ? "Windows" : "SqlLogin",
                builder.IntegratedSecurity ? null : NullIfEmpty(builder.UserID),
                builder.TrustServerCertificate);
        }

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException(
                "A connection needs at least a server and a database to be worth remembering.");
        }

        var sqlLogin = string.Equals(authentication, "SqlLogin", StringComparison.OrdinalIgnoreCase);

        return new ComparedEndpoint(
            server.Trim(),
            port,
            database.Trim(),
            sqlLogin ? "SqlLogin" : "Windows",
            sqlLogin ? NullIfEmpty(username) : null,
            trustServerCertificate);
    }

    public static ComparedEndpoint From(ConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return From(
            request.ConnectionString,
            request.Server,
            request.Port,
            request.Database,
            request.Authentication,
            request.Username,
            request.TrustServerCertificate);
    }

    // "HOST,1433" carries its port in the data source; a named instance carries a path instead and must
    // keep it whole.
    private static (string Server, int? Port) Split(string dataSource)
    {
        var comma = dataSource.LastIndexOf(',');

        if (comma > 0 && int.TryParse(dataSource[(comma + 1)..], out var port))
        {
            return (dataSource[..comma].Trim(), port);
        }

        return (dataSource.Trim(), null);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
