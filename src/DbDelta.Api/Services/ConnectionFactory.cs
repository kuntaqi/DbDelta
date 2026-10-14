using DbDelta.Api.Contracts;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDelta.Api.Services;

// Builds and parses connection strings per engine. SQL Server uses SqlConnectionStringBuilder and its
// conventions (named-instance '\', integrated security, HOST,PORT); PostgreSQL uses NpgsqlConnectionStringBuilder
// (host/port/database/username/password, no integrated security, default port 5432). The engine the request
// names decides which path a connection takes; everything downstream sees the same ResolvedConnection.
public sealed class ConnectionFactory
{
    public ResolvedConnection Resolve(ConnectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var engine = DatabaseEngine.Normalize(request.Engine);

        return string.IsNullOrWhiteSpace(request.ConnectionString)
            ? FromDetails(request, engine)
            : FromConnectionString(request.ConnectionString, engine);
    }

    // Repoints the same credentials at another database on the same server — used when surveying every database
    // on an instance. Engine-specific because the catalog key differs (InitialCatalog vs Database).
    public string Retarget(string connectionString, string database, string? engine)
    {
        if (DatabaseEngine.Normalize(engine) == DatabaseEngine.PostgreSql)
        {
            return new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
        }

        return new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;
    }

    private static ResolvedConnection FromConnectionString(string connectionString, string engine) =>
        engine == DatabaseEngine.PostgreSql
            ? FromPostgresConnectionString(connectionString)
            : FromSqlServerConnectionString(connectionString);

    private static ResolvedConnection FromDetails(ConnectionRequest request, string engine) =>
        engine == DatabaseEngine.PostgreSql
            ? FromPostgresDetails(request)
            : FromSqlServerDetails(request);

    private static ResolvedConnection FromSqlServerConnectionString(string connectionString)
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

        return new ResolvedConnection(builder.ConnectionString, ServerName(builder.DataSource), builder.InitialCatalog);
    }

    private static ResolvedConnection FromSqlServerDetails(ConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Database))
        {
            throw new InvalidOperationException("Give either a connection string, or a server and a database.");
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
            ConnectTimeout = 30,
        };

        if (!windows)
        {
            builder.UserID = request.Username;
            builder.Password = request.Password ?? string.Empty;
        }

        return new ResolvedConnection(builder.ConnectionString, ServerName(request.Server), request.Database);
    }

    private static ResolvedConnection FromPostgresConnectionString(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;

        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"That connection string could not be parsed: {ex.Message}", ex);
        }

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new InvalidOperationException("The connection string has no Host.");
        }

        if (string.IsNullOrWhiteSpace(builder.Database))
        {
            throw new InvalidOperationException(
                "The connection string has no Database. DbDelta compares one database at a time, so it has to be "
                + "named explicitly rather than defaulting to the login's own.");
        }

        return new ResolvedConnection(builder.ConnectionString, builder.Host, builder.Database);
    }

    private static ResolvedConnection FromPostgresDetails(ConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server) || string.IsNullOrWhiteSpace(request.Database))
        {
            throw new InvalidOperationException("Give either a connection string, or a server and a database.");
        }

        // PostgreSQL has no integrated-security concept; a login is given by username (and usually a password).
        // Npgsql 10 dropped TrustServerCertificate (SSL is negotiated by SslMode), so it is not set here.
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = request.Server,
            Database = request.Database,
            Timeout = 30,
        };

        if (request.Port is > 0)
        {
            builder.Port = request.Port.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            builder.Username = request.Username;
            builder.Password = request.Password;
        }

        return new ResolvedConnection(builder.ConnectionString, request.Server, request.Database);
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
