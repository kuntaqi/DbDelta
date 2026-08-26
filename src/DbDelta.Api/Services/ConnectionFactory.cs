using DbDelta.Api.Contracts;
using Microsoft.Data.SqlClient;

namespace DbDelta.Api.Services;

public sealed class ConnectionFactory
{
    public ResolvedConnection Resolve(ConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.IsNullOrWhiteSpace(request.ConnectionString)
            ? FromDetails(request)
            : FromConnectionString(request.ConnectionString);
    }

    private static ResolvedConnection FromConnectionString(string connectionString)
    {
        SqlConnectionStringBuilder builder;

        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"That connection string could not be parsed: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            throw new InvalidOperationException("The connection string has no Server / Data Source.");
        }

        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
        {
            throw new InvalidOperationException(
                "The connection string has no Database / Initial Catalog. DbDelta compares one database at a time, "
                + "so it has to be named explicitly rather than defaulting to whatever the login opens.");
        }

        // Left alone otherwise: a pasted string is the user's, and silently rewriting its options would
        // make it behave differently from the same string used anywhere else.
        if (builder.ConnectTimeout == 15)
        {
            builder.ConnectTimeout = 30;
        }

        return new ResolvedConnection(
            builder.ConnectionString,
            ServerName(builder.DataSource),
            builder.InitialCatalog);
    }

    private static ResolvedConnection FromDetails(ConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Database))
        {
            throw new InvalidOperationException(
                "Give either a connection string, or a server and a database.");
        }

        var windows = !string.Equals(request.Authentication, "SqlLogin", StringComparison.OrdinalIgnoreCase);

        if (!windows && string.IsNullOrWhiteSpace(request.Username))
        {
            throw new InvalidOperationException("SQL Server authentication needs a login name.");
        }

        // A named instance already carries its own path, so a port would contradict it.
        var dataSource = request.Port is > 0 && !request.Server.Contains('\\', StringComparison.Ordinal)
            ? $"{request.Server},{request.Port}"
            : request.Server;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = request.Database,
            IntegratedSecurity = windows,
            TrustServerCertificate = request.TrustServerCertificate,
            ConnectTimeout = 30
        };

        if (!windows)
        {
            builder.UserID = request.Username;
            builder.Password = request.Password ?? string.Empty;
        }

        return new ResolvedConnection(builder.ConnectionString, ServerName(request.Server), request.Database);
    }

    // Strips the port and any protocol prefix so "tcp:HOST,1433" classifies the same as "HOST".
    private static string ServerName(string dataSource)
    {
        var name = dataSource;

        var protocol = name.IndexOf(':', StringComparison.Ordinal);
        if (protocol > 0 && protocol <= 4)
        {
            name = name[(protocol + 1)..];
        }

        var comma = name.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
        {
            name = name[..comma];
        }

        return name.Trim();
    }
}
